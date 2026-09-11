using ChronoTerm.Pty;

namespace ChronoTerm.Terminal;

/// <summary>
/// Ports PTYProcess.py's normal_screen/alt_screen split: a TUI app's full-screen
/// redraw (vim, htop, less) and the shell prompt underneath it need to be
/// different buffers, or nothing ever clears the app's leftover content when it
/// exits. Alt-screen switches are now detected properly via CSI ?1049h/l in the
/// parser itself, instead of the substring search on raw decoded text that the
/// Python version used as a workaround.
/// </summary>
public sealed class TerminalSession : IVtScreenHost
{
    private TerminalScreen _normalScreen;
    private TerminalScreen _altScreen;
    private readonly VtParser _parser;
    private readonly object _lock = new();
    private IPty? _pty; // set by StartReading — see WriteResponse

    public bool InAltScreen { get; private set; }
    public TerminalScreen Screen => InAltScreen ? _altScreen : _normalScreen;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Lines back from live (0 = following live output), same meaning as
    /// main.py's scroll_offset global. Only meaningful outside alt-screen — TUI
    /// apps handle their own scrolling, so this always snaps to 0 there.</summary>
    public int ScrollOffset { get; private set; }

    public TerminalSession(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        _normalScreen = new TerminalScreen(cols, rows);
        _altScreen = new TerminalScreen(cols, rows);
        _parser = new VtParser(this);
    }

