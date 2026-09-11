namespace ChronoTerm.Terminal;

/// <summary>
/// One screen buffer (there are two live instances at any time, mirroring
/// PTYProcess.py's normal_screen/alt_screen — see TerminalSession). Deliberately
/// tracks only characters, not color/bold/etc: main.py's screen_to_lines only ever
/// reads cell.data off the pyte screen, so there's no rendering behavior to match
/// that needs per-cell attributes. If per-cell styling is ever wanted later, this
/// is the place to add an attribute byte alongside _cells.
/// </summary>
public sealed class TerminalScreen
{
    private char[] _cells; // row-major, Cols*Rows
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; set; } = true;

    /// <summary>DECCKM (CSI ?1h/?1l) — arrow keys send ESC O A-D instead of the
    /// default ESC [ A-D when set. Full-screen apps commonly toggle this on
    /// entry/exit (vim, and some shells' vi-mode line editors); without
    /// tracking it, arrow keys would send the wrong encoding and silently do
    /// nothing in those contexts. Per-screen like CursorVisible above, not a
    /// single global flag, since that's the existing convention here for
    /// terminal modes that a full-screen app toggles for its own alt-screen
    /// session without it leaking back to the normal-screen shell prompt.</summary>
    public bool ApplicationCursorKeys { get; set; }

    // DECSTBM scroll region, 0-indexed, inclusive. TUI apps (htop's split
    // panes, vim's status line) rely on this to scroll only part of the screen.
    private int _scrollTop;
    private int _scrollBottom;

    // Pending-wrap flag: xterm defers the actual wrap until the *next* printable
    // character, so a full-width line doesn't immediately push a blank row.
    private bool _wrapPending;

    private int _savedCursorX, _savedCursorY;

    /// <summary>Only populated for the normal screen (see TerminalSession) — lines
    /// that scroll off the top land here, newest last, capped like a real
    /// terminal's scrollback. List (not a queue) because TerminalSession needs
    /// indexed slicing to build the scrollback-window view.</summary>
    public List<string> Scrollback { get; } = new();
    private const int MaxScrollback = 1000;

    public TerminalScreen(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        _cells = new char[cols * rows];
        Array.Fill(_cells, ' ');
        _scrollTop = 0;
        _scrollBottom = rows - 1;
    }

    public void Resize(int cols, int rows)
    {
        var newCells = new char[cols * rows];
        Array.Fill(newCells, ' ');
        int copyRows = Math.Min(rows, Rows);
        int copyCols = Math.Min(cols, Cols);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newCells[r * cols + c] = _cells[r * Cols + c];

        _cells = newCells;
        Cols = cols;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        CursorY = Math.Min(CursorY, rows - 1);
        _wrapPending = false;
    }

    /// <summary>Snapshot of one row as a plain string — this is the direct
    /// equivalent of main.py's screen_to_lines per-row loop, minus the tab/wcwidth
    /// handling that belongs in the renderer once it exists, not here.</summary>
    public string GetLine(int row)
    {
        if (row < 0 || row >= Rows) return new string(' ', Cols);
        return new string(_cells, row * Cols, Cols);
    }

    /// <summary>Continuation-cell marker for the second half of a wide (2-cell)
    /// character — never rendered (TerminalGlRenderer skips it, same as it
    /// skips any char with no atlas slot), just reserves the cell so layout
    /// after a wide character doesn't shift left.</summary>
    public const char WideCharContinuation = '\0';

    public void PutChar(char c)
    {
        bool wide = WideCharWidth.IsWide(c);

        // A wide char can't split across the wrap boundary — if it wouldn't
        // fit in the last column, wrap early rather than clipping it.
        if (_wrapPending || (wide && CursorX == Cols - 1))
        {
            LineFeed();
            CarriageReturn();
            _wrapPending = false;
        }

        _cells[CursorY * Cols + CursorX] = c;

        if (wide && CursorX + 1 < Cols)
            _cells[CursorY * Cols + CursorX + 1] = WideCharContinuation;

        int advance = wide ? 2 : 1;
        if (CursorX + advance >= Cols)
        {
            CursorX = Cols - 1;
            _wrapPending = true; // defer actual wrap, see field comment
        }
        else
        {
            CursorX += advance;
        }
    }

    public void CarriageReturn()
    {
        CursorX = 0;
        _wrapPending = false;
    }

    /// <summary>LF — moves down, scrolling the region if already at the bottom.
    /// Does NOT return the cursor to column 0 (that's CarriageReturn's job); the
    /// parser sends both for a plain '\n' byte, matching real terminal behavior.</summary>
    public void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _scrollBottom)
            ScrollUp(1);
        else if (CursorY < Rows - 1)
            CursorY++;
    }

    /// <summary>ESC M — reverse index: like LineFeed but upward.</summary>
    public void ReverseLineFeed()
    {
        _wrapPending = false;
        if (CursorY == _scrollTop)
            ScrollDown(1);
        else if (CursorY > 0)
            CursorY--;
    }

    public void Backspace()
    {
        _wrapPending = false;
        if (CursorX > 0) CursorX--;
    }

    public void Tab()
    {
        _wrapPending = false;
        int next = (CursorX / 8 + 1) * 8;
        CursorX = Math.Min(next, Cols - 1);
    }

    public void SetCursorPos(int row, int col)
    {
        _wrapPending = false;
        CursorY = Math.Clamp(row, 0, Rows - 1);
        CursorX = Math.Clamp(col, 0, Cols - 1);
    }

    public void MoveCursorRelative(int dx, int dy)
    {
        _wrapPending = false;
        CursorX = Math.Clamp(CursorX + dx, 0, Cols - 1);
        CursorY = Math.Clamp(CursorY + dy, 0, Rows - 1);
    }

    public void SetScrollRegion(int top, int bottom)
    {
        _scrollTop = Math.Clamp(top, 0, Rows - 1);
        _scrollBottom = Math.Clamp(bottom, _scrollTop, Rows - 1);
        SetCursorPos(0, 0);
    }

    public void ResetScrollRegion()
    {
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    /// <summary>Push scrollTop's row into Scrollback before shifting — only
    /// meaningful for the normal screen; TerminalSession skips this call's
    /// scrollback effect for the alt screen by using a screen instance whose
    /// Scrollback is simply never read.</summary>
    public void ScrollUp(int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (_scrollTop == 0)
            {
                Scrollback.Add(GetLine(0).TrimEnd());
                if (Scrollback.Count > MaxScrollback)
                    Scrollback.RemoveAt(0);
            }

            for (int row = _scrollTop; row < _scrollBottom; row++)
                Array.Copy(_cells, (row + 1) * Cols, _cells, row * Cols, Cols);

            ClearRow(_scrollBottom);
        }
    }

    public void ScrollDown(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int row = _scrollBottom; row > _scrollTop; row--)
                Array.Copy(_cells, (row - 1) * Cols, _cells, row * Cols, Cols);

            ClearRow(_scrollTop);
        }
    }

    public void InsertLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        for (int i = 0; i < n; i++)
        {
            for (int row = _scrollBottom; row > CursorY; row--)
                Array.Copy(_cells, (row - 1) * Cols, _cells, row * Cols, Cols);
            ClearRow(CursorY);
        }
    }

    public void DeleteLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        for (int i = 0; i < n; i++)
        {
            for (int row = CursorY; row < _scrollBottom; row++)
                Array.Copy(_cells, (row + 1) * Cols, _cells, row * Cols, Cols);
            ClearRow(_scrollBottom);
        }
    }

    public void InsertChars(int n)
    {
        int rowStart = CursorY * Cols;
        for (int i = 0; i < n; i++)
        {
            for (int col = Cols - 1; col > CursorX; col--)
                _cells[rowStart + col] = _cells[rowStart + col - 1];
            _cells[rowStart + CursorX] = ' ';
        }
    }

    public void DeleteChars(int n)
    {
        int rowStart = CursorY * Cols;
        for (int i = 0; i < n; i++)
        {
            for (int col = CursorX; col < Cols - 1; col++)
                _cells[rowStart + col] = _cells[rowStart + col + 1];
            _cells[rowStart + Cols - 1] = ' ';
        }
    }

    public void EraseInLine(int mode)
    {
        int rowStart = CursorY * Cols;
        switch (mode)
        {
            case 0: for (int c = CursorX; c < Cols; c++) _cells[rowStart + c] = ' '; break;
            case 1: for (int c = 0; c <= CursorX; c++) _cells[rowStart + c] = ' '; break;
            case 2: ClearRow(CursorY); break;
        }
    }

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int r = CursorY + 1; r < Rows; r++) ClearRow(r);
                break;
            case 1:
                EraseInLine(1);
                for (int r = 0; r < CursorY; r++) ClearRow(r);
                break;
            case 2:
            case 3:
                for (int r = 0; r < Rows; r++) ClearRow(r);
                break;
        }
    }

    public void SaveCursor()
    {
        _savedCursorX = CursorX;
        _savedCursorY = CursorY;
    }

    public void RestoreCursor()
    {
        CursorX = _savedCursorX;
        CursorY = _savedCursorY;
        _wrapPending = false;
    }

    public void Reset()
    {
        Array.Fill(_cells, ' ');
        CursorX = 0;
        CursorY = 0;
        _wrapPending = false;
        ResetScrollRegion();
    }

    private void ClearRow(int row)
    {
        Array.Fill(_cells, ' ', row * Cols, Cols);
    }
}
