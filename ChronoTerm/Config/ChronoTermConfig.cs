namespace ChronoTerm.Config;

/// <summary>
/// Mirrors ConfigManager.py's _get_default_config() structure and default
/// values field-for-field. Property defaults ARE the "deep merge with
/// defaults" mechanism: YamlDotNet deserializes onto a freshly-constructed
/// instance, so any key missing from the user's config.yaml simply keeps
/// the default set here — no separate merge step needed like Python's dict-
/// based _deep_update.
///
/// effects.* fields are read fresh every frame by OnRender and passed into
/// TerminalGlRenderer.RenderFrame (glow/burn-in) and CrtEffects (vignette/
/// scanlines/curvature) — all live, no restart needed for any of them.
/// </summary>
public sealed class ChronoTermConfig
{
    public WindowConfig Window { get; set; } = new();
    public TerminalConfig Terminal { get; set; } = new();
    public FontConfig Font { get; set; } = new();
    public ColorsConfig Colors { get; set; } = new();
    public CursorConfig Cursor { get; set; } = new();
    public ScrollbarConfig Scrollbar { get; set; } = new();
    public EffectsConfig Effects { get; set; } = new();
}

public sealed class WindowConfig
{
    // Python defaults these to half the primary monitor's resolution,
    // computed at first-run (see ConfigManager.CreateDefault). 1000x700
    // here is just the pre-monitor-query placeholder.
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 700;
    public bool HideTitlebar { get; set; } = false;
    public double CornerRadius { get; set; } = 0.0; // live — read fresh each frame by the CRT composite shader (Rendering/TerminalGlRenderer.cs)
    public PaddingConfig Padding { get; set; } = new();
}

public sealed class PaddingConfig
{
    public int Top { get; set; } = 20;
    public int Bottom { get; set; } = 20;
    public int Left { get; set; } = 20;
    public int Right { get; set; } = 20;
}

public sealed class TerminalConfig
{
    public int WriteSpeed { get; set; } = 8192; // unused so far — no write-throttling ported yet

    // Round-tripped for compatibility with the Python version's config
    // schema, but NOT currently consulted for sizing: TerminalWindow computes
    // the actual grid size from window pixel dimensions ÷ font cell size at
    // startup (see initialCols/initialRows in TerminalWindow's constructor),
    // so a preset's rows/cols here have no effect in this port yet — only
    // Window.Width/Height (and the font) end up determining the grid.
    public int Rows { get; set; } = 80;
    public int Cols { get; set; } = 132;
}

public sealed class FontConfig
{
    public string Path { get; set; } = "";
    public int Size { get; set; } = 32;
    public double CharSpacing { get; set; } = 1.0;
    public double LineSpacing { get; set; } = 1.0;
    public string LastDirectory { get; set; } = "";
}

public sealed class ColorsConfig
{
    // Named so ConfigManager's sanitizer can restore exactly these values —
    // single source of truth instead of duplicating the numbers there.
    public static readonly double[] DefaultBackground = { 0.0, 0.0, 0.0, 0.8 };
    public static readonly double[] DefaultText = { 1.0, 0.67, 0.0, 1.0 };
    public static readonly double[] DefaultGlow = { 1.0, 0.5, 0.0, 1.0 };
    public static readonly double[] DefaultSelection = { 0.6, 0.3, 0.0, 0.3 };

    public double[] Background { get; set; } = (double[])DefaultBackground.Clone();
    public double[] Text { get; set; } = (double[])DefaultText.Clone();
    public double[] Glow { get; set; } = (double[])DefaultGlow.Clone();
    public double[] Selection { get; set; } = (double[])DefaultSelection.Clone();
}

public sealed class CursorConfig
{
    public string Shape { get; set; } = "0x2588"; // full block, matches TerminalGlRenderer's cursor quad
    public bool Blink { get; set; } = true;
}

public sealed class ScrollbarConfig
{
    // Live — read each frame by TerminalWindow's scrollbar-info build (see
    // App/TerminalWindow.cs) and passed to ScrollbarOverlay for rendering.
    public string Mode { get; set; } = "auto";
}

public sealed class EffectsConfig
{
    public GlowEffectConfig Glow { get; set; } = new();
    public VignetteEffectConfig Vignette { get; set; } = new();
    public ScanlinesEffectConfig Scanlines { get; set; } = new();
    public CurvatureEffectConfig Curvature { get; set; } = new();
    public BurnInEffectConfig BurnIn { get; set; } = new();
}

public sealed class GlowEffectConfig
{
    public double Intensity { get; set; } = 1.0;
    public double Spread { get; set; } = 1.0;
}

public sealed class VignetteEffectConfig
{
    public bool Enabled { get; set; } = false;
    public double Strength { get; set; } = 0.1;
}

public sealed class ScanlinesEffectConfig
{
    public bool Enabled { get; set; } = false;
    public double Intensity { get; set; } = 0.5;
    public double Frequency { get; set; } = 1.5;
}

public sealed class CurvatureEffectConfig
{
    public bool Enabled { get; set; } = false;
    public double Amount { get; set; } = 0.02;
}

public sealed class BurnInEffectConfig
{
    public bool Enabled { get; set; } = true;
    public double Intensity { get; set; } = 0.2;
    public double Build { get; set; } = 0.2;
    public double FadeTime { get; set; } = 1.0;
    public double[] Tint { get; set; } = { 0.5, 0.3, 0.0, 1.0 };
}
