using ChronoTerm.Config;
using ChronoTerm.Input;
using ChronoTerm.Pty;
using ChronoTerm.Rendering;
using ChronoTerm.Terminal;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace ChronoTerm.App;

/// <summary>
/// Ports main.py's window/GL-context setup and event loop, now config-driven
/// (window size/padding/titlebar, font path/size/spacing, colors, cursor blink)
/// instead of hardcoded. Post-fx passes from renderer.py (blur, burn-in, glow,
/// composite) are still NOT ported — config.effects.* round-trips through
/// load/save but nothing consumes it yet.
/// </summary>
public sealed class TerminalWindow : IDisposable
{
    private readonly IPty _pty;
    private readonly TerminalSession _session;
    private readonly IWindow _window;
    private readonly ChronoTermConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ChronoTerm.Settings.SettingsOverlay _settingsOverlay;
    private GL? _gl;
    private IInputContext? _input;
    private TerminalGlRenderer? _renderer;

    // Text selection — mirrors main.py's selection_start/selection_end globals.
    // (col, row) in terminal cells, not pixels. No modifier requirement to start
    // a drag (matches main.py: plain left-click-drag selects).
    private (int col, int row)? _selectionStart;
    private (int col, int row)? _selectionEnd;
    private bool _selecting;

    // Cached every frame in OnRender — copy (Ctrl+Shift+C) reads from exactly
    // this, not live screen state, so a copy made while scrolled into history
    // grabs history text. Mirrors main.py's last_rendered_lines.
    private string[]? _lastSnapshot;

    // Placeholder cell metrics used only before the renderer/font atlas exists
    // (constructor needs *some* initial terminal size before OnLoad runs).
    // Overwritten with real glyph metrics as soon as the renderer is built.
    private const int CellWidthPx = 9;
    private const int CellHeightPx = 18;

    // Set from the pty-reader background thread when the shell exits cleanly
    // (e.g. "exit"/"quit" typed at the prompt) — checked in OnRender (main
    // thread) rather than calling _window.Close() directly from that thread.
    private volatile bool _shellExited;

    // Cursor blink state — config.cursor.blink toggles this; ~530ms matches the
    // common terminal-emulator default blink rate (xterm et al).
    private double _blinkAccumulator;
    private bool _blinkOn = true;
    private const double BlinkIntervalSeconds = 0.53;

    // Scrollbar — tracked continuously (not just during selection-drag) so
    // "auto" mode can show it on mouseover even without a click. See
    // ScrollbarOverlay.cs for the actual thumb/track math.
    private int _mouseCol, _mouseRow;
    private ScrollbarInfo _lastScrollbarInfo;
    private bool _draggingScrollbar;
    private float _scrollbarDragStartY;
    private int _scrollbarDragStartOffset;

    // Window chrome move/resize by mouse — GLFW gives up its own OS-drawn
    // titlebar (and, with it, the OS's drag-to-move and edge-resize handles)
    // as soon as WindowBorder.Hidden is set, and has no portable hit-test/
    // "draggable region" API to get an equivalent back. So when the titlebar
    // is hidden, Alt+Left-drag (anywhere in the window) moves it and
    // Alt+Right-drag resizes it, entirely app-side. Left unconditional on
    // HideTitlebar — harmless, and doubles as a fallback on WMs that don't
    // give Alt+drag for free even with a titlebar present.
    private bool _altHeld;
    private bool _draggingWindowMove;
    private bool _draggingWindowResize;
    private Vector2D<int> _dragStartWindowPos;
    private Vector2D<int> _dragStartWindowSize;
    private System.Numerics.Vector2 _dragStartMouseLocal;
    private const int MinWindowWidth = 240;
    private const int MinWindowHeight = 160;

    public TerminalWindow(IPty pty, ChronoTermConfig config, ConfigManager configManager)
    {
        _pty = pty;
        _config = config;
        _configManager = configManager;

        int initialWidth = Math.Max(1, config.Window.Width);
        int initialHeight = Math.Max(1, config.Window.Height);
        int initialCols = Math.Max(1, initialWidth / CellWidthPx);
        int initialRows = Math.Max(1, initialHeight / CellHeightPx);
        _session = new TerminalSession(initialCols, initialRows);

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(initialWidth, initialHeight),
            Title = "ChronoTerm",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
            VSync = true,
            WindowBorder = config.Window.HideTitlebar ? WindowBorder.Hidden : WindowBorder.Resizable,
            // corner_radius from config isn't applied — GLFW has no portable rounded-
            // corner hint; would need per-platform window-manager tricks (KWin/Mutter
            // hints on Linux, DWM APIs on Windows) that aren't worth it yet.
            TransparentFramebuffer = true,
        };

