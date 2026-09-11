using Silk.NET.Input;

namespace ChronoTerm.Input;

/// <summary>
/// Ports input_handler.py's key_callback + key_to_char. Silk.NET's IKeyboard gives
/// us KeyDown(Key, scancode) for control/navigation keys and KeyChar(char) for
/// printable text (which already accounts for shift/layout, so we don't hand-roll
/// the shift maps the Python version needed for raw SDL keycodes).
/// </summary>
public static class KeyTranslator
{
    /// <summary>Ctrl/Alt chords and non-printable special keys. Returns null if the
    /// key isn't one of these (caller should fall back to text input for it).</summary>
    /// <param name="applicationCursorKeys">DECCKM state (CSI ?1h/?1l) of the
    /// currently active screen — see TerminalScreen.ApplicationCursorKeys.
    /// Only affects the four arrow keys: Home/End/PageUp/PageDown etc. aren't
    /// governed by DECCKM in the standard sense and keep one fixed encoding
    /// regardless (matches xterm's own behavior).</param>
    public static byte[]? TranslateKeyDown(Key key, KeyModifiers mods, bool applicationCursorKeys = false)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt);

        if (ctrl)
        {
            byte[]? ctrlBytes = key switch
            {
                Key.C => new byte[] { 0x03 },
                Key.D => new byte[] { 0x04 },
                Key.Z => new byte[] { 0x1a },
                Key.L => new byte[] { 0x0c },
                Key.U => new byte[] { 0x15 },
                Key.K => new byte[] { 0x0b },
                Key.A => new byte[] { 0x01 },
                Key.E => new byte[] { 0x05 },
                Key.W => new byte[] { 0x17 },
                Key.R => new byte[] { 0x12 },
                Key.P => new byte[] { 0x10 },
                Key.N => new byte[] { 0x0e },
                Key.F => new byte[] { 0x06 },
                Key.B => new byte[] { 0x02 },
                Key.X => new byte[] { 0x18 },
                Key.G => new byte[] { 0x07 },
                Key.O => new byte[] { 0x0f },
                _ => null,
            };
            if (ctrlBytes is not null) return ctrlBytes;
        }

        if (alt)
        {
            byte[]? altBytes = key switch
            {
                Key.F => "\x1bf"u8.ToArray(),
                Key.B => "\x1bb"u8.ToArray(),
                Key.D => "\x1bd"u8.ToArray(),
                _ => null,
            };
            if (altBytes is not null) return altBytes;
        }

        return key switch
        {
            Key.Up => (applicationCursorKeys ? "\x1bOA"u8 : "\x1b[A"u8).ToArray(),
            Key.Down => (applicationCursorKeys ? "\x1bOB"u8 : "\x1b[B"u8).ToArray(),
            Key.Right => (applicationCursorKeys ? "\x1bOC"u8 : "\x1b[C"u8).ToArray(),
            Key.Left => (applicationCursorKeys ? "\x1bOD"u8 : "\x1b[D"u8).ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.PageUp => "\x1b[5~"u8.ToArray(),
            Key.PageDown => "\x1b[6~"u8.ToArray(),
            Key.Insert => "\x1b[2~"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            Key.F1 => "\x1bOP"u8.ToArray(),
            Key.F2 => "\x1bOQ"u8.ToArray(),
            Key.F3 => "\x1bOR"u8.ToArray(),
            Key.F4 => "\x1bOS"u8.ToArray(),
            Key.F5 => "\x1b[15~"u8.ToArray(),
            Key.F6 => "\x1b[17~"u8.ToArray(),
            Key.F7 => "\x1b[18~"u8.ToArray(),
            Key.F8 => "\x1b[19~"u8.ToArray(),
            Key.F9 => "\x1b[20~"u8.ToArray(),
            Key.F10 => "\x1b[21~"u8.ToArray(),
            Key.F11 => "\x1b[23~"u8.ToArray(),
            Key.F12 => "\x1b[24~"u8.ToArray(),
            Key.Enter => new byte[] { (byte)'\r' },
            Key.Backspace => new byte[] { 0x7f },
            Key.Tab => new byte[] { (byte)'\t' },
            Key.Escape => new byte[] { 0x1b },
            _ => null,
        };
    }

    /// <summary>Scrollback / clipboard chords main.py intercepts before the shell
    /// ever sees them (PageUp/Down without alt-screen, Ctrl+Shift+C/V). Handled by
    /// the caller, not forwarded to the pty — kept here only as the shared key set
    /// so both layers reference the same source of truth.</summary>
    public static bool IsScrollKey(Key key) =>
        key is Key.PageUp or Key.PageDown or Key.Home or Key.End;
}
