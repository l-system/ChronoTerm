using Silk.NET.OpenGL;
using System.Runtime.InteropServices;

namespace ChronoTerm.Rendering;

/// <summary>
/// Stage B: real glow pipeline. Pass 1 draws a WHITE glyph+cursor mask into
/// glow_fbo (composite.frag applies textTint downstream, not the glyph draw —
/// matches Python's render_batch(..., (1,1,1))). Passes 2-3 separably blur that
/// mask (blur_fbo1 horizontal, blur_fbo2 vertical). Passes 4-5 composite the
/// blurred glow onto the screen (once normally blended with the background,
/// once again purely additive — Python does this exact double-draw for a
/// blended halo + an additive bloom on top). Pass 6 is our own selection quad
/// (not Python's selection.frag — see conversation notes). Pass 7 draws crisp
/// unblurred text from the RAW glow_fbo on top of everything so text stays
/// readable regardless of blur amount.
///
/// Vignette/curvature/scanlines/burn-in are wired into the composite shader
/// (matches composite.frag verbatim) but hardcoded OFF in this stage — Stage C
/// wires burn-in, Stage D wires the CRT-effect toggles from config.
/// </summary>
/// <summary>Bundles the CRT-effect toggles composite.frag supports — mirrors
/// how Python threads these through via **kwargs to _set_composite_uniforms,
/// identical across all three composite draws in one frame.</summary>
public readonly record struct CrtEffects(
    bool VignetteEnabled, float VignetteStrength,
    bool CurvatureEnabled, float CurvatureAmount,
    bool ScanlinesEnabled, float ScanlineIntensity, float ScanlineFrequency,
    float CornerRadius)
{
    public static readonly CrtEffects Off = new(false, 0f, false, 0f, false, 0f, 0f, 0f);
}

