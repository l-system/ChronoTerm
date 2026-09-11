using System.Text;

namespace ChronoTerm.Terminal;

public interface IVtScreenHost
{
    TerminalScreen Screen { get; }
    void EnterAltScreen();
    void ExitAltScreen();
    void SetWindowTitle(string title); // OSC 0/1/2

    /// <summary>Writes an escape-sequence reply back to the pty's input side —
    /// used for the handful of queries a well-behaved terminal is expected to
    /// actually answer (Primary Device Attributes, Device Status Report,
    /// Cursor Position Report). Without this, nothing ever answered any of
    /// them: fish (and other programs that probe terminal capabilities on
    /// startup) would send a DA1 query and then sit waiting for a reply that
    /// never comes, hanging for its own timeout before giving up — bash
    /// happens not to probe this by default, which is why the same binary
    /// looks fine under one shell and stalls for several seconds under
    /// another.</summary>
    void WriteResponse(ReadOnlySpan<byte> bytes);
}

/// <summary>
/// Feeds raw pty bytes through an incremental UTF-8 decoder (byte chunk
/// boundaries from a pty read have no relationship to UTF-8 character
/// boundaries — same problem PTYProcess.py's _utf8_decoder comment describes)
/// and drives a small ANSI/VT100/XTerm state machine on the decoded chars.
/// </summary>
public sealed class VtParser
{
    private enum State { Ground, Escape, Csi, Osc, OscEscape, Charset }

    private readonly IVtScreenHost _host;
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private State _state = State.Ground;
    private readonly List<int> _params = new();
    private int _currentParam = -1;
    private bool _privateMarker; // '?' prefix, e.g. CSI ?1049h
    private readonly StringBuilder _oscBuffer = new(); // accumulates "Ps;Pt" until BEL/ST

    // DEC Special Graphics (line-drawing) charset. ESC ( X designates G0, ESC ) X
    // designates G1 — they don't switch what's on screen by themselves. SO (0x0E)
    // invokes G1, SI (0x0F) invokes G0 back; ncurses apps (nano, htop's box borders)
    // designate G1 as line-drawing once via ESC ) 0 and then toggle with SO/SI for
    // every border character, leaving G0 as plain ASCII the rest of the time.
    private char _charsetTarget; // '(' or ')' — which set the pending designation is for
    private bool _g0IsLineDrawing;
    private bool _g1IsLineDrawing;
    private bool _invokedG1; // SO/SI shift state: false = G0 invoked, true = G1 invoked
    private bool _lineDrawingActive => _invokedG1 ? _g1IsLineDrawing : _g0IsLineDrawing;
    private static readonly Dictionary<char, char> LineDrawingMap = new()
    {
        ['j'] = '\u2518', ['k'] = '\u2510', ['l'] = '\u250c', ['m'] = '\u2514',
        ['n'] = '\u253c', ['q'] = '\u2500', ['t'] = '\u251c', ['u'] = '\u2524',
        ['v'] = '\u2534', ['w'] = '\u252c', ['x'] = '\u2502', ['a'] = '\u2592',
    };