        // Created before SettingsOverlay (below) specifically so the
        // onHideTitlebarChanged callback closes over an already-assigned
        // _window — doing it in the other order compiles fine (the lambda
        // only runs later, well after the constructor returns) but trips
        // CS8602 since nullable's definite-assignment check doesn't know
        // that.
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;
        // Guards against a "stuck" Alt if focus is lost mid-hold (e.g. an
        // OS-level Alt+Tab away from the window) — without this, releasing
        // Alt over a different window would never reach our KeyUp handler
        // and the next plain click would be misread as a chrome drag.
        _window.FocusChanged += focused =>
        {
            if (focused) return;
            _altHeld = false;
            _draggingWindowMove = false;
            _draggingWindowResize = false;
        };

        _settingsOverlay = new ChronoTerm.Settings.SettingsOverlay(config, configManager,
            onHideTitlebarChanged: hide => _window.WindowBorder = hide ? WindowBorder.Hidden : WindowBorder.Resizable,
            onWindowSettingsChanged: () =>
            {
                // Called from three places now: Load Config, and directly
                // from the Width/Height/Padding sliders on every drag tick —
                // titlebar and window size aren't read live anywhere else
                // (unlike colors/effects/cursor-blink, which every render
                // call already pulls fresh from _config), so they need this
                // explicit nudge instead.
                _window.WindowBorder = _config.Window.HideTitlebar ? WindowBorder.Hidden : WindowBorder.Resizable;
                // Setting Size (when it actually differs) triggers GLFW's
                // FramebufferResize callback, which already does everything
                // else a resize needs — viewport, renderer resize, padding,
                // and recomputing the PTY grid to fit — so nothing further is
                // required here even though the preset's own terminal.rows/
                // cols aren't consulted directly (see TerminalConfig's doc
                // comment on why).
                _window.Size = new Vector2D<int>(Math.Max(1, _config.Window.Width), Math.Max(1, _config.Window.Height));
                // Setting Size is a no-op (no resize event fires) when the
                // loaded preset's dimensions happen to match the window's
                // current size exactly — call this directly too so a
                // padding-only change still refreshes even in that case.
                ApplyPadding();
            });
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();

        _renderer = new TerminalGlRenderer(
            _gl, _config.Font.Path, _config.Font.Size,
            _config.Font.CharSpacing, _config.Font.LineSpacing);
        ApplyPadding();
        _renderer.Resize(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        ChronoTerm.Clipboard.Initialize(_window);

        // Real glyph metrics now exist — reconcile terminal size (it was set from
        // the CellWidthPx/CellHeightPx placeholder in the constructor).
        ResizeToFramebuffer(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        _input = _window.CreateInput();
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnMouseScroll;
        }

        _session.StartReading(
            _pty,
            onError: ex => Console.WriteLine($"[pty] read loop ended: {ex.Message}"),
            onExit: () =>
            {
                Console.WriteLine("[app] shell exited — closing window");
                _shellExited = true;
            });
    }

    // Software key-repeat: GLFW's own docs say its native REPEAT action
    // "should not be relied upon" for anything beyond basic text input, and
    // in practice Silk.NET's KeyDown here only fires once per physical press
    // for non-character keys — Backspace/Delete/arrows/etc via KeyTranslator,
    // and the settings overlay's arrow-key nudge. OnKeyChar's plain-character
    // repeat rides the OS's separate text-input mechanism and already works
    // fine; this is specifically for everything else. Only one key repeats
    // at a time, matching standard text-editor/terminal behavior (and GLFW's
    // own "at most one key is repeated" caveat) — not trying to support
    // simultaneous multi-key repeat.
    private Key? _repeatingKey;
    private KeyModifiers _repeatingMods;
    private RepeatTarget _repeatTarget;
    private double _repeatElapsed;
    private double _repeatAccumulator;
    private const double RepeatInitialDelay = 0.45;
    private const double RepeatInterval = 0.035;