public sealed class TerminalGlRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly FontAtlas _atlas;

    private readonly uint _textProgram;
    private readonly uint _solidProgram;
    private readonly uint _blurProgram;
    private readonly uint _compositeProgram;

    private readonly uint _textVao, _textVbo, _textEbo;
    private readonly uint _solidVao, _solidVbo;

    private readonly List<float> _vertexScratch = new();
    private readonly List<uint> _indexScratch = new();

    private int _screenWidth = 1, _screenHeight = 1;
    private int _paddingLeft, _paddingTop;

    private Framebuffer? _glowFbo;
    private Framebuffer? _blurFbo1;
    private Framebuffer? _blurFbo2;

    // Ping-ponged persistent accumulation buffer — unlike glow/blur, these are
    // NOT cleared every frame (that would defeat the whole point of "trail
    // persists across frames"). They only get cleared once, at creation.
    private Framebuffer? _burninFbo;
    private Framebuffer? _burninFboPrev;
    private readonly uint _burninProgram;

    private readonly FullscreenQuad _fullscreenQuad;

    // Normalizes blur radius/glow spread against font cell size — the effect
    // defaults were tuned against a 32px reference cell. Ports renderer.py's
    // glow_scale_factor/glow_intensity_scale exactly.
    private readonly float _glowScaleFactor;
    private readonly float _glowIntensityScale;

    public int CellWidth => _atlas.CellWidth;
    public int CellHeight => _atlas.CellHeight;

    public TerminalGlRenderer(GL gl, string fontPath, int fontSize, double charSpacing = 1.0, double lineSpacing = 1.0)
    {
        _gl = gl;
        _atlas = new FontAtlas(gl, fontPath, fontSize, charSpacing, lineSpacing);

        // Settings overlay gets its OWN small, fixed-size atlas — deliberately
        // NOT tied to the terminal's font size. Reusing the terminal atlas
        // meant the overlay's glyph cells were however big the user's terminal
        // font happened to be (e.g. 28pt), which made per-glyph tile-alignment
        // imprecision much more visible, and made it impossible to size the
        // panel as a clean percentage of window size. Generous lineSpacing
        // (1.3) gives breathing room between rows.
        _overlayAtlas = new FontAtlas(gl, fontPath, OverlayFontSize, 1.0, 1.3);

        _textProgram = Shader.Build(gl, TextVertexSrc, TextFragmentSrc);
        _solidProgram = Shader.Build(gl, SolidVertexSrc, SolidFragmentSrc);
        _blurProgram = Shader.Build(gl, BlurVertexSrc, BlurFragmentSrc);
        _compositeProgram = Shader.Build(gl, CompositeVertexSrc, CompositeFragmentSrc);
        _burninProgram = Shader.Build(gl, BurninVertexSrc, BurninFragmentSrc);

        (_textVao, _textVbo, _textEbo) = CreateTextBuffers();
        (_solidVao, _solidVbo) = CreateSolidBuffers();
        _fullscreenQuad = new FullscreenQuad(gl);

        _glowScaleFactor = Math.Max(CellWidth, CellHeight) / 32f;
        _glowIntensityScale = MathF.Min(1f, 1f / MathF.Sqrt(MathF.Max(_glowScaleFactor, 1e-6f)));
    }

    private const int OverlayFontSize = 16;
    private readonly FontAtlas _overlayAtlas;

    public void Resize(int pixelWidth, int pixelHeight)
    {
        _screenWidth = Math.Max(1, pixelWidth);
        _screenHeight = Math.Max(1, pixelHeight);

        if (_glowFbo is null)
        {
            _glowFbo = new Framebuffer(_gl, _screenWidth, _screenHeight);
            _blurFbo1 = new Framebuffer(_gl, _screenWidth, _screenHeight);
            _blurFbo2 = new Framebuffer(_gl, _screenWidth, _screenHeight);

            _burninFbo = new Framebuffer(_gl, _screenWidth, _screenHeight);
            _burninFboPrev = new Framebuffer(_gl, _screenWidth, _screenHeight);
            ClearOpaqueBlack(_burninFbo);
            ClearOpaqueBlack(_burninFboPrev);
        }
        else
        {
            _glowFbo.Resize(_screenWidth, _screenHeight);
            _blurFbo1!.Resize(_screenWidth, _screenHeight);
            _blurFbo2!.Resize(_screenWidth, _screenHeight);

            // Resizing tears down and recreates the texture, which incidentally
            // resets accumulated burn-in trail on a window resize — same
            // simplification Python effectively falls into via its own FBO
            // recreation path. Not worth preserving across a resize.
            _burninFbo!.Resize(_screenWidth, _screenHeight);
            _burninFboPrev!.Resize(_screenWidth, _screenHeight);
            ClearOpaqueBlack(_burninFbo);
            ClearOpaqueBlack(_burninFboPrev);
        }
    }

    /// <summary>Matches Python's burnin_fbo.clear(0,0,0,1) — opaque black, not
    /// transparent, since burn intensity lives in the R channel and an alpha
    /// hole here would be meaningless for this buffer.</summary>
    private void ClearOpaqueBlack(Framebuffer fbo)
    {
        fbo.Bind();
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    /// <summary>Pixel offset applied to every drawn quad — config['window']['padding'],
    /// so text doesn't start flush against the window edge.</summary>
    public void SetPadding(int left, int top)
    {
        _paddingLeft = left;
        _paddingTop = top;
    }

    /// <param name="lines">One string per visible row, already padded/truncated to
    /// column count by TerminalSession.GetSnapshot.</param>
    /// <param name="glowIntensity">config.effects.glow.intensity — 0 skips the
    /// blur passes entirely, not just zeroes their visual contribution.</param>
    /// <param name="glowSpread">config.effects.glow.spread.</param>
    /// <param name="selectionSpans">Per-row (row, startCol, endCol) highlight
    /// ranges — drawn behind the crisp-text pass, over the glow.</param>
    public void RenderFrame(
        string[] lines, int cursorX, int cursorY, bool cursorVisible,
        (float r, float g, float b) textColor, (float r, float g, float b, float a) backgroundColor,
        float glowIntensity, float glowSpread, (float r, float g, float b) glowTint,
        double deltaSeconds, bool burnInEnabled, float burnInBuild, float burnInFadeTime,
        float burnInOpacity, (float r, float g, float b, float a) burnInTint,
        IReadOnlyList<(int row, int startCol, int endCol)>? selectionSpans = null,
        (float r, float g, float b, float a)? selectionColor = null,
        CrtEffects crt = default,
        char cursorChar = '\u2588')
    {
        if (_glowFbo is null || _blurFbo1 is null || _blurFbo2 is null) return; // Resize() hasn't run yet
        if (_burninFbo is null || _burninFboPrev is null) return;

        // --- Pass 1: WHITE glyph + cursor mask into glow_fbo ---
        _glowFbo.Bind();
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        DrawGlyphs(lines, (1f, 1f, 1f), cursorVisible ? (cursorChar, cursorX, cursorY) : null);

        // --- Burn-in accumulation (ping-pong) ---
        // Deliberately NOT cleared before drawing — the shader computes a full
        // replacement value per pixel from prev-trail + current-frame, so
        // there's nothing to clear; clearing would just be wasted work.
        if (burnInEnabled)
        {
            _burninFbo.Bind();
            _gl.Disable(EnableCap.Blend);

            float decay = burnInFadeTime > 0f
                ? MathF.Exp((float)(-deltaSeconds / burnInFadeTime))
                : 0.985f; // matches Python's fallback when fade_time isn't usable
            decay = Math.Clamp(decay, 0f, 1f);

            _gl.UseProgram(_burninProgram);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _glowFbo.TextureHandle);
            SetUniform1(_burninProgram, "currentFrame", 0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _burninFboPrev.TextureHandle);
            SetUniform1(_burninProgram, "previousBurnin", 1);
            SetUniform1(_burninProgram, "build", burnInBuild);
            SetUniform1(_burninProgram, "decay", decay);
            _fullscreenQuad.Draw();

            // Ping-pong swap: the buffer we just wrote becomes "prev" for
            // compositing below (and for next frame's accumulation read).
            (_burninFbo, _burninFboPrev) = (_burninFboPrev, _burninFbo);

            _gl.Enable(EnableCap.Blend);
        }

        bool glowOn = glowIntensity > 0f;
        float scaledSpread = glowSpread * _glowScaleFactor;

        // --- Passes 2-3: separable blur of the glow mask (skipped when glow is off) ---
        if (glowOn)
        {
            _blurFbo1.Bind();
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            _gl.Disable(EnableCap.Blend);
            DrawBlurPass(_glowFbo.TextureHandle, horizontal: true);

            _blurFbo2.Bind();
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            DrawBlurPass(_blurFbo1.TextureHandle, horizontal: false);

            _gl.Enable(EnableCap.Blend);
        }

        uint blurredGlow = glowOn ? _blurFbo2.TextureHandle : _glowFbo.TextureHandle;
        uint burninTexture = _burninFboPrev.TextureHandle;

        // --- Pass 4: background + blurred glow, blended onto the screen ---
        Framebuffer.BindDefault(_gl, _screenWidth, _screenHeight);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.Enable(EnableCap.Blend);

        if (backgroundColor.a > 0f)
        {
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            DrawComposite(blurredGlow, burninTexture, backgroundColor, glowIntensity * _glowIntensityScale, scaledSpread,
                textColor, glowTint, burnInEnabled, burnInOpacity, burnInTint, crt);
        }

        // --- Pass 5: additive glow bloom on top ---
        if (glowOn)
        {
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            DrawComposite(_blurFbo2.TextureHandle, burninTexture, (0f, 0f, 0f, 0f), glowIntensity, scaledSpread,
                textColor, glowTint, burnInEnabled, burnInOpacity, burnInTint, crt);
        }

        // --- Pass 6: selection highlight (our own quad, not selection.frag) ---
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (selectionSpans is { Count: > 0 })
            DrawSelectionSpans(selectionSpans, selectionColor ?? (textColor.r, textColor.g, textColor.b, 0.3f));

        // --- Pass 7: crisp, unblurred text on top of everything ---
        DrawComposite(_glowFbo.TextureHandle, burninTexture, (0f, 0f, 0f, 0f), 0f, 0f,
            textColor, glowTint, burnInEnabled, burnInOpacity, burnInTint, crt);
    }

    private void DrawBlurPass(uint sourceTexture, bool horizontal)
    {
        _gl.UseProgram(_blurProgram);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sourceTexture);
        SetUniform1(_blurProgram, "screenTexture", 0);
        SetUniform2(_blurProgram, "texelSize", 1f / _screenWidth, 1f / _screenHeight);
        SetUniformBool(_blurProgram, "horizontal", horizontal);
        SetUniform1(_blurProgram, "intensity", 5f * _glowScaleFactor);
        _fullscreenQuad.Draw();
    }

    private void DrawComposite(
        uint textTexture, uint burninTexture, (float r, float g, float b, float a) backgroundColor,
        float glowIntensity, float glowSpread,
        (float r, float g, float b) textTint, (float r, float g, float b) glowTint,
        bool enableBurnIn, float burnInOpacity, (float r, float g, float b, float a) burnInTint,
        CrtEffects crt)
    {
        _gl.UseProgram(_compositeProgram);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, textTexture);
        SetUniform1(_compositeProgram, "textTexture", 0);

        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, burninTexture);
        SetUniform1(_compositeProgram, "burninTexture", 1);

        SetUniform4(_compositeProgram, "backgroundColor", backgroundColor.r, backgroundColor.g, backgroundColor.b, backgroundColor.a);
        SetUniform1(_compositeProgram, "glowIntensity", glowIntensity);
        SetUniform1(_compositeProgram, "glowSpread", glowSpread);
        SetUniform1(_compositeProgram, "cornerRadius", crt.CornerRadius);
        SetUniform2(_compositeProgram, "resolution", _screenWidth, _screenHeight);
        SetUniform2(_compositeProgram, "texelSize", 1f / _screenWidth, 1f / _screenHeight);
        SetUniform3(_compositeProgram, "textTint", textTint.r, textTint.g, textTint.b);
        SetUniform3(_compositeProgram, "glowTint", glowTint.r, glowTint.g, glowTint.b);

        SetUniform1(_compositeProgram, "vignetteStrength", crt.VignetteStrength);
        SetUniform1(_compositeProgram, "curvatureAmount", crt.CurvatureAmount);
        SetUniform1(_compositeProgram, "scanlineIntensity", crt.ScanlineIntensity);
        SetUniform1(_compositeProgram, "scanlineFrequency", crt.ScanlineFrequency);
        SetUniform1(_compositeProgram, "burnInOpacity", burnInOpacity);
        SetUniform4(_compositeProgram, "burnInTint", burnInTint.r, burnInTint.g, burnInTint.b, burnInTint.a);

        SetUniformBool(_compositeProgram, "enableVignette", crt.VignetteEnabled);
        SetUniformBool(_compositeProgram, "enableBurnIn", enableBurnIn);
        SetUniformBool(_compositeProgram, "enableCurvature", crt.CurvatureEnabled);
        SetUniformBool(_compositeProgram, "enableScanlines", crt.ScanlinesEnabled);

        _fullscreenQuad.Draw();
    }

    private void DrawSolidRectPx(float x0, float y0, float x1, float y1, (float r, float g, float b, float a) color)
    {
        Span<float> verts = stackalloc float[12];
        verts[0] = x0; verts[1] = y0; verts[2] = x1; verts[3] = y0; verts[4] = x1; verts[5] = y1;
        verts[6] = x1; verts[7] = y1; verts[8] = x0; verts[9] = y1; verts[10] = x0; verts[11] = y0;

        _gl.UseProgram(_solidProgram);
        SetScreenSizeUniform(_solidProgram);
        SetUniform4(_solidProgram, "uColor", color.r, color.g, color.b, color.a);

        _gl.BindVertexArray(_solidVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _solidVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.DynamicDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    /// <summary>Panel origin/size in the same framebuffer-pixel space every
    /// other draw call in this renderer uses (matches SetScreenSizeUniform).
    /// Public so TerminalWindow's mouse hit-testing can compute the identical
    /// layout DrawOverlayPanel just drew — both read from this renderer's own
    /// _screenWidth/_screenHeight, so they can't drift apart from each other
    /// even if that space doesn't perfectly match window.Size under DPI
    /// scaling (a pre-existing characteristic of this renderer generally, not
    /// something new here).</summary>
    public (float originX, float originY, float cellW, float cellH) ComputeOverlayLayout(int cols, int rows, float insetFraction = 0.1f)
    {
        int panelW = (int)Math.Round(_screenWidth * (1 - insetFraction));
        int panelH = (int)Math.Round(_screenHeight * (1 - insetFraction));
        float originX = (_screenWidth - panelW) / 2f;
        float originY = (_screenHeight - panelH) / 2f;
        float cellW = cols > 0 ? (float)panelW / cols : 0;
        float cellH = rows > 0 ? (float)panelH / rows : 0;
        return (originX, originY, cellW, cellH);
    }

    /// <summary>Draws the settings overlay panel flat, straight to the default
    /// framebuffer, AFTER RenderFrame's full pipeline has already run —
    /// deliberately bypasses glow_fbo/blur/composite entirely, so it never
    /// goes through the curvature warp (unlike the scrollbar, which rides the
    /// normal text pipeline on purpose). Call after RenderFrame(), not instead
    /// of it. Sized as a percentage inset of the window (e.g. 0.1 = panel is
    /// 90% of window width/height, centered) — NOT tied to the terminal's own
    /// font size, via the dedicated small _overlayAtlas.</summary>
    public void DrawOverlayPanel(
        string[] lines, List<(int row, int col, float r, float g, float b, float a)> swatches,
        (float r, float g, float b) textColor, float insetFraction = 0.1f,
        List<(int row, int colStart, int colEnd, float r, float g, float b, float a)>? highlightBars = null)
    {
        if (lines.Length == 0) return;

        var (originX, originY, cellW, cellH) = ComputeOverlayLayout(lines[0].Length, lines.Length, insetFraction);

        Framebuffer.BindDefault(_gl, _screenWidth, _screenHeight);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        float panelWidthPx = lines[0].Length * cellW;
        float panelHeightPx = lines.Length * cellH;
        DrawSolidRectPx(originX, originY, originX + panelWidthPx, originY + panelHeightPx, (0.05f, 0.05f, 0.05f, 0.92f));

        // Full-width-of-span background bars (e.g. the font browser's selected
        // row) — drawn before the small swatches/glyphs so both land on top.
        if (highlightBars is not null)
        {
            foreach (var (row, colStart, colEnd, r, g, b, a) in highlightBars)
            {
                float x0 = originX + colStart * cellW;
                float x1 = originX + colEnd * cellW;
                float y0 = originY + row * cellH;
                DrawSolidRectPx(x0, y0, x1, y0 + cellH, (r, g, b, a));
            }
        }

        foreach (var (row, col, r, g, b, a) in swatches)
        {
            float x0 = originX + col * cellW;
            float y0 = originY + row * cellH;
            DrawSolidRectPx(x0, y0 + cellH * 0.15f, x0 + cellW * 1.5f, y0 + cellH * 0.85f, (r, g, b, a));
        }

        DrawGlyphsAt(_overlayAtlas, lines, textColor, null, originX, originY, cellW, cellH);
    }

    private void DrawSelectionSpans(IReadOnlyList<(int row, int startCol, int endCol)> spans, (float r, float g, float b, float a) color)
    {
        Span<float> verts = stackalloc float[12];

        foreach (var (row, startCol, endCol) in spans)
        {
            if (endCol <= startCol) continue;
            float x0 = _paddingLeft + startCol * CellWidth;
            float y0 = _paddingTop + row * CellHeight;
            float x1 = _paddingLeft + endCol * CellWidth;
            float y1 = y0 + CellHeight;

            _gl.UseProgram(_solidProgram);
            SetScreenSizeUniform(_solidProgram);
            SetUniform4(_solidProgram, "uColor", color.r, color.g, color.b, color.a);

            verts[0] = x0; verts[1] = y0; verts[2] = x1; verts[3] = y0; verts[4] = x1; verts[5] = y1;
            verts[6] = x1; verts[7] = y1; verts[8] = x0; verts[9] = y1; verts[10] = x0; verts[11] = y0;

            _gl.BindVertexArray(_solidVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _solidVbo);
            _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }
    }

    /// <param name="cursorGlyphPos">If set, an extra glyph quad (using the
    /// character in Config.Cursor.Shape, parsed by TerminalWindow — see
    /// ParseCursorChar) is appended to the same batch at this (col,row) —
    /// matches Python's cursor rendering literally through the font
    /// pipeline, not a solid quad, so it participates in glow the same way
    /// text does.</param>
    private void DrawGlyphs(string[] lines, (float r, float g, float b) tint, (char ch, int col, int row)? cursorGlyphPos) =>
        DrawGlyphsAt(_atlas, lines, tint, cursorGlyphPos, _paddingLeft, _paddingTop, CellWidth, CellHeight);

    /// <param name="cellW">On-screen quad size — deliberately independent of
    /// atlas.CellWidth/CellHeight (which only govern the UV rectangle sampled
    /// from the atlas texture). Letting these differ is what allows the
    /// settings overlay to stretch its small fixed-size atlas glyphs to fit a
    /// window-relative panel size.</param>
    private void DrawGlyphsAt(
        FontAtlas atlas, string[] lines, (float r, float g, float b) tint, (char ch, int col, int row)? cursorGlyphPos,
        float originX, float originY, float cellW, float cellH)
    {
        _vertexScratch.Clear();
        _indexScratch.Clear();
        uint quadIndex = 0;

        void EmitQuad(char c, int col, int row)
        {
            var uv = atlas.GetUv(c);
            if (uv is null) return; // no atlas slot for this char — skip rather than crash

            float x0 = originX + col * cellW;
            float y0 = originY + row * cellH;
            float x1 = x0 + cellW;
            float y1 = y0 + cellH;
            var (u, v, w, h) = uv.Value;

            _vertexScratch.AddRange(new[]
            {
                x0, y0, u, v,
                x1, y0, u + w, v,
                x1, y1, u + w, v + h,
                x0, y1, u, v + h,
            });

            uint baseIdx = quadIndex * 4;
            _indexScratch.AddRange(new[]
            {
                baseIdx, baseIdx + 1, baseIdx + 2,
                baseIdx + 2, baseIdx + 3, baseIdx,
            });
            quadIndex++;
        }

        for (int row = 0; row < lines.Length; row++)
        {
            string line = lines[row];
            for (int col = 0; col < line.Length; col++)
            {
                char c = line[col];
                if (c == ' ') continue; // nothing to draw, keep the batch small
                EmitQuad(c, col, row);
            }
        }

        if (cursorGlyphPos is { } pos)
            EmitQuad(pos.ch, pos.col, pos.row); // atlas.GetUv returning null (unsupported codepoint) already degrades to "draw nothing" — a blank/hidden cursor, not a crash

        if (quadIndex == 0) return;

        _gl.UseProgram(_textProgram);
        SetScreenSizeUniform(_textProgram);
        SetUniform3(_textProgram, "uTextColor", tint.r, tint.g, tint.b);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, atlas.TextureHandle);
        SetUniform1(_textProgram, "uAtlas", 0);

        _gl.BindVertexArray(_textVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(_vertexScratch), BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _textEbo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, CollectionsMarshal.AsSpan(_indexScratch), BufferUsageARB.DynamicDraw);

        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)_indexScratch.Count, DrawElementsType.UnsignedInt, null);
        }
    }

    private void SetScreenSizeUniform(uint program)
    {
        int loc = _gl.GetUniformLocation(program, "uScreenSize");
        _gl.Uniform2(loc, (float)_screenWidth, (float)_screenHeight);
    }

    private void SetUniform1(uint program, string name, int value) =>
        _gl.Uniform1(_gl.GetUniformLocation(program, name), value);

    private void SetUniform1(uint program, string name, float value) =>
        _gl.Uniform1(_gl.GetUniformLocation(program, name), value);

    private void SetUniform2(uint program, string name, float a, float b) =>
        _gl.Uniform2(_gl.GetUniformLocation(program, name), a, b);

    private void SetUniform3(uint program, string name, float a, float b, float c) =>
        _gl.Uniform3(_gl.GetUniformLocation(program, name), a, b, c);

    private void SetUniform4(uint program, string name, float a, float b, float c, float d) =>
        _gl.Uniform4(_gl.GetUniformLocation(program, name), a, b, c, d);

    private void SetUniformBool(uint program, string name, bool value) =>
        _gl.Uniform1(_gl.GetUniformLocation(program, name), value ? 1 : 0);

    private unsafe (uint vao, uint vbo, uint ebo) CreateTextBuffers()
    {
        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);

        const uint stride = 4 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));

        _gl.BindVertexArray(0);
        return (vao, vbo, ebo);
    }

    private unsafe (uint vao, uint vbo) CreateSolidBuffers()
    {
        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        _gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private const string TextVertexSrc = """
        #version 330 core
        layout (location = 0) in vec2 aPos;
        layout (location = 1) in vec2 aTexCoord;
        uniform vec2 uScreenSize;
        out vec2 vTexCoord;
        void main() {
            vec2 ndc = vec2((aPos.x / uScreenSize.x) * 2.0 - 1.0, 1.0 - (aPos.y / uScreenSize.y) * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string TextFragmentSrc = """
        #version 330 core
        in vec2 vTexCoord;
        out vec4 fragColor;
        uniform sampler2D uAtlas;
        uniform vec3 uTextColor;
        void main() {
            float coverage = texture(uAtlas, vTexCoord).a;
            fragColor = vec4(uTextColor, coverage);
        }
        """;

    private const string SolidVertexSrc = """
        #version 330 core
        layout (location = 0) in vec2 aPos;
        uniform vec2 uScreenSize;
        void main() {
            vec2 ndc = vec2((aPos.x / uScreenSize.x) * 2.0 - 1.0, 1.0 - (aPos.y / uScreenSize.y) * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
        }
        """;

    private const string SolidFragmentSrc = """
        #version 330 core
        out vec4 fragColor;
        uniform vec4 uColor;
        void main() { fragColor = uColor; }
        """;

    // Direct port of shaders/blur.vert.
    private const string BlurVertexSrc = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 TexCoords;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            TexCoords = aUV;
        }
        """;

    // Direct port of shaders/blur.frag — 9-tap separable Gaussian, unchanged
    // weights/offsets from the Python version.
    private const string BlurFragmentSrc = """
        #version 330 core
        in vec2 TexCoords;
        out vec4 FragColor;

        uniform sampler2D screenTexture;
        uniform vec2 texelSize;
        uniform bool horizontal;
        uniform float intensity;

        void main()
        {
            vec2 tex_offset = texelSize * intensity;
            vec3 result = texture(screenTexture, TexCoords).rgb * 0.227027;

            if(horizontal)
            {
                result += texture(screenTexture, TexCoords + vec2(tex_offset.x, 0.0)).rgb * 0.1945946;
                result += texture(screenTexture, TexCoords - vec2(tex_offset.x, 0.0)).rgb * 0.1945946;
                result += texture(screenTexture, TexCoords + vec2(2.0 * tex_offset.x, 0.0)).rgb * 0.1216216;
                result += texture(screenTexture, TexCoords - vec2(2.0 * tex_offset.x, 0.0)).rgb * 0.1216216;
                result += texture(screenTexture, TexCoords + vec2(3.0 * tex_offset.x, 0.0)).rgb * 0.054054;
                result += texture(screenTexture, TexCoords - vec2(3.0 * tex_offset.x, 0.0)).rgb * 0.054054;
                result += texture(screenTexture, TexCoords + vec2(4.0 * tex_offset.x, 0.0)).rgb * 0.016216;
                result += texture(screenTexture, TexCoords - vec2(4.0 * tex_offset.x, 0.0)).rgb * 0.016216;
            }
            else
            {
                result += texture(screenTexture, TexCoords + vec2(0.0, tex_offset.y)).rgb * 0.1945946;
                result += texture(screenTexture, TexCoords - vec2(0.0, tex_offset.y)).rgb * 0.1945946;
                result += texture(screenTexture, TexCoords + vec2(0.0, 2.0 * tex_offset.y)).rgb * 0.1216216;
                result += texture(screenTexture, TexCoords - vec2(0.0, 2.0 * tex_offset.y)).rgb * 0.1216216;
                result += texture(screenTexture, TexCoords + vec2(0.0, 3.0 * tex_offset.y)).rgb * 0.054054;
                result += texture(screenTexture, TexCoords - vec2(0.0, 3.0 * tex_offset.y)).rgb * 0.054054;
                result += texture(screenTexture, TexCoords + vec2(0.0, 4.0 * tex_offset.y)).rgb * 0.016216;
                result += texture(screenTexture, TexCoords - vec2(0.0, 4.0 * tex_offset.y)).rgb * 0.016216;
            }

            FragColor = vec4(result, texture(screenTexture, TexCoords).a);
        }
        """;

    // Direct port of shaders/burnin.vert — same shape as blur.vert.
    private const string BurninVertexSrc = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 TexCoords;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            TexCoords = aUV;
        }
        """;

    // Direct port of shaders/burnin.frag.
    private const string BurninFragmentSrc = """
        #version 330 core
        in vec2 TexCoords;
        out vec4 FragColor;

        uniform sampler2D currentFrame;
        uniform sampler2D previousBurnin;
        uniform float build;
        uniform float decay;

        void main()
        {
            vec4 current = texture(currentFrame, TexCoords);
            float currentLuma = current.a;

            float prevBurn = texture(previousBurnin, TexCoords).r;

            float burnIntensity = prevBurn;

            if (currentLuma > 0.1) {
                burnIntensity = mix(prevBurn, 1.0, build);
            } else {
                burnIntensity = prevBurn * decay;
            }

            FragColor = vec4(burnIntensity, 0.0, 0.0, 1.0);
        }
        """;

    // Direct port of shaders/composite.vert.
    private const string CompositeVertexSrc = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 TexCoords;
        out vec2 FragPos;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            TexCoords = aUV;
            FragPos = aPos;
        }
        """;

    // Direct port of shaders/composite.frag — uniform names kept identical to
    // the Python source. vignette/curvature/scanlines/burn-in branches compile
    // in but are always called with their enable* flags false in this stage.
    private const string CompositeFragmentSrc = """
        #version 330 core

        in vec2 TexCoords;
        in vec2 FragPos;
        out vec4 FragColor;

        uniform sampler2D textTexture;
        uniform sampler2D burninTexture;

        uniform vec4  backgroundColor;
        uniform float glowIntensity;
        uniform float glowSpread;
        uniform float cornerRadius;
        uniform vec2  resolution;
        uniform vec2  texelSize;
        uniform vec3  textTint;
        uniform vec3  glowTint;

        uniform float vignetteStrength;
        uniform float curvatureAmount;
        uniform float scanlineIntensity;
        uniform float scanlineFrequency;

        uniform float burnInOpacity;
        uniform vec4  burnInTint;

        uniform bool enableVignette;
        uniform bool enableBurnIn;
        uniform bool enableCurvature;
        uniform bool enableScanlines;

        vec2 applyCurvature(vec2 uv) {
            if (!enableCurvature || curvatureAmount == 0.0) return uv;
            uv = uv * 2.0 - 1.0;
            float r2 = dot(uv, uv);
            float distortion = 1.0 + curvatureAmount * r2;
            uv *= distortion;
            return uv * 0.5 + 0.5;
        }

        float calculateVignette(vec2 uv) {
            if (!enableVignette || vignetteStrength == 0.0) return 1.0;
            vec2 center = uv - 0.5;
            float dist = length(center);
            return 1.0 - smoothstep(0.3, 0.8, dist * vignetteStrength);
        }

        float calculateScanlines(vec2 uv) {
            if (!enableScanlines || scanlineIntensity == 0.0) return 1.0;
            float scanline = sin(uv.y * resolution.y * scanlineFrequency) * 0.5 + 0.5;
            return 1.0 - (scanline * scanlineIntensity);
        }

        vec4 sampleTextWithGlow(sampler2D tex, vec2 uv) {
            vec4 sharpText = texture(tex, uv);
            sharpText.rgb *= textTint;

            if (glowIntensity <= 0.0 || glowSpread <= 0.0) {
                return sharpText;
            }

            vec4 glow = vec4(0.0);
            float totalWeight = 0.0;

            int samples = 16;
            float maxRadius = glowSpread * 3.0;

            for (int ring = 1; ring <= 3; ring++) {
                float radius = maxRadius * float(ring) / 3.0;
                float ringWeight = exp(-float(ring) * 2.5);

                for (int i = 0; i < samples; i++) {
                    float angle = 2.0 * 3.14159 * float(i) / float(samples);
                    vec2 offset = vec2(cos(angle), sin(angle)) * radius * texelSize;
                    vec4 samp = texture(tex, uv + offset);
                    glow += samp * ringWeight;
                    totalWeight += ringWeight;
                }
            }

            if (totalWeight > 0.0) {
                glow /= totalWeight;
            }

            glow.rgb *= glowTint * glowIntensity;

            vec4 result = sharpText;
            result.rgb = mix(glow.rgb, sharpText.rgb, sharpText.a);
            result.a = max(sharpText.a, glow.a * 0.8);

            return result;
        }

        float roundedBoxSDF(vec2 centerPos, vec2 size, float radius) {
            return length(max(abs(centerPos) - size, 0.0)) - radius;
        }

        void main()
        {
            vec2 curvedUV = applyCurvature(TexCoords);

            if (curvedUV.x < 0.0 || curvedUV.x > 1.0 || curvedUV.y < 0.0 || curvedUV.y > 1.0) {
                FragColor = vec4(0.0, 0.0, 0.0, 0.0);
                return;
            }

            vec4 textColor = sampleTextWithGlow(textTexture, curvedUV);

            float scanlineFactor = calculateScanlines(curvedUV);
            textColor.rgb *= scanlineFactor;
            float vignette = calculateVignette(curvedUV);
            textColor.rgb *= vignette;

            float cornerAlpha = 1.0;
            if (cornerRadius > 0.0) {
                vec2 pixelPos = curvedUV * resolution;
                vec2 centerPos = pixelPos - resolution * 0.5;
                vec2 size = resolution * 0.5 - cornerRadius;
                float dist = roundedBoxSDF(centerPos, size, cornerRadius);
                cornerAlpha = 1.0 - smoothstep(-1.0, 1.0, dist);
            }

            vec3 baseRGB;
            float outA;
            if (backgroundColor.a < 0.01) {
                baseRGB = textColor.rgb;
                outA   = textColor.a * cornerAlpha;
            } else {
                baseRGB = mix(backgroundColor.rgb, textColor.rgb, textColor.a);
                outA    = backgroundColor.a * cornerAlpha;
            }

            vec3 burnInLayer = baseRGB;
            if (enableBurnIn && burnInOpacity > 0.0) {
                float burn = texture(burninTexture, curvedUV).r;
                burnInLayer += burnInTint.rgb * burn * burnInOpacity * burnInTint.a;
            }

            vec3 finalRGB;
            if (backgroundColor.a < 0.01) {
                finalRGB = mix(burnInLayer, textColor.rgb, textColor.a);
                outA = max(textColor.a, burnInOpacity * texture(burninTexture, curvedUV).r) * cornerAlpha;
            } else {
                vec3 bgWithBurnIn = mix(backgroundColor.rgb, burnInLayer, burnInOpacity);
                finalRGB = mix(bgWithBurnIn, textColor.rgb, textColor.a);
                outA = backgroundColor.a * cornerAlpha;
            }

            FragColor = vec4(finalRGB, outA * cornerAlpha);
        }
        """;

    public void Dispose()
    {
        _atlas.Dispose();
        _overlayAtlas.Dispose();
        _gl.DeleteProgram(_textProgram);
        _gl.DeleteProgram(_solidProgram);
        _gl.DeleteProgram(_blurProgram);
        _gl.DeleteProgram(_compositeProgram);
        _gl.DeleteProgram(_burninProgram);
        _gl.DeleteVertexArray(_textVao);
        _gl.DeleteBuffer(_textVbo);
        _gl.DeleteBuffer(_textEbo);
        _gl.DeleteVertexArray(_solidVao);
        _gl.DeleteBuffer(_solidVbo);
        _glowFbo?.Dispose();
        _blurFbo1?.Dispose();
        _blurFbo2?.Dispose();
        _burninFbo?.Dispose();
        _burninFboPrev?.Dispose();
        _fullscreenQuad.Dispose();
    }
}
