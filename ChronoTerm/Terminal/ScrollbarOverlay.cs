namespace ChronoTerm.Terminal;

public readonly record struct ScrollbarInfo(bool Visible, int BarCol, int ThumbStart, int ThumbEnd, int TotalContent, int ViewportHeight);

public static class ScrollbarOverlay
{
    private const char ThumbChar = '\u2588'; // full block
    private const char TrackChar = '\u2502'; // light vertical line — both already in FontAtlas's coverage

    /// <summary>Returns a NEW lines array with the last column overwritten by
    /// thumb/track characters when the scrollbar should be visible — does NOT
    /// mutate the input. Deliberately different from main.py here: Python's
    /// last_rendered_lines (used for clipboard copy) IS the post-overlay
    /// result, so copying text that happens to include the last column while
    /// the scrollbar is showing picks up stray thumb/track characters. Keeping
    /// the overlay output separate from the clipboard-source snapshot avoids
    /// that.</summary>
    public static (string[] lines, ScrollbarInfo info) Apply(
        string[] lines, int total, int offset, int height, string mode,
        bool mouseOverBarArea, bool dragging)
    {
        if (lines.Length == 0 || total <= height)
            return (lines, new ScrollbarInfo(false, 0, 0, 0, total, height));

        int barCol = Math.Max(0, lines[0].Length - 1);

        bool shouldShow = mode switch
        {
            "on" => true,
            "off" => false,
            _ => mouseOverBarArea || dragging, // "auto"
        };

        if (!shouldShow)
            return (lines, new ScrollbarInfo(false, barCol, 0, 0, total, height));

        int thumbSize = Math.Max(1, (int)((long)height * height / total));
        int maxOffset = Math.Max(0, total - height);

        int thumbPos;
        if (maxOffset > 0)
        {
            // offset=0 (live view) -> thumb at bottom; offset=maxOffset (oldest) -> thumb at top.
            int visibleTop = maxOffset - offset;
            thumbPos = (int)(visibleTop / (double)maxOffset * (height - thumbSize));
        }
        else
        {
            thumbPos = height - thumbSize;
        }
        thumbPos = Math.Clamp(thumbPos, 0, height - thumbSize);

        var info = new ScrollbarInfo(true, barCol, thumbPos, thumbPos + thumbSize, total, height);

        var outLines = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            char[] chars = lines[i].Length > barCol
                ? lines[i].ToCharArray()
                : PadTo(lines[i], barCol + 1);

            chars[barCol] = (i >= thumbPos && i < thumbPos + thumbSize) ? ThumbChar : TrackChar;
            outLines[i] = new string(chars);
        }

        return (outLines, info);
    }

    private static char[] PadTo(string s, int width)
    {
        var chars = new char[width];
        Array.Fill(chars, ' ');
        s.CopyTo(0, chars, 0, s.Length);
        return chars;
    }

    public static bool IsOverThumb(int charX, int charY, ScrollbarInfo info) =>
        info.Visible && charX >= info.BarCol && charY >= info.ThumbStart && charY < info.ThumbEnd;

    public static bool IsOverBarArea(int charX, int charY, int barCol, int height) =>
        charX >= barCol && charY >= 0 && charY < height;

    /// <summary>Port of calculate_scroll_from_drag, taking an already-computed
    /// character-unit drag delta — the caller (TerminalWindow) does the
    /// pixel-to-character division itself using the real CellHeight, rather
    /// than this method re-deriving or guessing at it.</summary>
    public static int ScrollOffsetFromDrag(double dragDeltaChars, int dragStartOffset, ScrollbarInfo info)
    {
        int maxOffset = Math.Max(0, info.TotalContent - info.ViewportHeight);
        if (maxOffset <= 0) return 0;

        int thumbSize = info.ThumbEnd - info.ThumbStart;
        int scrollableArea = Math.Max(1, info.ViewportHeight - thumbSize);
        double contentPerChar = maxOffset / (double)scrollableArea;
        int offsetDelta = (int)(dragDeltaChars * contentPerChar);

        // Positive drag (moving down) should decrease scroll offset (toward live view).
        int newOffset = dragStartOffset - offsetDelta;
        return Math.Clamp(newOffset, 0, maxOffset);
    }
}