    /// <summary>What a held key should keep doing once TickKeyRepeat fires —
    /// distinct from just "is the overlay open" since the overlay's normal
    /// slider panel and its font-browser sub-mode interpret Up/Down
    /// completely differently (nudge a value vs. move a list selection).</summary>
    private enum RepeatTarget { None, Pty, OverlaySlider, Browser }

    private void ArmRepeat(Key key, KeyModifiers mods, RepeatTarget target)
    {
        _repeatingKey = key;
        _repeatingMods = mods;
        _repeatTarget = target;
        _repeatElapsed = 0;
        _repeatAccumulator = 0;
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        if (key is Key.AltLeft or Key.AltRight) _altHeld = false;
        if (_repeatingKey == key) _repeatingKey = null;
    }

    /// <summary>Call once per frame with the frame's deltaSeconds. Fires the
    /// same action OnKeyDown would have, at a steady interval, once the key's
    /// been held past the initial delay.</summary>
    private void TickKeyRepeat(double deltaSeconds)
    {
        if (_repeatingKey is not { } key) return;

        _repeatElapsed += deltaSeconds;
        if (_repeatElapsed < RepeatInitialDelay) return;

        _repeatAccumulator += deltaSeconds;
        if (_repeatAccumulator < RepeatInterval) return;
        _repeatAccumulator -= RepeatInterval;

        if (_repeatTarget == RepeatTarget.OverlaySlider)
        {
            if (!_settingsOverlay.IsOpen) { _repeatingKey = null; return; } // overlay closed mid-hold
            if (key is Key.Right or Key.Up) _settingsOverlay.NudgeFocused(1);
            else if (key is Key.Left or Key.Down) _settingsOverlay.NudgeFocused(-1);
            return;
        }

        if (_repeatTarget == RepeatTarget.Browser)
        {
            if (!_settingsOverlay.IsBrowserOpen) { _repeatingKey = null; return; } // browser closed mid-hold
            if (key == Key.Up) _settingsOverlay.MoveBrowserSelection(-1);
            else if (key == Key.Down) _settingsOverlay.MoveBrowserSelection(1);
            return;
        }

        byte[]? bytes = KeyTranslator.TranslateKeyDown(key, _repeatingMods);
        if (bytes is not null)
        {
            _pty.Input.Write(bytes, 0, bytes.Length);
            _pty.Input.Flush();
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        // Laptops without a full-size numpad often overlay Page Up/Down onto
        // Keypad9/Keypad3 (the classic 7/8/9=Home/Up/PgUp, 1/2/3=End/Down/PgDn
        // layout). GLFW reports the physical key identity here regardless of
        // whatever the laptop's own Fn/NumLock state is doing at the driver
        // level — Silk.NET's IKeyboard doesn't expose that lock-state to us —
        // so Key.Keypad9 shows up as Key.Keypad9, never as Key.PageUp, even
        // when the OS is already treating it as navigation. Normalizing once,
        // right here, means every PageUp/PageDown consumer below (scrollback
        // paging, the font browser, and raw escape sequences forwarded to
        // interactive shell programs like vim/less) picks it up for free
        // instead of needing the same special case in three separate places.
        if (key == Key.Keypad9) key = Key.PageUp;
        else if (key == Key.Keypad3) key = Key.PageDown;

        // Tracked unconditionally (ahead of the overlay early-return below)
        // so it stays accurate even while the settings overlay is open.
        if (key is Key.AltLeft or Key.AltRight) { _altHeld = true; return; }

        if (_settingsOverlay.IsOpen)
        {
            if (_settingsOverlay.IsBrowserOpen)
            {
                if (key == Key.Escape) { _settingsOverlay.CancelBrowser(); return; }
                if (key is Key.Enter or Key.KeypadEnter) { _settingsOverlay.ConfirmBrowserSelection(); return; }
                if (key == Key.Backspace) { _settingsOverlay.HandleBrowserBackspace(); return; }
                if (key == Key.Up) { _settingsOverlay.MoveBrowserSelection(-1); ArmRepeat(key, KeyModifiers.None, RepeatTarget.Browser); return; }
                if (key == Key.Down) { _settingsOverlay.MoveBrowserSelection(1); ArmRepeat(key, KeyModifiers.None, RepeatTarget.Browser); return; }
                if (key == Key.PageUp) { _settingsOverlay.MoveBrowserSelection(-ChronoTerm.Settings.SettingsOverlay.BrowserPageSize); return; }
                if (key == Key.PageDown) { _settingsOverlay.MoveBrowserSelection(ChronoTerm.Settings.SettingsOverlay.BrowserPageSize); return; }
                if (key == Key.Home) { _settingsOverlay.JumpBrowserToStart(); return; }
                if (key == Key.End) { _settingsOverlay.JumpBrowserToEnd(); return; }
                return; // swallow everything else while typing/browsing — nothing reaches the shell
            }

            if (_settingsOverlay.IsSaveAsOpen)
            {
                if (key == Key.Escape) { _settingsOverlay.CancelSaveAs(); return; }
                if (key is Key.Enter or Key.KeypadEnter) { _settingsOverlay.ConfirmSaveAs(); return; }
                if (key == Key.Backspace) { _settingsOverlay.HandleSaveAsBackspace(); return; }
                return; // swallow everything else while typing a name — nothing reaches the shell
            }

            if (key == Key.Escape) { _settingsOverlay.Close(); _repeatingKey = null; return; }
            if (key is Key.Right or Key.Up) { _settingsOverlay.NudgeFocused(1); ArmRepeat(key, KeyModifiers.None, RepeatTarget.OverlaySlider); return; }
            if (key is Key.Left or Key.Down) { _settingsOverlay.NudgeFocused(-1); ArmRepeat(key, KeyModifiers.None, RepeatTarget.OverlaySlider); return; }
            return; // nothing else reaches the shell while the overlay is open
        }

        var mods = CurrentModifiers(keyboard);

        // -------- Clipboard: Ctrl+Shift+C --------
        if (mods.HasFlag(KeyModifiers.Control) && mods.HasFlag(KeyModifiers.Shift) && key == Key.C)
        {
            CopySelectionToClipboard();
            return;
        }

        // -------- Clipboard: Ctrl+Shift+V --------
        if (mods.HasFlag(KeyModifiers.Control) && mods.HasFlag(KeyModifiers.Shift) && key == Key.V)
        {
            string? text = ChronoTerm.Clipboard.PasteFromClipboard();
            if (!string.IsNullOrEmpty(text))
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
                _pty.Input.Write(data, 0, data.Length);
                _pty.Input.Flush();
                Console.WriteLine($"[Clipboard] Pasted {data.Length} bytes");
            }
            else
            {
                Console.WriteLine("[Clipboard] Paste came back empty — see [Clipboard] lines above for why");
            }
            return;
        }

        // -------- Scrollback when not in alt-screen --------
        if (key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            int half = Math.Max(1, _session.Rows / 2);
            switch (key)
            {
                case Key.PageUp: _session.ScrollBy(half); return;
                case Key.PageDown: _session.ScrollBy(-half); return;
                case Key.Home when mods.HasFlag(KeyModifiers.Control): _session.ScrollToTop(); return;
                case Key.End when mods.HasFlag(KeyModifiers.Control): _session.ScrollToBottom(); return;
            }
            // Plain Home/End without Ctrl falls through to KeyTranslator below
            // (cursor-line home/end, forwarded to the shell as usual).
        }

        byte[]? bytes = KeyTranslator.TranslateKeyDown(key, mods);
        if (bytes is not null)
        {
            _pty.Input.Write(bytes, 0, bytes.Length);
            _pty.Input.Flush();
            ArmRepeat(key, mods, RepeatTarget.Pty);
        }
        // Printable characters arrive via OnKeyChar instead (handles shift/layout
        // for us, unlike raw SDL keycodes in the Python version) — OS-level text
        // repeat already covers those, no ArmRepeat needed here.
    }

