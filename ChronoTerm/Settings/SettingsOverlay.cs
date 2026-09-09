using ChronoTerm.Config;
using System.Linq;

namespace ChronoTerm.Settings;

public enum ControlKind { Slider, Toggle, Button, ColorBar, Picker }

/// <summary>One interactive row in the panel. Sliders/color-bars store a
/// Get/Set pair bound directly to a ChronoTermConfig field via closures —
/// dragging a bar mutates the live config object immediately (same object
/// TerminalWindow reads every frame), which is the entire live-apply
/// mechanism. No event system needed since overlay and render loop share
/// the same thread and the same config instance.</summary>
public sealed class SettingsControl
{
    public required ControlKind Kind;
    public required int Row;
    public required int BarCol;   // column where the interactive region starts
    public required int BarWidth; // width in columns of the bar/toggle/button
    public Func<double>? GetValue;
    public Action<double>? SetValue;
    public double Min = 0, Max = 1;
    public double Step = 0.01; // exact per-press/per-notch increment — see AddSlider
    public Func<bool>? GetBool;
    public Action<bool>? SetBool;
    public Action? OnClick;

    // Picker only: dynamic label showing the current value (font name,
    // cursor shape, ...). Clicking anywhere in the bar (same as a Button)
    // invokes OnClick to open the relevant browser rather than stepping
    // through values inline — for the font case especially, with hundreds/
    // thousands of installed fonts a one-at-a-time stepper doesn't scale, so
    // this is a "current value + click to browse" control instead of a
    // slider-like one.
    public Func<string>? GetLabel;
}

public sealed class SettingsOverlay
{
    public bool IsOpen { get; private set; }

    private readonly ChronoTermConfig _config;
    private readonly ConfigManager _configManager;
    private readonly List<SettingsControl> _controls = new();
    private readonly List<FontEntry> _allFonts;
    private SettingsControl? _dragging;

    // Widened from 58 to fit a 4th (alpha) color bar per row — see AddColorBars.
    public const int PanelCols = 66;
    public int PanelRows { get; private set; }

    private const char FilledBar = '\u2588'; // full block
    private const char EmptyBar = '\u2591';  // light shade
    private const int BarWidth = 14;
    private const int LabelCol = 3;
    private const int BarCol = 20;
    private const int ValueCol = BarCol + BarWidth + 2;
    private const int PickerWidth = 34;

    private readonly Action<bool> _onHideTitlebarChanged;
    private readonly Action _onWindowSettingsChanged;

