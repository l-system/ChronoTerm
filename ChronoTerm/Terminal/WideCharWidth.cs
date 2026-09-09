namespace ChronoTerm.Terminal;

/// <summary>
/// Determines whether a character occupies 2 terminal cells instead of 1 —
/// the standard "East Asian Wide/Fullwidth" categories from Unicode's
/// East_Asian_Width property. This only affects cursor advance / layout
/// (see TerminalScreen.PutChar) — it does NOT mean these characters actually
/// render a glyph. Our font atlas is a small, fixed set rasterized once at
/// startup (ASCII + box-drawing + braille); pre-rasterizing all of CJK
/// (tens of thousands of codepoints) isn't practical with that design. Wide
/// characters render as correctly-spaced blank cells, not visible glyphs —
/// true CJK glyph rendering would need a dynamically-built atlas, which is a
/// bigger architectural change than this fixes.
/// </summary>
public static class WideCharWidth
{
    public static bool IsWide(char c)
    {
        int cp = c;
        return cp is
            (>= 0x1100 and <= 0x115F) or   // Hangul Jamo
            (>= 0x2E80 and <= 0x303E) or   // CJK Radicals, Kangxi, CJK Symbols/Punctuation
            (>= 0x3041 and <= 0x33FF) or   // Hiragana, Katakana, CJK Compat, Enclosed CJK
            (>= 0x3400 and <= 0x4DBF) or   // CJK Unified Ideographs Extension A
            (>= 0x4E00 and <= 0x9FFF) or   // CJK Unified Ideographs
            (>= 0xA000 and <= 0xA4CF) or   // Yi Syllables/Radicals
            (>= 0xAC00 and <= 0xD7A3) or   // Hangul Syllables
            (>= 0xF900 and <= 0xFAFF) or   // CJK Compatibility Ideographs
            (>= 0xFE30 and <= 0xFE4F) or   // CJK Compatibility Forms
            (>= 0xFF00 and <= 0xFF60) or   // Fullwidth Forms
            (>= 0xFFE0 and <= 0xFFE6);     // Fullwidth Signs
    }
}