    private void CopySelectionToClipboard()
    {
        if (_selectionStart is not { } start || _selectionEnd is not { } end || _lastSnapshot is not { } lines || lines.Length == 0)
        {
            Console.WriteLine("[Clipboard] Copy pressed but there's no active selection");
            return;
        }

        var (x1, y1) = start;
        var (x2, y2) = end;
        if (y1 > y2 || (y1 == y2 && x1 > x2)) ((x1, y1), (x2, y2)) = ((x2, y2), (x1, y1)); // normalize order (tuples have no lexicographic > in C#)

        y1 = Math.Clamp(y1, 0, lines.Length - 1);
        y2 = Math.Clamp(y2, 0, lines.Length - 1);

        var selected = new List<string>();
        for (int row = y1; row <= y2; row++)
        {
            string line = row < lines.Length ? lines[row] : "";
            int lineLen = line.Length;
            int startCol = row == y1 ? x1 : 0;
            int endCol = row == y2 ? x2 : lineLen;
            startCol = Math.Clamp(startCol, 0, lineLen);
            endCol = Math.Clamp(endCol, 0, lineLen);
            selected.Add(endCol > startCol ? line[startCol..endCol] : "");
        }

        string text = string.Join('\n', selected);
        bool ok = ChronoTerm.Clipboard.CopyToClipboard(text);
        Console.WriteLine(ok
            ? $"[Clipboard] Copied {text.Length} chars"
            : "[Clipboard] Copy failed — see [Clipboard] line above for why");
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (_altHeld && button is MouseButton.Left or MouseButton.Right)
        {
            _dragStartWindowPos = _window.Position;
            _dragStartWindowSize = _window.Size;
            _dragStartMouseLocal = mouse.Position;
            _draggingWindowMove = button == MouseButton.Left;
            _draggingWindowResize = button == MouseButton.Right;
            return;
        }

        if (button == MouseButton.Right)
        {
            _settingsOverlay.Toggle();
            return;
        }

        if (button != MouseButton.Left) return;

        if (_settingsOverlay.IsOpen)
        {
            var (panelCol, panelRow) = PixelToPanelCell(mouse.Position);
            _settingsOverlay.HandleMouseDown(panelCol, panelRow);
            return;
        }

        if (ScrollbarOverlay.IsOverThumb(_mouseCol, _mouseRow, _lastScrollbarInfo))
        {
            _draggingScrollbar = true;
            _scrollbarDragStartY = mouse.Position.Y;
            _scrollbarDragStartOffset = _session.ScrollOffset;
            return;
        }

        var cell = PixelToCell(mouse.Position);
        _selectionStart = cell;
        _selectionEnd = cell;
        _selecting = true;
    }