    public SettingsOverlay(ChronoTermConfig config, ConfigManager configManager,
        Action<bool> onHideTitlebarChanged, Action onWindowSettingsChanged)
    {
        _config = config;
        _configManager = configManager;
        _onHideTitlebarChanged = onHideTitlebarChanged;
        _onWindowSettingsChanged = onWindowSettingsChanged;

        // Discovered once at panel construction (not per-frame — even the
        // fc-list subprocess call is cheap but not THAT cheap). If the
        // configured font isn't among the discovered fonts (custom path, or
        // fontconfig doesn't know about it for some reason), it's added so
        // it's always present in the browser and never silently swapped out.
        _allFonts = FontFinder.DiscoverFonts();
        if (!string.IsNullOrEmpty(_config.Font.Path) && _allFonts.All(f => f.Path != _config.Font.Path))
        {
            _allFonts.Add(new FontEntry(_config.Font.Path, Path.GetFileNameWithoutExtension(_config.Font.Path)));
            _allFonts.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        BuildControls();
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        _dragging = null;
        _focused = null;
        _browserMode = BrowserMode.None;
        _browserScrollbarDragging = false;
        _saveAsOpen = false;
    }

    public void Close()
    {
        IsOpen = false;
        _dragging = null;
        _focused = null;
        _browserMode = BrowserMode.None;
        _browserScrollbarDragging = false;
        _saveAsOpen = false;
    }

    // ---- Layout ----
    // Row numbers assigned once here and reused by both BuildControls and
    // Render — keeping them as named constants avoids the two falling out of
    // sync as fields get added/reordered.
    private const int RowTitle = 1;
    private const int RowWindowHeader = 3;
    private const int RowTitlebar = 4;
    private const int RowWindowWidth = 5;
    private const int RowWindowHeight = 6;
    private const int RowPadding = 7;
    private const int RowCursorBlink = 8;
    private const int RowCursorShape = 9;
    private const int RowFontHeader = 11;
    private const int RowFontFamily = 12;
    private const int RowFontSize = 13;
    private const int RowCharSpacing = 14;
    private const int RowLineSpacing = 15;
    private const int RowColorsHeader = 17;
    private const int RowColorText = 18;
    private const int RowColorBackground = 19;
    private const int RowColorGlow = 20;
    private const int RowColorSelection = 21;
    private const int RowEffectsHeader = 23;
    private const int RowGlowIntensity = 24;
    private const int RowGlowSpread = 25;
    private const int RowBurnIn = 26;
    private const int RowVignette = 27;
    private const int RowCurvature = 28;
    private const int RowScanlines = 29;
    private const int RowCornerRadius = 30;
    private const int RowButtons = 32;
    private const int RowBorderBottom = 33;

    // ---- List browser (a full-panel sub-mode, not an inline control) ----
    // Shared by two different pickers — "pick a font" and "pick a config
    // preset to load" — since they're the same interaction shape (search,
    // scroll, highlight, click-or-Enter-to-choose) over a different list of
    // (Key, Display) pairs: Key is what gets applied on confirm (a file path
    // for fonts, a preset name for configs), Display is what's shown/searched.
    // Reuses the same PanelCols/PanelRows the normal panel already reports,
    // so TerminalWindow's layout/hit-testing code needs zero changes to
    // support it — Render()/HandleMouseDown()/etc. just branch internally on
    // _browserMode and swap in this content instead.
    private enum BrowserMode { None, Font, LoadConfig, CursorShape }

    /// <summary>Curated so every option is guaranteed to actually render —
    /// FontAtlas covers ASCII/Box-Drawing/Block-Elements/Braille, and these
    /// are all drawn from that set. (Some hand-authored configs use shapes
    /// outside those ranges, e.g. Dingbats — those still load fine, they just
    /// won't have a glyph and render as an invisible cursor; see
    /// OpenCursorShapeBrowser for how an unlisted current value is handled.)</summary>
    private static readonly (string Key, string Display)[] CursorShapePresets =
    {
        ("0x2588", "Block (\u2588)"),
        ("0x005F", "Underscore (_)"),
        ("0x2502", "Vertical Bar (\u2502)"),
        ("0x2506", "Dashed Bar (\u2506)"),
        ("0x0000", "Hidden"),
    };

    private const int BrRowTitle = 1;
    private const int BrRowSearch = 3;
    private const int BrRowListStart = 5;
    private const int BrVisibleRows = 20; // rows 5..24, well inside RowBorderBottom=29
    private const int BrRowFooter = 26;
    private const int BrScrollbarCol = PanelCols - 2; // just inside the right border
    public const int BrowserPageSize = BrVisibleRows; // true one-screen jump, not an arbitrary count

    private BrowserMode _browserMode = BrowserMode.None;
    private string _browserFilter = "";
    private List<(string Key, string Display)> _browserAllItems = new();
    private List<(string Key, string Display)> _browserFiltered = new();
    private int _browserSelected;
    private int _browserScroll;
    private bool _browserScrollbarDragging;

    // ---- Save-As prompt (a full-panel sub-mode, plain text entry, no list) ----
    private bool _saveAsOpen;
    private string _saveAsName = "";

    private void BuildControls()
    {
        _controls.Clear();

        // ON = titlebar shown (inverse of HideTitlebar) — so the default
        // state (titlebar visible, HideTitlebar=false) reads as ON at a
        // glance, matching every other toggle in this panel where ON means
        // "the thing is active/present." Flips the live GLFW window
        // decoration via the callback immediately — no need to reopen the
        // window or hit Save first.
        AddToggle(RowTitlebar, () => !_config.Window.HideTitlebar, shown =>
        {
            _config.Window.HideTitlebar = !shown;
            _onHideTitlebarChanged(!shown);
        });

        // Live: dragging either slider resizes the actual window immediately
        // via _onWindowSettingsChanged (same callback Load Config uses),
        // which cascades through GLFW's resize handling — viewport, PTY grid,
        // and padding all follow automatically. See TerminalWindow's wiring
        // of this callback for why nothing else is needed here.
        AddSlider(RowWindowWidth, () => _config.Window.Width,
            v => { _config.Window.Width = (int)v; _onWindowSettingsChanged(); }, 400, 3840, step: 10);
        AddSlider(RowWindowHeight, () => _config.Window.Height,
            v => { _config.Window.Height = (int)v; _onWindowSettingsChanged(); }, 300, 2160, step: 10);
        AddPaddingBars(RowPadding); // also live — ApplyPadding() reads these fresh on every resize

        AddToggle(RowCursorBlink, () => _config.Cursor.Blink, v => _config.Cursor.Blink = v);
        AddCursorShapePicker(RowCursorShape);

        AddFontPicker(RowFontFamily);
        AddSlider(RowFontSize, () => _config.Font.Size, v => _config.Font.Size = (int)v, 6, 128, step: 1);
        AddSlider(RowCharSpacing, () => _config.Font.CharSpacing, v => _config.Font.CharSpacing = v, 0.5, 2.0, step: 0.05);
        AddSlider(RowLineSpacing, () => _config.Font.LineSpacing, v => _config.Font.LineSpacing = v, 0.5, 2.0, step: 0.05);

        AddColorBars(RowColorText, _config.Colors.Text);
        AddColorBars(RowColorBackground, _config.Colors.Background);
        AddColorBars(RowColorGlow, _config.Colors.Glow);
        AddColorBars(RowColorSelection, _config.Colors.Selection);

        AddSlider(RowGlowIntensity, () => _config.Effects.Glow.Intensity, v => _config.Effects.Glow.Intensity = v, 0, 3, step: 0.05);
        AddSlider(RowGlowSpread, () => _config.Effects.Glow.Spread, v => _config.Effects.Glow.Spread = v, 0, 3, step: 0.05);
        AddSlider(RowBurnIn, () => _config.Effects.BurnIn.Intensity, v => _config.Effects.BurnIn.Intensity = v, 0, 1, step: 0.02,
            setEnabled: on => _config.Effects.BurnIn.Enabled = on);
        AddSlider(RowVignette, () => _config.Effects.Vignette.Strength, v => _config.Effects.Vignette.Strength = v, 0, 1, step: 0.02,
            setEnabled: on => _config.Effects.Vignette.Enabled = on);
        AddSlider(RowCurvature, () => _config.Effects.Curvature.Amount, v => _config.Effects.Curvature.Amount = v, 0, 0.1, step: 0.002,
            setEnabled: on => _config.Effects.Curvature.Enabled = on);
        AddSlider(RowScanlines, () => _config.Effects.Scanlines.Intensity, v => _config.Effects.Scanlines.Intensity = v, 0, 1, step: 0.02,
            setEnabled: on => _config.Effects.Scanlines.Enabled = on);
        AddSlider(RowCornerRadius, () => _config.Window.CornerRadius, v => _config.Window.CornerRadius = v, 0, 128, step: 1); // was 50 — widened to cover cozy.yaml's 128; dial back if that's more range than wanted

        // Four buttons, centered as a group under the 66-col panel:
        // "[ SAVE ]"(8) "[ SAVE AS ]"(11) "[ LOAD ]"(8) "[ CLOSE ]"(9), 2-col
        // gaps between each — total 42 cols, so a 12-col left margin centers it.
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Button, Row = RowButtons, BarCol = 12, BarWidth = 8,
            OnClick = () => _configManager.Save(),
        });
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Button, Row = RowButtons, BarCol = 22, BarWidth = 11,
            OnClick = OpenSaveAsPrompt,
        });
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Button, Row = RowButtons, BarCol = 35, BarWidth = 8,
            OnClick = OpenConfigBrowser,
        });
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Button, Row = RowButtons, BarCol = 45, BarWidth = 9,
            OnClick = Close,
        });

        PanelRows = RowBorderBottom + 1;
    }

    private void AddSlider(int row, Func<double> get, Action<double> set, double min, double max, double step, Action<bool>? setEnabled = null) =>
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Slider, Row = row, BarCol = BarCol, BarWidth = BarWidth,
            GetValue = get,
            SetValue = v =>
            {
                set(v);
                setEnabled?.Invoke(v > 0); // 0 on the slider = off, no separate toggle needed
            },
            Min = min, Max = max, Step = step,
        });

    private void AddToggle(int row, Func<bool> get, Action<bool> set) =>
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Toggle, Row = row, BarCol = BarCol, BarWidth = 3,
            GetBool = get, SetBool = set,
        });

    /// <summary>A button-like control showing the current font name; clicking
    /// it opens the searchable font browser (see AddColorBars docs above the
    /// Picker case in Render() for why this isn't an inline stepper).</summary>
    private void AddFontPicker(int row) =>
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Picker, Row = row, BarCol = BarCol, BarWidth = PickerWidth,
            GetLabel = () =>
            {
                var match = _allFonts.FirstOrDefault(f => f.Path == _config.Font.Path);
                return match.DisplayName ?? Path.GetFileNameWithoutExtension(_config.Font.Path);
            },
            OnClick = OpenFontBrowser,
        });

    private void AddCursorShapePicker(int row) =>
        _controls.Add(new SettingsControl
        {
            Kind = ControlKind.Picker, Row = row, BarCol = BarCol, BarWidth = PickerWidth,
            GetLabel = () =>
            {
                foreach (var preset in CursorShapePresets)
                    if (string.Equals(preset.Key, _config.Cursor.Shape, StringComparison.OrdinalIgnoreCase))
                        return preset.Display;
                return $"Custom ({_config.Cursor.Shape})";
            },
            OnClick = OpenCursorShapeBrowser,
        });

    /// <summary>Four independent int sliders (Top/Bottom/Left/Right) sharing
    /// one row — same visual spacing as AddColorBars, but built on plain
    /// ControlKind.Slider rather than ColorBar: ColorBar's renderer hardcodes
    /// a 0..1 → 0..255 rescale for the readout, which only makes sense for a
    /// color channel, not a padding value already in its natural units.</summary>
    private void AddPaddingBars(int row)
    {
        const int barW = 6;
        int col = LabelCol + 13;
        (Func<double> Get, Action<double> Set)[] fields =
        {
            (() => _config.Window.Padding.Top, v => _config.Window.Padding.Top = (int)v),
            (() => _config.Window.Padding.Bottom, v => _config.Window.Padding.Bottom = (int)v),
            (() => _config.Window.Padding.Left, v => _config.Window.Padding.Left = (int)v),
            (() => _config.Window.Padding.Right, v => _config.Window.Padding.Right = (int)v),
        };
        foreach (var (get, set) in fields)
        {
            _controls.Add(new SettingsControl
            {
                Kind = ControlKind.Slider, Row = row, BarCol = col, BarWidth = barW,
                GetValue = get,
                // Same live-refresh callback as Width/Height — ApplyPadding()
                // only runs at construction and on a window resize event, so
                // without this a padding drag would silently do nothing until
                // the window happened to resize some other way.
                SetValue = v => { set(v); _onWindowSettingsChanged(); },
                Min = 0, Max = 100, Step = 1,
            });
            col += barW + 6;
        }
    }

    /// <summary>Four sliders (R/G/B/A) sharing one row, packed after the color
    /// swatch.</summary>
    private void AddColorBars(int row, double[] colorArray)
    {
        const int barW = 6;
        int col = LabelCol + 13; // after label + swatch glyph
        for (int i = 0; i < 4; i++)
        {
            int idx = i; // capture
            _controls.Add(new SettingsControl
            {
                Kind = ControlKind.ColorBar, Row = row, BarCol = col, BarWidth = barW,
                GetValue = () => colorArray[idx], SetValue = v => colorArray[idx] = v, Min = 0, Max = 1, Step = 1.0 / 255.0,
            });
            col += barW + 6; // bar(barW) + "]"+space(2) + 3-digit value + space before next "[" (1)
        }
    }

    // ---- Rendering ----

    /// <summary>Builds the panel as a plain text grid plus a list of solid-color
    /// swatch rects (one per color row — can't show 4 different colors in a
    /// single glyph-batch draw call, which uses one uTextColor uniform for the
    /// whole batch, so swatches are drawn as separate solid quads instead).</summary>
    public (string[] lines, List<(int row, int col, float r, float g, float b, float a)> swatches,
        List<(int row, int colStart, int colEnd, float r, float g, float b, float a)> highlightBars) Render()
    {
        if (_browserMode != BrowserMode.None) return RenderBrowser();
        if (_saveAsOpen) return RenderSaveAsPrompt();

        var lines = new string[PanelRows];
        for (int i = 0; i < lines.Length; i++) lines[i] = new string(' ', PanelCols);

        SetLine(lines, 0, Border('\u250c', '\u2510'));
        SetLine(lines, RowBorderBottom, Border('\u2514', '\u2518'));
        for (int r = 1; r < RowBorderBottom; r++)
            lines[r] = '\u2502' + lines[r][1..^1] + '\u2502';

        WriteText(lines, RowTitle, LabelCol, "ChronoTerm Settings");
        WriteText(lines, RowWindowHeader, LabelCol, "WINDOW");
        WriteText(lines, RowFontHeader, LabelCol, "FONT");
        WriteText(lines, RowColorsHeader, LabelCol, "COLORS");
        WriteText(lines, RowEffectsHeader, LabelCol, "EFFECTS");

        WriteLabel(lines, RowTitlebar, "Titlebar");
        WriteLabel(lines, RowWindowWidth, "Width");
        WriteLabel(lines, RowWindowHeight, "Height");
        WriteLabel(lines, RowPadding, "Padding");
        WriteLabel(lines, RowCursorBlink, "Cursor Blink");
        WriteLabel(lines, RowCursorShape, "Cursor Shape");
        WriteLabel(lines, RowFontFamily, "Family");
        WriteLabel(lines, RowFontSize, "Size");
        WriteLabel(lines, RowCharSpacing, "Char Spacing");
        WriteLabel(lines, RowLineSpacing, "Line Spacing");
        WriteLabel(lines, RowGlowIntensity, "Glow Intensity");
        WriteLabel(lines, RowGlowSpread, "Glow Spread");
        WriteLabel(lines, RowBurnIn, "Burn-In");
        WriteLabel(lines, RowVignette, "Vignette");
        WriteLabel(lines, RowCurvature, "Curvature");
        WriteLabel(lines, RowScanlines, "Scanlines");
        WriteLabel(lines, RowCornerRadius, "Corner Radius");

        WriteText(lines, RowColorText, LabelCol, "Text");
        WriteText(lines, RowColorBackground, LabelCol, "Background");
        WriteText(lines, RowColorGlow, LabelCol, "Glow");
        WriteText(lines, RowColorSelection, LabelCol, "Selection");

        WriteText(lines, RowButtons, 12, "[ SAVE ]");
        WriteText(lines, RowButtons, 22, "[ SAVE AS ]");
        WriteText(lines, RowButtons, 35, "[ LOAD ]");
        WriteText(lines, RowButtons, 45, "[ CLOSE ]");

        var swatches = new List<(int, int, float, float, float, float)>();

        foreach (var c in _controls)
        {
            switch (c.Kind)
            {
                case ControlKind.Slider:
                {
                    double frac = Fraction(c.GetValue!(), c.Min, c.Max);
                    WriteBar(lines, c.Row, c.BarCol, c.BarWidth, frac);
                    WriteText(lines, c.Row, c.BarCol + c.BarWidth + 2, FormatValue(c.GetValue!()));
                    break;
                }
                case ControlKind.ColorBar:
                {
                    double frac = Fraction(c.GetValue!(), c.Min, c.Max);
                    WriteBar(lines, c.Row, c.BarCol, c.BarWidth, frac);
                    int as255 = (int)Math.Round(Math.Clamp(c.GetValue!(), 0, 1) * 255);
                    WriteText(lines, c.Row, c.BarCol + c.BarWidth + 2, as255.ToString().PadLeft(3));
                    break;
                }
                case ControlKind.Picker:
                {
                    string name = c.GetLabel?.Invoke() ?? "(none)";
                    // Inner width leaves room for a trailing " \u25be" (dropdown
                    // caret) so the control visually reads as "click to open".
                    int innerWidth = c.BarWidth - 3;
                    if (name.Length > innerWidth)
                        name = name[..Math.Max(0, innerWidth - 1)] + "\u2026"; // ellipsis
                    WriteText(lines, c.Row, c.BarCol, name.PadRight(innerWidth));
                    WriteText(lines, c.Row, c.BarCol + c.BarWidth - 2, "\u25be");
                    break;
                }
                case ControlKind.Toggle:
                {
                    // FilledBar/EmptyBar (U+2588/U+2591) instead of the
                    // U+25CF/U+25CB circle glyphs previously here — those live
                    // in the Geometric Shapes block, which FontAtlas doesn't
                    // cover (it only covers ASCII 32-126, Box Drawing, Block
                    // Elements, and Braille — see FontAtlas.cs), so GetUv()
                    // returned null and the marker silently never drew in
                    // EITHER state. FilledBar/EmptyBar are already used for
                    // the sliders above and are in the covered Block Elements
                    // range, so this also reads as "same on/off language as
                    // the rest of the panel" rather than a one-off checkbox.
                    bool on = c.GetBool!();
                    WriteText(lines, c.Row, c.BarCol, on ? $"[{FilledBar}]" : $"[{EmptyBar}]");
                    WriteText(lines, c.Row, c.BarCol + 4, on ? "ON " : "OFF");
                    break;
                }
            }
        }

        // Swatch preview quads, positioned right after each color row's label.
        AddSwatch(swatches, RowColorText, LabelCol + 11, _config.Colors.Text);
        AddSwatch(swatches, RowColorBackground, LabelCol + 11, _config.Colors.Background);
        AddSwatch(swatches, RowColorGlow, LabelCol + 11, _config.Colors.Glow);
        AddSwatch(swatches, RowColorSelection, LabelCol + 11, _config.Colors.Selection);

        // Focus marker — shows which control keyboard arrow keys will nudge.
        if (_focused is not null)
            WriteText(lines, _focused.Row, 1, ">");

        return (lines, swatches, new List<(int, int, int, float, float, float, float)>());
    }

    /// <summary>Full-panel searchable list — replaces the normal control
    /// rendering for both list-based sub-modes (pick a font / pick a config
    /// preset to load). Typed characters filter by Display or Key (for fonts
    /// that also matches on file path, so searching "mono" surfaces fonts
    /// whose family name doesn't literally contain "mono" but live in a
    /// directory that does — occasionally useful, e.g. distro package naming).
    ///
    /// Navigation modeled on Yazi: a highlighted cursor row instead of just a
    /// marker glyph, plus a draggable/clickable scrollbar for jumping across
    /// long lists fast (arrows/PageUp/PageDown/Home/End cover the keyboard
    /// side — see TerminalWindow.OnKeyDown's browser branch).</summary>
    private (string[] lines, List<(int, int, float, float, float, float)> swatches,
        List<(int, int, int, float, float, float, float)> highlightBars) RenderBrowser()
    {
        var lines = new string[PanelRows];
        for (int i = 0; i < lines.Length; i++) lines[i] = new string(' ', PanelCols);

        SetLine(lines, 0, Border('\u250c', '\u2510'));
        SetLine(lines, RowBorderBottom, Border('\u2514', '\u2518'));
        for (int r = 1; r < RowBorderBottom; r++)
            lines[r] = '\u2502' + lines[r][1..^1] + '\u2502';

        string browserTitle = _browserMode switch
        {
            BrowserMode.Font => "Select Font",
            BrowserMode.LoadConfig => "Load Config",
            BrowserMode.CursorShape => "Select Cursor Shape",
            _ => "",
        };
        WriteText(lines, BrRowTitle, LabelCol, browserTitle);
        WriteText(lines, BrRowSearch, LabelCol, $"Search: {_browserFilter}\u2588"); // block = text cursor

        var highlights = new List<(int, int, int, float, float, float, float)>();
        bool needsScrollbar = _browserFiltered.Count > BrVisibleRows;
        int listRightEdge = needsScrollbar ? BrScrollbarCol : PanelCols - 2;

        if (_browserFiltered.Count == 0)
        {
            string emptyMessage = _browserMode switch
            {
                BrowserMode.Font => "(no matching fonts)",
                BrowserMode.LoadConfig => "(no saved configs — use Save As to create one)",
                _ => "(no matches)",
            };
            WriteText(lines, BrRowListStart, LabelCol, emptyMessage);
        }
        else
        {
            for (int i = 0; i < BrVisibleRows; i++)
            {
                int itemIndex = _browserScroll + i;
                if (itemIndex >= _browserFiltered.Count) break;
                int row = BrRowListStart + i;

                if (itemIndex == _browserSelected)
                    // Full-row highlight bar (not just a marker glyph) so the
                    // current position reads at a glance in a long list.
                    highlights.Add((row, LabelCol - 1, listRightEdge, 0.30f, 0.45f, 0.65f, 0.55f));

                string label = _browserFiltered[itemIndex].Display;
                int maxLen = listRightEdge - LabelCol - 1;
                if (label.Length > maxLen) label = label[..Math.Max(0, maxLen - 1)] + "\u2026";
                WriteText(lines, row, LabelCol, label);
            }
        }

        if (needsScrollbar)
        {
            int thumbSize = Math.Max(1, BrVisibleRows * BrVisibleRows / _browserFiltered.Count);
            int maxScroll = Math.Max(0, _browserFiltered.Count - BrVisibleRows);
            int thumbPos = maxScroll > 0
                ? (int)Math.Round(_browserScroll / (double)maxScroll * (BrVisibleRows - thumbSize))
                : 0;
            thumbPos = Math.Clamp(thumbPos, 0, BrVisibleRows - thumbSize);

            for (int i = 0; i < BrVisibleRows; i++)
            {
                char c = (i >= thumbPos && i < thumbPos + thumbSize) ? '\u2588' : '\u2502';
                WriteText(lines, BrRowListStart + i, BrScrollbarCol, c.ToString());
            }
        }

        int position = _browserFiltered.Count == 0 ? 0 : _browserSelected + 1;
        WriteText(lines, BrRowFooter, LabelCol,
            $"{position}/{_browserFiltered.Count}  \u2191\u2193 PgUp/PgDn Home/End  Enter: choose  Esc: cancel");

        return (lines, new List<(int, int, float, float, float, float)>(), highlights);
    }

    /// <summary>Full-panel text prompt — no list, just typed characters into
    /// _saveAsName. Deliberately not folded into RenderBrowser/BrowserMode:
    /// there's no list to scroll/highlight here, so sharing that machinery
    /// would mean threading a bunch of "well, unless this is SaveAs" branches
    /// through code that's otherwise about list navigation.</summary>
    private (string[] lines, List<(int, int, float, float, float, float)> swatches,
        List<(int, int, int, float, float, float, float)> highlightBars) RenderSaveAsPrompt()
    {
        var lines = new string[PanelRows];
        for (int i = 0; i < lines.Length; i++) lines[i] = new string(' ', PanelCols);

        SetLine(lines, 0, Border('\u250c', '\u2510'));
        SetLine(lines, RowBorderBottom, Border('\u2514', '\u2518'));
        for (int r = 1; r < RowBorderBottom; r++)
            lines[r] = '\u2502' + lines[r][1..^1] + '\u2502';

        WriteText(lines, BrRowTitle, LabelCol, "Save Config As");
        WriteText(lines, BrRowTitle + 2, LabelCol, "Saves a copy of the current settings as a named preset.");
        WriteText(lines, BrRowSearch, LabelCol, $"Name: {_saveAsName}\u2588");
        WriteText(lines, BrRowFooter, LabelCol, "Enter: save  Esc: cancel");

        return (lines, new List<(int, int, float, float, float, float)>(), new List<(int, int, int, float, float, float, float)>());
    }

    /// <summary>True for EITHER list-based sub-mode (font or config) — this
    /// is what TerminalWindow checks to route Up/Down/PageUp/PageDown/Home/
    /// End/Enter/Backspace/typed-characters generically, since both modes
    /// want identical key handling. Which mode specifically is only relevant
    /// inside SettingsOverlay itself (for rendering and for what "confirm"
    /// actually does).</summary>
    public bool IsBrowserOpen => _browserMode != BrowserMode.None;

    public bool IsSaveAsOpen => _saveAsOpen;

    private void OpenFontBrowser()
    {
        _browserMode = BrowserMode.Font;
        _browserAllItems = _allFonts.Select(f => (Key: f.Path, Display: f.DisplayName)).ToList();
        _browserFilter = "";
        RecomputeBrowserFilter();
        _browserSelected = Math.Max(0, _browserFiltered.FindIndex(i => i.Key == _config.Font.Path));
        ScrollToSelection();
    }

    private void OpenConfigBrowser()
    {
        _browserMode = BrowserMode.LoadConfig;
        _browserAllItems = _configManager.ListProfiles().Select(n => (Key: n, Display: n)).ToList();
        _browserFilter = "";
        RecomputeBrowserFilter();
        _browserSelected = 0;
        ScrollToSelection();
    }

    private void OpenCursorShapeBrowser()
    {
        _browserMode = BrowserMode.CursorShape;
        _browserAllItems = new List<(string Key, string Display)>(CursorShapePresets);
        // Mirrors OpenFontBrowser's handling of a current value that isn't in
        // the curated list — a hand-edited config, or a preset like
        // future.yaml's 0x2758, still shows up as a selectable (if possibly
        // invisible — see CursorShapePresets' doc comment) entry rather than
        // silently vanishing from view the moment you open the picker.
        if (!_browserAllItems.Exists(i => string.Equals(i.Key, _config.Cursor.Shape, StringComparison.OrdinalIgnoreCase)))
            _browserAllItems.Add((_config.Cursor.Shape, $"Custom ({_config.Cursor.Shape})"));
        _browserFilter = "";
        RecomputeBrowserFilter();
        _browserSelected = Math.Max(0, _browserFiltered.FindIndex(
            i => string.Equals(i.Key, _config.Cursor.Shape, StringComparison.OrdinalIgnoreCase)));
        ScrollToSelection();
    }

    public void CancelBrowser() => _browserMode = BrowserMode.None;

    public void ConfirmBrowserSelection()
    {
        if (_browserSelected >= 0 && _browserSelected < _browserFiltered.Count)
        {
            var chosen = _browserFiltered[_browserSelected];
            if (_browserMode == BrowserMode.Font)
            {
                _config.Font.Path = chosen.Key;
                _config.Font.LastDirectory = Path.GetDirectoryName(chosen.Key) ?? _config.Font.LastDirectory;
            }
            else if (_browserMode == BrowserMode.CursorShape)
            {
                _config.Cursor.Shape = chosen.Key; // live — TerminalWindow parses this fresh every frame
            }
            else if (_browserMode == BrowserMode.LoadConfig)
            {
                // Colors/effects/cursor-blink/scrollbar-mode already apply live
                // on their own (everything downstream reads _config fresh each
                // frame) since CopyInto mutates values in place rather than
                // replacing the object graph. Window size, titlebar, and
                // padding are the exceptions — those are otherwise only
                // applied at specific trigger points (window construction, a
                // manual resize, the Titlebar toggle's own callback), none of
                // which "loading a preset" naturally passes through — hence
                // this explicit nudge.
                if (_configManager.LoadProfile(chosen.Key)) _onWindowSettingsChanged();
            }
        }
        _browserMode = BrowserMode.None;
    }

    public void HandleBrowserChar(char c)
    {
        // Printable, non-control characters only — Backspace/Enter/Escape
        // arrive as key events (see TerminalWindow.OnKeyDown), not chars.
        if (char.IsControl(c) || _browserFilter.Length >= 50) return;
        _browserFilter += c;
        RecomputeBrowserFilter();
        _browserSelected = 0;
        _browserScroll = 0;
    }

    public void HandleBrowserBackspace()
    {
        if (_browserFilter.Length == 0) return;
        _browserFilter = _browserFilter[..^1];
        RecomputeBrowserFilter();
        _browserSelected = 0;
        _browserScroll = 0;
    }

    public void MoveBrowserSelection(int delta)
    {
        if (_browserFiltered.Count == 0) return;
        _browserSelected = Math.Clamp(_browserSelected + delta, 0, _browserFiltered.Count - 1);
        ScrollToSelection();
    }

    public void JumpBrowserToStart()
    {
        if (_browserFiltered.Count == 0) return;
        _browserSelected = 0;
        ScrollToSelection();
    }

    public void JumpBrowserToEnd()
    {
        if (_browserFiltered.Count == 0) return;
        _browserSelected = _browserFiltered.Count - 1;
        ScrollToSelection();
    }

    /// <summary>Moves the selection to whichever item the given track row
    /// corresponds to — used for both a direct click on the scrollbar track
    /// and continuous updates while dragging the thumb (see HandleMouseMove).
    /// Proportional mapping across the full list, not just the visible page,
    /// so a click near the bottom of the track jumps most of the way through
    /// even in a list of thousands.</summary>
    private void JumpBrowserToTrackRow(int trackRow)
    {
        if (_browserFiltered.Count == 0) return;
        double t = BrVisibleRows > 1 ? trackRow / (double)(BrVisibleRows - 1) : 0;
        _browserSelected = Math.Clamp((int)Math.Round(t * (_browserFiltered.Count - 1)), 0, _browserFiltered.Count - 1);
        ScrollToSelection();
    }

    private void ScrollToSelection()
    {
        if (_browserSelected < _browserScroll) _browserScroll = _browserSelected;
        else if (_browserSelected >= _browserScroll + BrVisibleRows) _browserScroll = _browserSelected - BrVisibleRows + 1;
    }

    private void RecomputeBrowserFilter()
    {
        _browserFiltered = string.IsNullOrEmpty(_browserFilter)
            ? _browserAllItems
            : _browserAllItems.Where(i =>
                    i.Display.Contains(_browserFilter, StringComparison.OrdinalIgnoreCase) ||
                    i.Key.Contains(_browserFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    // ---- Save-As prompt ----

    private void OpenSaveAsPrompt()
    {
        _saveAsOpen = true;
        _saveAsName = "";
    }

    public void CancelSaveAs() => _saveAsOpen = false;

    public void ConfirmSaveAs()
    {
        if (string.IsNullOrWhiteSpace(_saveAsName)) return; // ignore Enter on an empty name rather than saving "Untitled"
        _configManager.SaveProfileAs(_saveAsName);
        _saveAsOpen = false;
    }

    public void HandleSaveAsChar(char c)
    {
        if (char.IsControl(c) || _saveAsName.Length >= 50) return;
        _saveAsName += c;
    }

    public void HandleSaveAsBackspace()
    {
        if (_saveAsName.Length == 0) return;
        _saveAsName = _saveAsName[..^1];
    }

    private static void AddSwatch(List<(int, int, float, float, float, float)> swatches, int row, int col, double[] color) =>
        // Alpha now comes from the color's own 4th channel (previously hardcoded
        // to 1f before alpha bars existed) — the swatch preview is drawn over a
        // dark panel background, so partial alpha renders visibly translucent.
        swatches.Add((row, col, (float)color[0], (float)color[1], (float)color[2], (float)color[3]));

    private static double Fraction(double value, double min, double max) =>
        max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;

    private static string FormatValue(double v) => v == Math.Floor(v) && Math.Abs(v) < 1000 ? ((int)v).ToString() : v.ToString("0.00");

    private void WriteBar(string[] lines, int row, int col, int width, double frac)
    {
        int filled = (int)Math.Round(frac * width);
        var bar = new char[width];
        for (int i = 0; i < width; i++) bar[i] = i < filled ? FilledBar : EmptyBar;
        WriteText(lines, row, col - 1, "[");
        WriteText(lines, row, col, new string(bar));
        WriteText(lines, row, col + width, "]");
    }

    private static void WriteLabel(string[] lines, int row, string label) => WriteText(lines, row, LabelCol, label);

    private static void WriteText(string[] lines, int row, int col, string text)
    {
        if (row < 0 || row >= lines.Length) return;
        var chars = lines[row].ToCharArray();
        for (int i = 0; i < text.Length && col + i < chars.Length - 1; i++)
            if (col + i >= 1) chars[col + i] = text[i];
        lines[row] = new string(chars);
    }

    private static void SetLine(string[] lines, int row, string content) => lines[row] = content;

    private string Border(char left, char right)
    {
        var chars = new char[PanelCols];
        chars[0] = left;
        chars[^1] = right;
        for (int i = 1; i < PanelCols - 1; i++) chars[i] = '\u2500';
        return new string(chars);
    }

    // ---- Interaction ----

    private SettingsControl? _hovered;
    private SettingsControl? _focused;

    /// <summary>Returns true if the click landed on a control (caller should
    /// not also process it as a terminal click). colFrac is the raw
    /// sub-column mouse position — NOT truncated — since drag precision comes
    /// from this, not from how many characters wide the bar is drawn.</summary>
    public bool HandleMouseDown(double colFrac, int row)
    {
        if (_browserMode != BrowserMode.None) return HandleBrowserClick((int)colFrac, row);
        if (_saveAsOpen) return true; // modal text prompt — nothing clickable in it, just swallow the click

        int col = (int)colFrac; // whole-cell hit-testing is fine for toggles/buttons

        foreach (var c in _controls)
        {
            if (c.Row != row) continue;
            bool inBar = col >= c.BarCol && col < c.BarCol + c.BarWidth;

            if (c.Kind is ControlKind.Slider or ControlKind.ColorBar && inBar)
            {
                _dragging = c;
                _focused = c; // clicking a slider also focuses it for arrow-key nudging
                ApplyDrag(c, colFrac);
                return true;
            }
            if (c.Kind == ControlKind.Toggle && (inBar || (col >= c.BarCol + 4 && col < c.BarCol + 7)))
            {
                c.SetBool!(!c.GetBool!());
                return true;
            }
            if (c.Kind is ControlKind.Button or ControlKind.Picker && inBar)
            {
                c.OnClick!();
                return true;
            }
        }
        return false;
    }

    /// <summary>Clicking a row in the list selects AND confirms immediately —
    /// consistent with how every other control in this panel applies changes
    /// live rather than requiring a separate "OK". Clicking the scrollbar
    /// track/thumb instead just moves the cursor there (Yazi-style: the
    /// scrollbar is for getting somewhere fast, not for picking — you still
    /// land on an item and can keep navigating from it) and arms
    /// drag-to-scroll for the following HandleMouseMove calls.</summary>
    private bool HandleBrowserClick(int col, int row)
    {
        if (_browserFiltered.Count > BrVisibleRows && col == BrScrollbarCol &&
            row >= BrRowListStart && row < BrRowListStart + BrVisibleRows)
        {
            _browserScrollbarDragging = true;
            JumpBrowserToTrackRow(row - BrRowListStart);
            return true;
        }

        int itemIndex = row - BrRowListStart + _browserScroll;
        if (row < BrRowListStart || itemIndex < 0 || itemIndex >= _browserFiltered.Count) return true; // swallow clicks outside the list too — it's a modal
        _browserSelected = itemIndex;
        ConfirmBrowserSelection();
        return true;
    }

    public void HandleMouseMove(double colFrac, int row)
    {
        if (_browserMode != BrowserMode.None)
        {
            if (_browserScrollbarDragging) JumpBrowserToTrackRow(row - BrRowListStart);
            return;
        }
        if (_saveAsOpen) return;

        if (_dragging is not null)
        {
            ApplyDrag(_dragging, colFrac);
            return;
        }

        // Track what's under the cursor even without a click — HandleScroll
        // uses this so the wheel can fine-tune whatever's currently hovered.
        int col = (int)colFrac;
        _hovered = _controls.FirstOrDefault(c =>
            c.Row == row && c.Kind is ControlKind.Slider or ControlKind.ColorBar &&
            col >= c.BarCol && col < c.BarCol + c.BarWidth);
    }

    public void HandleMouseUp()
    {
        _dragging = null;
        _browserScrollbarDragging = false;
    }

    /// <summary>Scroll-wheel fine adjustment on whatever slider/color-bar is
    /// currently hovered — steps by the control's own precise Step value
    /// (e.g. exactly 1/255 for a color bar), not a generic percentage. While
    /// a list browser is open, the wheel scrolls the list instead, and by
    /// more than one row per notch — one row per notch was the main reason
    /// scrolling a big list felt slow.</summary>
    public void HandleScroll(float wheelY)
    {
        if (wheelY == 0) return;
        if (_browserMode != BrowserMode.None) { MoveBrowserSelection(Math.Sign(wheelY) * 3); return; }
        if (_saveAsOpen) return;
        if (_hovered is not { } c) return;
        Nudge(c, Math.Sign(wheelY));
    }

    /// <summary>Arrow-key fine adjustment on whichever control was last
    /// clicked (see the focus marker drawn in Render()). This is the precise,
    /// deterministic path for landing on an exact value — e.g. holding Right
    /// on a color bar walks 1/255 at a time until it reads exactly 153.</summary>
    public void NudgeFocused(int direction) => Nudge(_focused, direction);

    private static void Nudge(SettingsControl? c, int direction)
    {
        if (c?.SetValue is null || c.GetValue is null || direction == 0) return;
        c.SetValue(Math.Clamp(c.GetValue() + c.Step * direction, c.Min, c.Max));
    }

    private static void ApplyDrag(SettingsControl c, double colFrac)
    {
        double frac = Math.Clamp((colFrac - c.BarCol) / (c.BarWidth - 1), 0, 1);
        c.SetValue!(c.Min + frac * (c.Max - c.Min));
    }
}