    /// <summary>Call from the pty reader thread. Thread-safe with respect to
    /// GetSnapshot(), called from the render thread.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            _parser.Feed(bytes);
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            Cols = cols;
            Rows = rows;
            _normalScreen.Resize(cols, rows);
            _altScreen.Resize(cols, rows);
        }
    }

    public void EnterAltScreen()
    {
        if (InAltScreen) return;
        InAltScreen = true;
        ScrollOffset = 0; // follow live during TUIs — matches handle_mouse_wheel's alt-screen guard
        // Fresh alt buffer so stale content from a *previous* TUI app's
        // session can't bleed into this one — same reasoning as the Python version.
        _altScreen = new TerminalScreen(Cols, Rows);
    }

    public void ExitAltScreen()
    {
        InAltScreen = false;
    }

    private volatile string? _pendingTitle;

    /// <summary>Called from the pty-reader thread via VtParser (OSC 0/1/2).
    /// Not applied directly — window.Title assignment isn't guaranteed safe
    /// from a non-main thread, so this just records the request; the render
    /// thread picks it up via TakePendingTitle(), same poll-a-flag pattern
    /// used for shell-exit detection.</summary>
    public void SetWindowTitle(string title) => _pendingTitle = title;

    /// <summary>Writes an escape-sequence reply straight back to the pty's
    /// input side. Called synchronously from VtParser.Feed() — i.e. from the
    /// pty-reader thread itself, same thread StartReading's loop runs on —
    /// which is fine here: it's a distinct stream (pty.Input, not the
    /// pty.Output that thread is busy reading from) and these replies are a
    /// handful of bytes, not a size where partial/torn writes are a realistic
    /// concern. _pty is only null if a query somehow arrives before
    /// StartReading has run, which shouldn't happen since Feed() is only ever
    /// invoked from within that same reader loop — guarded anyway since
    /// "malformed input caused an unexpected code path" is exactly the kind
    /// of thing worth not crashing over.</summary>
    public void WriteResponse(ReadOnlySpan<byte> bytes)
    {
        var pty = _pty;
        if (pty is null) return;
        pty.Input.Write(bytes);
        pty.Input.Flush();
    }

    /// <summary>Call once per frame from the render thread. Returns null if
    /// no title change is pending.</summary>
    public string? TakePendingTitle()
    {
        var title = _pendingTitle;
        _pendingTitle = null;
        return title;
    }

    /// <summary>Wheel scroll — deltaLines positive scrolls back into history,
    /// negative scrolls toward live. No-op (forced to 0) in alt-screen, same as
    /// handle_mouse_wheel's early-return-to-0 for TUI apps.</summary>
    public void ScrollBy(int deltaLines)
    {
        lock (_lock)
        {
            if (InAltScreen) { ScrollOffset = 0; return; }
            ScrollOffset = Math.Clamp(ScrollOffset + deltaLines, 0, _normalScreen.Scrollback.Count);
        }
    }

    /// <summary>Absolute set — used by scrollbar thumb dragging, unlike
    /// ScrollBy's relative wheel-step semantics.</summary>
    public void SetScrollOffset(int offset)
    {
        lock (_lock)
        {
            if (InAltScreen) { ScrollOffset = 0; return; }
            ScrollOffset = Math.Clamp(offset, 0, _normalScreen.Scrollback.Count);
        }
    }

    /// <summary>Total scrollback line count — the scrollbar needs this to
    /// compute thumb size/position independent of whatever's currently
    /// visible.</summary>
    public int ScrollbackCount { get { lock (_lock) { return _normalScreen.Scrollback.Count; } } }

    public void ScrollToTop()
    {
        lock (_lock)
        {
            if (!InAltScreen) ScrollOffset = _normalScreen.Scrollback.Count;
        }
    }

    public void ScrollToBottom()
    {
        lock (_lock) { ScrollOffset = 0; }
    }

    /// <summary>Snapshot of the visible rows as plain strings, for the renderer
    /// and for clipboard copy (mirrors main.py's last_rendered_lines — copy
    /// selects from exactly what GetSnapshot last returned, not live screen
    /// state, so a copy during scrollback grabs history text, not the live
    /// buffer underneath it). Call from the render thread — takes the same lock
    /// Feed() uses.</summary>
    public string[] GetSnapshot(out int cursorX, out int cursorY, out bool cursorVisible)
    {
        lock (_lock)
        {
            if (!InAltScreen && ScrollOffset > 0 && _normalScreen.Scrollback.Count > 0)
            {
                var lines = BuildScrollbackWindow();
                // No live cursor while scrolled into history — main.py's
                // scrollback branch never calls into the cursor-drawing path.
                cursorX = 0;
                cursorY = 0;
                cursorVisible = false;
                return lines;
            }

            var screen = Screen;
            var live = new string[screen.Rows];
            for (int r = 0; r < screen.Rows; r++)
                live[r] = screen.GetLine(r);
            cursorX = screen.CursorX;
            cursorY = screen.CursorY;
            cursorVisible = screen.CursorVisible;
            return live;
        }
    }

    private string[] BuildScrollbackWindow()
    {
        var scrollback = _normalScreen.Scrollback;
        int total = scrollback.Count;
        int start = Math.Max(0, total - ScrollOffset - Rows);
        int end = Math.Max(0, total - ScrollOffset);

        var chunk = new List<string>(end - start);
        for (int i = start; i < end; i++)
            chunk.Add(PadOrTruncate(scrollback[i], Cols));

        while (chunk.Count < Rows)
            chunk.Insert(0, new string(' ', Cols));

        // chunk[-visible_rows:] equivalent, in case start==0 gave us extra.
        if (chunk.Count > Rows)
            chunk = chunk.GetRange(chunk.Count - Rows, Rows);

        return chunk.ToArray();
    }

    private static string PadOrTruncate(string s, int width)
    {
        if (s.Length == width) return s;
        return s.Length > width ? s[..width] : s.PadRight(width);
    }

    /// <summary>Wires this session's Feed() up as the consumer of a pty's output
    /// stream. Runs the read loop on a background thread, same shape as
    /// TerminalWindow's previous ad-hoc reader — call this instead of subscribing
    /// to OutputReceived directly once a TerminalSession is in the picture.</summary>
    /// <param name="onExit">Fired when the read loop ends for any reason — clean
    /// EOF (n==0) OR an IOException. Both mean the same thing in practice: the
    /// shell is gone. Linux PTY masters commonly raise EIO rather than a clean
    /// EOF when the slave side closes (a well-known quirk — Python's os.read()
    /// hits the exact same thing), and .NET surfaces that as IOException, not a
    /// 0-byte read. Treating both as "shell exited" avoids silently swallowing
    /// the EIO case into just a log line with nothing acting on it.</param>
    public Thread StartReading(IPty pty, Action<Exception>? onError = null, Action? onExit = null)
    {
        _pty = pty;
        var thread = new Thread(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int n = pty.Output.Read(buffer, 0, buffer.Length);
                    if (n == 0) break; // clean EOF
                    Feed(buffer.AsSpan(0, n));
                }
            }
            catch (IOException ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                onExit?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "pty-reader",
        };
        thread.Start();
        return thread;
    }
}