    /// <summary>Panel-relative cell coordinates, unaffected by terminal padding
    /// or curvature compensation — the overlay is flat/undistorted by design.</summary>
    /// <summary>colFrac is deliberately NOT truncated to a whole column —
    /// slider drag precision comes from this, not from bar width. A 14-column
    /// bar spanning ~250 screen pixels gives ~250 draggable positions this
    /// way, not 14; truncating here would throw that resolution away before
    /// SettingsOverlay ever saw it.</summary>
    private (double colFrac, int row) PixelToPanelCell(System.Numerics.Vector2 pixelPos)
    {
        if (_renderer is null) return (0, 0);
        var (originX, originY, cellW, cellH) = _renderer.ComputeOverlayLayout(
            ChronoTerm.Settings.SettingsOverlay.PanelCols, _settingsOverlay.PanelRows);
        if (cellW <= 0 || cellH <= 0) return (0, 0);
        double colFrac = (pixelPos.X - originX) / cellW;
        int row = (int)((pixelPos.Y - originY) / cellH);
        return (colFrac, row);
    }

    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        if (_draggingWindowMove)
        {
            // position is window-client-relative, so as SetWindowPos moves the
            // window under a stationary cursor, position keeps reporting how
            // far the cursor now sits from where the drag started — exactly
            // the offset to re-add to the window's starting position. Standard
            // GLFW borderless-window-drag technique.
            var delta = position - _dragStartMouseLocal;
            _window.Position = _dragStartWindowPos + new Vector2D<int>((int)delta.X, (int)delta.Y);
            return;
        }
        if (_draggingWindowResize)
        {
            // Window origin doesn't move here, so no re-basing needed: grow/
            // shrink directly by how far the cursor has moved since mouse-down.
            var delta = position - _dragStartMouseLocal;
            int newWidth = Math.Max(MinWindowWidth, _dragStartWindowSize.X + (int)delta.X);
            int newHeight = Math.Max(MinWindowHeight, _dragStartWindowSize.Y + (int)delta.Y);
            _window.Size = new Vector2D<int>(newWidth, newHeight);
            return;
        }

        if (_settingsOverlay.IsOpen)
        {
            var (panelCol, panelRow) = PixelToPanelCell(position);
            _settingsOverlay.HandleMouseMove(panelCol, panelRow);
            return;
        }

        (_mouseCol, _mouseRow) = PixelToCell(position);