    public VtParser(IVtScreenHost host) => _host = host;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        // Worst case one char per byte; incremental decoder carries partial
        // multi-byte sequences over to the next Feed() call internally.
        Span<char> chars = bytes.Length <= 4096 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        int charCount = _utf8Decoder.GetChars(bytes, chars, flush: false);
        for (int i = 0; i < charCount; i++)
            FeedChar(chars[i]);
    }

    private void FeedChar(char c)
    {
        var screen = _host.Screen;

        switch (_state)
        {
            case State.Ground:
                HandleGround(c, screen);
                break;

            case State.Escape:
                HandleEscape(c, screen);
                break;

            case State.Csi:
                HandleCsi(c, screen);
                break;

            case State.Osc:
                if (c == '\a') { FinishOsc(); _state = State.Ground; }
                else if (c == '\x1b') { _state = State.OscEscape; } // possible ST start
                else { _oscBuffer.Append(c); }
                break;

            case State.OscEscape:
                // ST is ESC \ — anything else here is malformed input, bail to
                // Ground rather than trying to reinterpret it as a fresh escape.
                if (c == '\\') FinishOsc();
                _state = State.Ground;
                break;

            case State.Charset:
                bool isLineDrawing = c == '0';
                if (_charsetTarget == ')') _g1IsLineDrawing = isLineDrawing;
                else _g0IsLineDrawing = isLineDrawing;
                _state = State.Ground;
                break;
        }
    }

    /// <summary>Parses accumulated "Ps;Pt" and, for Ps in {0,1,2} (icon+title,
    /// icon, title — xterm convention), forwards Pt to the host as the new
    /// window title. Any other Ps (working directory hints, clipboard OSC 52,
    /// etc.) is intentionally still a no-op — only title-setting is wired up.</summary>
    private void FinishOsc()
    {
        string content = _oscBuffer.ToString();
        _oscBuffer.Clear();

        int semi = content.IndexOf(';');
        if (semi <= 0) return;
        if (!int.TryParse(content.AsSpan(0, semi), out int ps)) return;
        if (ps is 0 or 1 or 2)
            _host.SetWindowTitle(content[(semi + 1)..]);
    }

    private void HandleGround(char c, TerminalScreen screen)
    {
        switch (c)
        {
            case '\x1b': _state = State.Escape; break;
            case '\r': screen.CarriageReturn(); break;
            case '\n': screen.LineFeed(); break;
            case '\b': screen.Backspace(); break;
            case '\t': screen.Tab(); break;
            case '\a': break; // BEL — no title bar / no bell sound to trigger
            case '\0': break;
            case '\x0e': _invokedG1 = true; break;  // SO — shift to G1
            case '\x0f': _invokedG1 = false; break; // SI — shift to G0
            default:
                if (c >= ' ')
                {
                    if (_lineDrawingActive && LineDrawingMap.TryGetValue(c, out var mapped))
                        screen.PutChar(mapped);
                    else
                        screen.PutChar(c);
                }
                break;
        }
    }

    private void HandleEscape(char c, TerminalScreen screen)
    {
        switch (c)
        {
            case '[': _state = State.Csi; _params.Clear(); _currentParam = -1; _privateMarker = false; break;
            case ']': _state = State.Osc; _oscBuffer.Clear(); break;
            case '(' or ')': _charsetTarget = c; _state = State.Charset; break;
            case '7': screen.SaveCursor(); _state = State.Ground; break;
            case '8': screen.RestoreCursor(); _state = State.Ground; break;
            case 'D': screen.LineFeed(); _state = State.Ground; break;       // IND
            case 'M': screen.ReverseLineFeed(); _state = State.Ground; break; // RI
            case 'c': screen.Reset(); _state = State.Ground; break;          // RIS
            default: _state = State.Ground; break;
        }
    }

    private void HandleCsi(char c, TerminalScreen screen)
    {
        if (c == '?' && _params.Count == 0 && _currentParam == -1)
        {
            _privateMarker = true;
            return;
        }

        if (c is >= '0' and <= '9')
        {
            if (_currentParam == -1) _currentParam = 0;
            _currentParam = _currentParam * 10 + (c - '0');
            return;
        }

        if (c == ';')
        {
            _params.Add(_currentParam == -1 ? 0 : _currentParam);
            _currentParam = -1;
            return;
        }

        // Final byte — dispatch.
        if (_currentParam != -1) _params.Add(_currentParam);
        int P(int index, int def = 0) => index < _params.Count ? _params[index] : def;
        int P1(int index) => P(index, 1) == 0 ? 1 : P(index, 1); // 1-based default, 0 also means 1

        if (_privateMarker)
        {
            // Only the handful of private modes ChronoTerm actually needs:
            // alt-screen (1049/47), cursor visibility (25), and application
            // cursor keys (1 — DECCKM).
            int mode = P(0);
            if (c == 'h') // set
            {
                if (mode is 1049 or 47) _host.EnterAltScreen();
                else if (mode == 25) screen.CursorVisible = true;
                else if (mode == 1) screen.ApplicationCursorKeys = true;
            }
            else if (c == 'l') // reset
            {
                if (mode is 1049 or 47) _host.ExitAltScreen();
                else if (mode == 25) screen.CursorVisible = false;
                else if (mode == 1) screen.ApplicationCursorKeys = false;
            }
        }
        else
        {
            switch (c)
            {
                case 'H' or 'f': screen.SetCursorPos(P1(0) - 1, P1(1) - 1); break;
                case 'A': screen.MoveCursorRelative(0, -P1(0)); break;
                case 'B': screen.MoveCursorRelative(0, P1(0)); break;
                case 'C': screen.MoveCursorRelative(P1(0), 0); break;
                case 'D': screen.MoveCursorRelative(-P1(0), 0); break;
                case 'G': screen.SetCursorPos(screen.CursorY, P1(0) - 1); break;
                case 'd': screen.SetCursorPos(P1(0) - 1, screen.CursorX); break;
                case 'J': screen.EraseInDisplay(P(0)); break;
                case 'K': screen.EraseInLine(P(0)); break;
                case 'L': screen.InsertLines(P1(0)); break;
                case 'M': screen.DeleteLines(P1(0)); break;
                case 'P': screen.DeleteChars(P1(0)); break;
                case '@': screen.InsertChars(P1(0)); break;
                case 'S': screen.ScrollUp(P1(0)); break;
                case 'T': screen.ScrollDown(P1(0)); break;
                case 'r':
                    if (_params.Count >= 2) screen.SetScrollRegion(P1(0) - 1, P(1) - 1);
                    else screen.ResetScrollRegion();
                    break;
                // 'm' (SGR/color) intentionally not handled — see TerminalScreen
                // doc comment; ChronoTerm never reads per-cell color.

                // Primary Device Attributes (CSI c / CSI 0 c) — "what are you?"
                // "?1;2c" is the classic xterm-family answer (VT100 + Advanced
                // Video Option); it's less about literal accuracy and more
                // about being A recognized, well-formed answer so callers
                // asking "does anything answer at all?" get a prompt yes.
                case 'c': _host.WriteResponse("\x1b[?1;2c"u8); break;

                // Device Status Report. Ps=5 ("are you OK?") -> "yes" (0n).
                // Ps=6 (Cursor Position Report) -> actual 1-based row;col,
                // which is why this needs to happen here rather than as a
                // fire-and-forget constant like the other two.
                case 'n':
                    if (P(0) == 5) _host.WriteResponse("\x1b[0n"u8);
                    else if (P(0) == 6) _host.WriteResponse(Encoding.ASCII.GetBytes($"\x1b[{screen.CursorY + 1};{screen.CursorX + 1}R"));
                    break;
            }
        }

        _state = State.Ground;
        _params.Clear();
        _currentParam = -1;
        _privateMarker = false;
    }
}