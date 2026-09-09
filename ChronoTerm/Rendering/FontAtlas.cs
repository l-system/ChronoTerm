using Silk.NET.OpenGL;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ChronoTerm.Rendering;

/// <summary>
/// Fixed-cell (monospace) glyph atlas. Covers ASCII 32..126, plus three
/// Unicode blocks modern TUI apps lean on heavily and the old 12-char
/// legacy subset didn't: full Box Drawing (U+2500-257F), Block Elements
/// (U+2580-259F), and Braille Patterns (U+2800-28FF).
///
/// If a configured font lacks glyphs for a given codepoint, that cell still
/// renders blank — this only fixes the atlas-slot gap, not font coverage.
///
/// Cell height comes from the font's horizontal line-height metric,
/// scaled from font design units to the configured font size.
/// </summary>
public sealed class FontAtlas : IDisposable
{
    private readonly GL _gl;

    public uint TextureHandle { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public int AtlasWidth { get; }
    public int AtlasHeight { get; }

    private const int FirstChar = 32;
    private const int AsciiCount = 95; // 32..126 inclusive
    private const int ColsPerRow = 16;

    // Full Unicode blocks used heavily by terminal applications.
    private const int BoxDrawingStart = 0x2500;
    private const int BoxDrawingCount = 0x80; // U+2500-257F

    private const int BlockElementsStart = 0x2580;
    private const int BlockElementsCount = 0x20; // U+2580-259F

    private const int BrailleStart = 0x2800;
    private const int BrailleCount = 0x100; // U+2800-28FF

    private readonly Dictionary<char, (float u, float v, float w, float h)> _charInfo = new();

    public FontAtlas(
        GL gl,
        string fontPath,
        int fontSize,
        double charSpacing = 1.0,
        double lineSpacing = 1.0)
    {
        _gl = gl;

        var collection = new FontCollection();
        var family = collection.Add(fontPath);
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var options = new TextOptions(font);

        // "M" as the reference glyph for cell width.
        // This is the standard monospace convention.
        var advance = TextMeasurer.MeasureAdvance("M", options);

        CellWidth = Math.Max(
            1,
            (int)MathF.Ceiling(
                (float)(advance.Width * charSpacing)));

        // SixLabors.Fonts 2.x exposes horizontal typographic metrics through
        // FontMetrics.HorizontalMetrics.
        //
        // LineHeight is expressed in font design units, so convert it to
        // pixels using UnitsPerEm and the configured font size.
        var metrics = font.FontMetrics;
        var horizontalMetrics = metrics.HorizontalMetrics;

        float unitsPerEm = metrics.UnitsPerEm;

        float naturalLineHeight = unitsPerEm > 0
            ? horizontalMetrics.LineHeight * (fontSize / unitsPerEm)
            : fontSize * 1.2f;

        if (naturalLineHeight <= 0 ||
            float.IsNaN(naturalLineHeight) ||
            float.IsInfinity(naturalLineHeight))
        {
            naturalLineHeight = fontSize * 1.2f;
        }

        CellHeight = Math.Max(
            1,
            (int)MathF.Ceiling(
                naturalLineHeight * (float)lineSpacing));

        var allChars = new List<char>(
            AsciiCount +
            BoxDrawingCount +
            BlockElementsCount +
            BrailleCount);

        for (int i = 0; i < AsciiCount; i++)
        {
            allChars.Add((char)(FirstChar + i));
        }

        for (int i = 0; i < BoxDrawingCount; i++)
        {
            allChars.Add((char)(BoxDrawingStart + i));
        }

        for (int i = 0; i < BlockElementsCount; i++)
        {
            allChars.Add((char)(BlockElementsStart + i));
        }

        for (int i = 0; i < BrailleCount; i++)
        {
            allChars.Add((char)(BrailleStart + i));
        }

        int rows = (int)Math.Ceiling(
            allChars.Count / (double)ColsPerRow);

        AtlasWidth = CellWidth * ColsPerRow;
        AtlasHeight = CellHeight * rows;

        using var atlasImage =
            new Image<Rgba32>(AtlasWidth, AtlasHeight);

        // Render every glyph into its own tile first.
        //
        // This guarantees that a glyph cannot bleed into the neighboring
        // atlas row if its actual ink extends beyond the calculated cell
        // height. ImageSharp clips drawing to the glyph tile's bounds.
        using var glyphTile =
            new Image<Rgba32>(CellWidth, CellHeight);

        for (int i = 0; i < allChars.Count; i++)
        {
            char c = allChars[i];

            int col = i % ColsPerRow;
            int row = i / ColsPerRow;

            int px = col * CellWidth;
            int py = row * CellHeight;

            glyphTile.Mutate(ctx =>
            {
                ctx.Clear(Color.Transparent);

                ctx.DrawText(
                    c.ToString(),
                    font,
                    Color.White,
                    new PointF(0, 0));
            });

            atlasImage.Mutate(ctx =>
            {
                ctx.DrawImage(
                    glyphTile,
                    new Point(px, py),
                    1f);
            });

            _charInfo[c] = (
                (float)px / AtlasWidth,
                (float)py / AtlasHeight,
                (float)CellWidth / AtlasWidth,
                (float)CellHeight / AtlasHeight);
        }

        var pixels = new byte[
            AtlasWidth *
            AtlasHeight *
            4];

        atlasImage.CopyPixelDataTo(pixels);

        TextureHandle = _gl.GenTexture();

        _gl.BindTexture(
            TextureTarget.Texture2D,
            TextureHandle);

        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)GLEnum.Nearest);

        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)GLEnum.Nearest);

        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);

        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);

        unsafe
        {
            fixed (byte* p = pixels)
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)AtlasWidth,
                    (uint)AtlasHeight,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    p);
            }
        }

        _gl.BindTexture(
            TextureTarget.Texture2D,
            0);
    }

    /// <summary>
    /// Returns the UV rectangle for a character, or null if the character
    /// does not have an atlas slot.
    /// </summary>
    public (float u, float v, float w, float h)? GetUv(char c) =>
        _charInfo.TryGetValue(c, out var uv)
            ? uv
            : null;

    public void Dispose() =>
        _gl.DeleteTexture(TextureHandle);
}