        if (_draggingScrollbar)
        {
            int cellH = _renderer?.CellHeight ?? CellHeightPx;
            double dragDeltaChars = (position.Y - _scrollbarDragStartY) / cellH;
            int newOffset = ScrollbarOverlay.ScrollOffsetFromDrag(dragDeltaChars, _scrollbarDragStartOffset, _lastScrollbarInfo);
            _session.SetScrollOffset(newOffset);
            return;
        }

        if (!_selecting) return;
        _selectionEnd = (_mouseCol, _mouseRow);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (_draggingWindowMove || _draggingWindowResize)
        {
            _draggingWindowMove = false;
            _draggingWindowResize = false;
            return;
        }

        if (button != MouseButton.Left) return;

        if (_settingsOverlay.IsOpen)
        {
            _settingsOverlay.HandleMouseUp();
            return;
        }

        if (_draggingScrollbar)
        {
            _draggingScrollbar = false;
            return;
        }

        _selecting = false;
        // No drag occurred — clear rather than leave a zero-width selection
        // sitting around, matching main.py's behavior on a plain click.
        if (_selectionStart == _selectionEnd)
        {
            _selectionStart = null;
            _selectionEnd = null;
        }
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_settingsOverlay.IsOpen)
        {
            _settingsOverlay.HandleScroll(wheel.Y);
            return;
        }
        if (_draggingScrollbar) return;
        int step = (int)Math.Max(1, Math.Abs(wheel.Y)) * 3;
        _session.ScrollBy(wheel.Y > 0 ? step : wheel.Y < 0 ? -step : 0);
    }

    /// <summary>Port of calculate_curvature_compensation — an empirical (not
    /// exact-inverse) approximation of where a curvature-warped pixel actually
    /// came from. Python's own comment calls this "simple empirical
    /// compensation"; matched here rather than deriving an exact inverse, both
    /// for consistency with the behavior you're used to and because getting a
    /// hand-derived exact inverse subtly wrong would be worse than this.</summary>
    private static (float x, float y) ApplyCurvatureCompensation(
        float mouseX, float mouseY, double curvatureAmount, int windowWidth, int windowHeight)
    {
        if (curvatureAmount <= 0) return (mouseX, mouseY);

        float xFactor = mouseX / windowWidth;
        float xComp = (float)curvatureAmount * xFactor * windowWidth * 0.8f;
        float yFactor = mouseY / windowHeight;
        float yComp = (float)curvatureAmount * yFactor * windowHeight * 0.5f;

        return (mouseX + xComp, mouseY + yComp);
    }

    /// <summary>Port of mouse_to_char_coords. Used for both text selection and
    /// scrollbar hit-testing — matches Python using one shared conversion for
    /// both, so curvature compensation applies consistently everywhere mouse
    /// input turns into a cell coordinate.</summary>
    private (int col, int row) PixelToCell(System.Numerics.Vector2 pixelPos)
    {
        float mouseX = pixelPos.X;
        float mouseY = pixelPos.Y;

        var curvature = _config.Effects.Curvature;
        if (curvature.Enabled)
            (mouseX, mouseY) = ApplyCurvatureCompensation(mouseX, mouseY, curvature.Amount, _window.Size.X, _window.Size.Y);

        var bounds = LayoutMath.EffectiveTextBounds(
            _window.Size.X, _window.Size.Y,
            _config.Window.Padding.Top, _config.Window.Padding.Bottom,
            _config.Window.Padding.Left, _config.Window.Padding.Right,
            (float)_config.Window.CornerRadius);

        int cellW = _renderer?.CellWidth ?? CellWidthPx;
        int cellH = _renderer?.CellHeight ?? CellHeightPx;

        float adjustedX = mouseX - bounds.Left;
        float adjustedY = mouseY - bounds.Top;

        int col = Math.Max(0, (int)MathF.Round(adjustedX / cellW));
        // Half-line offset centers the click target on each character row
        // instead of its top edge — matches Python's char_y_float - 0.5.
        int row = Math.Max(0, (int)MathF.Round(adjustedY / cellH - 0.5f));

        col = Math.Clamp(col, 0, Math.Max(0, _session.Cols - 1));
        row = Math.Clamp(row, 0, Math.Max(0, _session.Rows - 1));
        return (col, row);
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (_settingsOverlay.IsOpen)
        {
            if (_settingsOverlay.IsBrowserOpen) _settingsOverlay.HandleBrowserChar(c);
            else if (_settingsOverlay.IsSaveAsOpen) _settingsOverlay.HandleSaveAsChar(c);
            return;
        }
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(c.ToString());
        _pty.Input.Write(bytes, 0, bytes.Length);
        _pty.Input.Flush();
    }

    private static KeyModifiers CurrentModifiers(IKeyboard keyboard)
    {
        var mods = KeyModifiers.None;
        if (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight))
            mods |= KeyModifiers.Control;
        if (keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight))
            mods |= KeyModifiers.Alt;
        if (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight))
            mods |= KeyModifiers.Shift;
        return mods;
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _gl?.Viewport(size);
        _renderer?.Resize(size.X, size.Y);
        ApplyPadding();
        ResizeToFramebuffer(size.X, size.Y);

        // Keep config current on every resize, mirroring main.py's resize handler —
        // Save() (on close) then persists whatever the window size was last set to.
        // Logical window size, not framebuffer pixels (matters on HiDPI displays).
        _config.Window.Width = _window.Size.X;
        _config.Window.Height = _window.Size.Y;
    }

    /// <summary>Recomputes and applies the renderer's padding from
    /// LayoutMath — call whenever window size or corner_radius could have
    /// changed. Includes corner-radius compensation (layout.py's
    /// calculate_corner_radius_compensation), which the padding value alone
    /// never accounted for before this — with corner_radius > 0, text used
    /// to render slightly into the rounded-corner clip zone.</summary>
    private void ApplyPadding()
    {
        var bounds = LayoutMath.EffectiveTextBounds(
            _window.Size.X, _window.Size.Y,
            _config.Window.Padding.Top, _config.Window.Padding.Bottom,
            _config.Window.Padding.Left, _config.Window.Padding.Right,
            (float)_config.Window.CornerRadius);
        _renderer?.SetPadding((int)bounds.Left, (int)bounds.Top);
    }

    private void ResizeToFramebuffer(int pixelWidth, int pixelHeight)
    {
        int cellW = _renderer?.CellWidth ?? CellWidthPx;
        int cellH = _renderer?.CellHeight ?? CellHeightPx;
        var bounds = LayoutMath.EffectiveTextBounds(
            _window.Size.X, _window.Size.Y,
            _config.Window.Padding.Top, _config.Window.Padding.Bottom,
            _config.Window.Padding.Left, _config.Window.Padding.Right,
            (float)_config.Window.CornerRadius);
        int cols = Math.Max(1, (int)(bounds.EffectiveWidth / cellW));
        int rows = Math.Max(1, (int)(bounds.EffectiveHeight / cellH));
        _session.Resize(cols, rows);
        try
        {
            _pty.Resize(cols, rows);
        }
        catch (IOException)
        {
            // Shell may have already exited; resize races with shutdown are expected.
        }
    }

    private static (float r, float g, float b) Rgb(double[] c) =>
        ((float)c[0], (float)(c.Length > 1 ? c[1] : c[0]), (float)(c.Length > 2 ? c[2] : c[0]));

    private static (float r, float g, float b, float a) Rgba(double[] c) =>
        ((float)c[0], (float)(c.Length > 1 ? c[1] : c[0]), (float)(c.Length > 2 ? c[2] : c[0]), (float)(c.Length > 3 ? c[3] : 1.0));

    /// <summary>Cursor.Shape is stored as a hex string like "0x2588" (the same
    /// format Python's config used) rather than a raw char, so it's YAML/UI
    /// friendly and diffable. Parsed fresh every frame — cheap, and means a
    /// changed shape (from the settings panel or a loaded preset) shows up
    /// immediately with no restart, unlike Font.Path. An unparseable or
    /// out-of-range value falls back to the classic full block rather than
    /// throwing — ConfigManager.Sanitize catches this on load too, but
    /// defending here as well costs nothing and covers a value edited to
    /// something bad mid-session (e.g. hand-typed into a future text field).</summary>
    private static char ParseCursorChar(string shape)
    {
        try
        {
            return (char)Convert.ToInt32(shape, 16);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return '\u2588';
        }
    }

    private void OnRender(double deltaSeconds)
    {
        if (_shellExited)
        {
            _window.Close();
            return;
        }

        if (_renderer is null) return;

        TickKeyRepeat(deltaSeconds);

        if (_session.TakePendingTitle() is { } newTitle)
            _window.Title = newTitle;

        if (_config.Cursor.Blink)
        {
            _blinkAccumulator += deltaSeconds;
            if (_blinkAccumulator >= BlinkIntervalSeconds)
            {
                _blinkAccumulator -= BlinkIntervalSeconds;
                _blinkOn = !_blinkOn;
            }
        }
        else
        {
            _blinkOn = true;
        }

        var lines = _session.GetSnapshot(out int cursorX, out int cursorY, out bool cursorVisible);
        _lastSnapshot = lines; // clipboard source — deliberately NOT the scrollbar-overlaid version

        string[] renderLines = lines;
        if (!_session.InAltScreen && lines.Length > 0)
        {
            int barCol = Math.Max(0, lines[0].Length - 1);
            bool mouseOverBarArea = ScrollbarOverlay.IsOverBarArea(_mouseCol, _mouseRow, barCol, lines.Length);
            var (overlaid, info) = ScrollbarOverlay.Apply(
                lines, _session.ScrollbackCount, _session.ScrollOffset, _session.Rows,
                _config.Scrollbar.Mode, mouseOverBarArea, _draggingScrollbar);
            renderLines = overlaid;
            _lastScrollbarInfo = info;
        }
        else
        {
            _lastScrollbarInfo = default;
        }

        var spans = BuildSelectionSpans(lines);
        var burnIn = _config.Effects.BurnIn;
        var vignette = _config.Effects.Vignette;
        var curvature = _config.Effects.Curvature;
        var scanlines = _config.Effects.Scanlines;
        var crt = new CrtEffects(
            vignette.Enabled, (float)vignette.Strength,
            curvature.Enabled, (float)curvature.Amount,
            scanlines.Enabled, (float)scanlines.Intensity, (float)scanlines.Frequency,
            (float)_config.Window.CornerRadius);

        _renderer.RenderFrame(
            renderLines, cursorX, cursorY, cursorVisible && _blinkOn,
            Rgb(_config.Colors.Text), Rgba(_config.Colors.Background),
            (float)_config.Effects.Glow.Intensity, (float)_config.Effects.Glow.Spread, Rgb(_config.Colors.Glow),
            deltaSeconds, burnIn.Enabled, (float)burnIn.Build, (float)burnIn.FadeTime,
            (float)burnIn.Intensity, Rgba(burnIn.Tint),
            spans, Rgba(_config.Colors.Selection), crt, ParseCursorChar(_config.Cursor.Shape));

        if (_settingsOverlay.IsOpen)
        {
            var (overlayLines, swatches, highlightBars) = _settingsOverlay.Render();
            _renderer.DrawOverlayPanel(overlayLines, swatches, (0.8f, 0.9f, 1.0f), highlightBars: highlightBars);
        }
    }

    private List<(int row, int startCol, int endCol)>? BuildSelectionSpans(string[] lines)
    {
        if (_selectionStart is not { } start || _selectionEnd is not { } end) return null;
        if (start == end) return null;

        var (x1, y1) = start;
        var (x2, y2) = end;
        if (y1 > y2 || (y1 == y2 && x1 > x2)) ((x1, y1), (x2, y2)) = ((x2, y2), (x1, y1));

        y1 = Math.Clamp(y1, 0, lines.Length - 1);
        y2 = Math.Clamp(y2, 0, lines.Length - 1);

        var spans = new List<(int, int, int)>(y2 - y1 + 1);
        for (int row = y1; row <= y2; row++)
        {
            int lineLen = row < lines.Length ? lines[row].Length : 0;
            int startCol = row == y1 ? x1 : 0;
            int endCol = row == y2 ? x2 : lineLen;
            spans.Add((row, Math.Clamp(startCol, 0, lineLen), Math.Clamp(endCol, 0, lineLen)));
        }
        return spans;
    }

    private void OnClosing()
    {
        _configManager.Save();
        _pty.Dispose();

        // Must happen here, not in Dispose(): by the time window.Run() returns
        // and Dispose() runs, GLFW has already destroyed the window and its GL
        // context, so any gl* call (even DeleteTexture) fails to resolve its
        // function pointer — SymbolLoadingException. Closing is the last point
        // where the context is guaranteed still current.
        _input?.Dispose();
        _renderer?.Dispose();
        _gl?.Dispose();
    }

    public void Dispose()
    {
        _window.Dispose();
    }
}