using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ChronoTerm.Config;

public sealed class ConfigManager
{
    private readonly string _appName;
    private string? _configDir;
    private string? _configPath;

    public ChronoTermConfig Config { get; private set; }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties() // forward-compat: unknown keys in an old/edited config.yaml don't crash startup
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public ConfigManager(string appName = "ChronoTerm")
    {
        _appName = appName;
        Config = Load();
    }

    public string GetConfigDir()
    {
        if (_configDir is not null) return _configDir;

        // .NET's ApplicationData special folder maps to %APPDATA% on Windows and
        // ~/.config (XDG_CONFIG_HOME) on Linux — matches ConfigManager.py's own
        // per-platform branching without needing to hand-write it. (On macOS this
        // also resolves to ~/.config rather than Python's ~/Library/Application
        // Support — a known difference, low priority while macOS isn't a target.)
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _configDir = Path.Combine(baseDir, _appName);
        Directory.CreateDirectory(_configDir);
        return _configDir;
    }

    public string GetConfigPath()
    {
        _configPath ??= Path.Combine(GetConfigDir(), "config.yaml");
        return _configPath;
    }

    /// <summary>Named presets live in a "configs" subfolder next to the
    /// active config.yaml, per the requested layout — GetConfigDir()'s parent
    /// is ~/.config/ChronoTerm, so this is ~/.config/ChronoTerm/configs.</summary>
    public string GetConfigsDir()
    {
        string dir = Path.Combine(GetConfigDir(), "configs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Names (without the .yaml extension) of saved presets, for the
    /// settings panel's Load browser.</summary>
    public List<string> ListProfiles()
    {
        try
        {
            return Directory.EnumerateFiles(GetConfigsDir(), "*.yaml")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] Error listing saved configs: {ex.Message}");
            return new List<string>();
        }
    }

    private static string SanitizeProfileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>Saves the CURRENT in-memory Config as a named preset. Doesn't
    /// touch the active config.yaml — this is "save a copy", not "switch
    /// active config" (that's what LoadProfile does, deliberately later).</summary>
    public bool SaveProfileAs(string name)
    {
        string clean = SanitizeProfileName(name);
        if (string.IsNullOrWhiteSpace(clean)) return false;
        SaveInternal(Config, Path.Combine(GetConfigsDir(), clean + ".yaml"));
        return true;
    }

    /// <summary>Loads a named preset and makes it the active config — both in
    /// memory right now AND persisted to config.yaml so it's still active on
    /// the next launch too. Returns false (Config left untouched) if the
    /// preset is missing or corrupt, same tolerant parsing as the active
    /// config gets on startup.</summary>
    public bool LoadProfile(string name)
    {
        string path = Path.Combine(GetConfigsDir(), SanitizeProfileName(name) + ".yaml");
        var loaded = TryReadConfigFile(path, out _, out string? error);
        if (loaded is null)
        {
            Console.WriteLine($"[Config] Could not load preset '{name}': {error}");
            return false;
        }

        // Deliberately NOT `Config = loaded` — SettingsOverlay's color-bar
        // sliders were built against the specific array objects living
        // inside THIS Config instance (see SettingsOverlay.AddColorBars),
        // captured once when the panel was constructed. Swapping the whole
        // object out from under it would leave those sliders pointing at
        // orphaned arrays that never repaint or take input again. Copying
        // values into the existing object graph keeps every prior reference
        // — this one included — pointed at something live.
        CopyInto(loaded, Config);
        SaveInternal(Config, GetConfigPath());
        Console.WriteLine($"[Config] Loaded preset '{name}' and made it active.");
        return true;
    }

    private static void CopyInto(ChronoTermConfig source, ChronoTermConfig target)
    {
        target.Window.Width = source.Window.Width;
        target.Window.Height = source.Window.Height;
        target.Window.HideTitlebar = source.Window.HideTitlebar;
        target.Window.CornerRadius = source.Window.CornerRadius;
        target.Window.Padding.Top = source.Window.Padding.Top;
        target.Window.Padding.Bottom = source.Window.Padding.Bottom;
        target.Window.Padding.Left = source.Window.Padding.Left;
        target.Window.Padding.Right = source.Window.Padding.Right;

        target.Terminal.WriteSpeed = source.Terminal.WriteSpeed;
        target.Terminal.Rows = source.Terminal.Rows;
        target.Terminal.Cols = source.Terminal.Cols;

        // Path/Size/CharSpacing/LineSpacing round-trip fine here, but — same
        // caveat as the settings panel's own font controls — the glyph atlas
        // is only built once at startup, so a font change from a loaded
        // preset needs a restart to actually show up on screen.
        target.Font.Path = source.Font.Path;
        target.Font.Size = source.Font.Size;
        target.Font.CharSpacing = source.Font.CharSpacing;
        target.Font.LineSpacing = source.Font.LineSpacing;
        target.Font.LastDirectory = source.Font.LastDirectory;

        CopyColor(source.Colors.Background, target.Colors.Background);
        CopyColor(source.Colors.Text, target.Colors.Text);
        CopyColor(source.Colors.Glow, target.Colors.Glow);
        CopyColor(source.Colors.Selection, target.Colors.Selection);

        target.Cursor.Shape = source.Cursor.Shape;
        target.Cursor.Blink = source.Cursor.Blink;

        target.Scrollbar.Mode = source.Scrollbar.Mode;

        target.Effects.Glow.Intensity = source.Effects.Glow.Intensity;
        target.Effects.Glow.Spread = source.Effects.Glow.Spread;
        target.Effects.Vignette.Enabled = source.Effects.Vignette.Enabled;
        target.Effects.Vignette.Strength = source.Effects.Vignette.Strength;
        target.Effects.Scanlines.Enabled = source.Effects.Scanlines.Enabled;
        target.Effects.Scanlines.Intensity = source.Effects.Scanlines.Intensity;
        target.Effects.Scanlines.Frequency = source.Effects.Scanlines.Frequency;
        target.Effects.Curvature.Enabled = source.Effects.Curvature.Enabled;
        target.Effects.Curvature.Amount = source.Effects.Curvature.Amount;
        target.Effects.BurnIn.Enabled = source.Effects.BurnIn.Enabled;
        target.Effects.BurnIn.Intensity = source.Effects.BurnIn.Intensity;
        target.Effects.BurnIn.Build = source.Effects.BurnIn.Build;
        target.Effects.BurnIn.FadeTime = source.Effects.BurnIn.FadeTime;
        CopyColor(source.Effects.BurnIn.Tint, target.Effects.BurnIn.Tint);
    }

    /// <summary>Copies contents into the EXISTING target array (never
    /// replaces the reference) — see LoadProfile's comment on why identity
    /// matters here.</summary>
    private static void CopyColor(double[] source, double[] target) =>
        Array.Copy(source, target, Math.Min(source.Length, target.Length));

    private ChronoTermConfig CreateDefault()
    {
        var config = new ChronoTermConfig();

        var (screenWidth, screenHeight) = ScreenInfo.GetPrimaryMonitorSize();
        config.Window.Width = screenWidth / 2;
        config.Window.Height = screenHeight / 2;
        config.Font.Path = ResolveFontPath();

        return config;
    }

    /// <summary>Shared by CreateDefault (first run) and Sanitize (healing an
    /// existing config with a missing/invalid font path) so both paths log
    /// and fall back identically instead of duplicating the warning text.</summary>
    private static string ResolveFontPath()
    {
        string? font = FontFinder.FindSuitableFont();
        if (font is not null) return font;

        Console.WriteLine("[Config] Warning: no suitable font found. Terminal may not render correctly.");
        Console.WriteLine("[Config] Please install a monospace font or configure one manually.");
        return "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf"; // matches Python's fallback
    }

    /// <summary>Parses+sanitizes a config file if it exists and is readable.
    /// Shared by Load() (the active config.yaml) and LoadProfile() (a named
    /// preset) — returns null with a diagnostic in errorMessage for a
    /// missing/unreadable file, leaving what to do about that up to the
    /// caller, since the two react differently (Load falls back to a working
    /// default silently; LoadProfile reports the failure and leaves the
    /// current config untouched).</summary>
    private ChronoTermConfig? TryReadConfigFile(string path, out bool wasHealed, out string? errorMessage)
    {
        wasHealed = false;
        errorMessage = null;
        if (!File.Exists(path)) { errorMessage = "file not found"; return null; }

        ChronoTermConfig config;
        try
        {
            string yaml = File.ReadAllText(path);
            config = Deserializer.Deserialize<ChronoTermConfig>(yaml) ?? new ChronoTermConfig();
        }
        catch (Exception ex)
        {
            // Deliberately broad, not just YamlException: a genuinely
            // corrupted file (truncated write, binary garbage, wrong
            // encoding, a permissions error mid-read) can surface as all
            // sorts of exception types depending on exactly how it's broken.
            errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }

        wasHealed = Sanitize(config);
        return config;
    }

    private ChronoTermConfig Load()
    {
        string path = GetConfigPath();
        var config = TryReadConfigFile(path, out bool healed, out string? error);

        if (config is null)
        {
            if (error == "file not found")
            {
                Console.WriteLine($"[Config] Config file not found. Creating a new one at: {path}");
                var fresh = CreateDefault();
                SaveInternal(fresh, path);
                return fresh;
            }

            // Deliberately not auto-saved: leaves the corrupted file on disk
            // for manual inspection/recovery instead of silently overwriting
            // whatever the user had, using a working default only in memory.
            Console.WriteLine($"[Config] Error reading config file ({error}). Using default settings.");
            return CreateDefault();
        }

        Console.WriteLine($"[Config] Loaded from: {path}");
        if (healed)
        {
            Console.WriteLine("[Config] Some values were missing or invalid and were reset to defaults.");
            SaveInternal(config, path); // heal the file on disk so this doesn't repeat every launch
        }
        return config;
    }

    /// <summary>Fixes up individual fields that parsed as valid YAML but are
    /// semantically unusable — null/empty/out-of-range values that would
    /// otherwise crash something much later (font loading, texture creation,
    /// color-array indexing) instead of failing loudly here. Returns true if
    /// anything was changed, so Load() knows whether to re-save.
    ///
    /// Deliberately per-field rather than "if anything's wrong, discard the
    /// whole config": one bad value (e.g. a hand-edited "font: path:") should
    /// only cost the user that one setting, not every customization in the
    /// file.</summary>
    private bool Sanitize(ChronoTermConfig config)
    {
        bool changed = false;

        // A bare "font:" (or "colors:", "window:", etc. — any section header
        // with no children) deserializes the whole section object to null,
        // not just a missing leaf value — everything below would
        // NullReferenceException on the next line without this. `??=` also
        // exercises each section's own constructor defaults for free.
        if (config.Window is null) { config.Window = new(); changed = true; }
        if (config.Terminal is null) { config.Terminal = new(); changed = true; }
        if (config.Font is null) { config.Font = new(); changed = true; }
        if (config.Colors is null) { config.Colors = new(); changed = true; }
        if (config.Cursor is null) { config.Cursor = new(); changed = true; }
        if (config.Scrollbar is null) { config.Scrollbar = new(); changed = true; }
        if (config.Effects is null) { config.Effects = new(); changed = true; }

        if (string.IsNullOrWhiteSpace(config.Font.Path) || !File.Exists(config.Font.Path))
        {
            if (!string.IsNullOrWhiteSpace(config.Font.Path))
                Console.WriteLine($"[Config] Font path '{config.Font.Path}' doesn't exist — falling back to a detected font.");
            config.Font.Path = ResolveFontPath();
            changed = true;
        }

        // 6..500 is a generous sane range — anything outside it is almost
        // certainly a corrupted value (0, negative, or a stray extra digit)
        // rather than an intentional choice, and would otherwise crash
        // SixLabors.Fonts' CreateFont or produce a multi-gigabyte atlas texture.
        if (config.Font.Size is <= 0 or > 500) { config.Font.Size = 32; changed = true; }

        if (!IsFiniteAndPositive(config.Font.CharSpacing)) { config.Font.CharSpacing = 1.0; changed = true; }
        if (!IsFiniteAndPositive(config.Font.LineSpacing)) { config.Font.LineSpacing = 1.0; changed = true; }

        var (bg, bgChanged) = SanitizeColor(config.Colors.Background, ColorsConfig.DefaultBackground);
        config.Colors.Background = bg; changed |= bgChanged;
        var (text, textChanged) = SanitizeColor(config.Colors.Text, ColorsConfig.DefaultText);
        config.Colors.Text = text; changed |= textChanged;
        var (glow, glowChanged) = SanitizeColor(config.Colors.Glow, ColorsConfig.DefaultGlow);
        config.Colors.Glow = glow; changed |= glowChanged;
        var (selection, selectionChanged) = SanitizeColor(config.Colors.Selection, ColorsConfig.DefaultSelection);
        config.Colors.Selection = selection; changed |= selectionChanged;

        if (config.Window.Width <= 0 || config.Window.Height <= 0)
        {
            var (screenWidth, screenHeight) = ScreenInfo.GetPrimaryMonitorSize();
            config.Window.Width = config.Window.Width > 0 ? config.Window.Width : screenWidth / 2;
            config.Window.Height = config.Window.Height > 0 ? config.Window.Height : screenHeight / 2;
            changed = true;
        }

        if (config.Terminal.Rows <= 0) { config.Terminal.Rows = 80; changed = true; }
        if (config.Terminal.Cols <= 0) { config.Terminal.Cols = 132; changed = true; }

        // Now that Cursor.Shape actually drives what glyph gets drawn (see
        // TerminalWindow.ParseCursorChar), an unparseable value is worth
        // healing here too, not just falling back silently at render time.
        try { Convert.ToInt32(config.Cursor.Shape, 16); }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            config.Cursor.Shape = "0x2588";
            changed = true;
        }

        return changed;
    }

    private static bool IsFiniteAndPositive(double v) => double.IsFinite(v) && v > 0;

    /// <summary>A color that's null, empty, or missing the alpha channel
    /// (e.g. a config saved before alpha support was added, or hand-edited
    /// down to 3 values) would index-out-of-range the moment anything reads
    /// color[3] — reset the whole channel to its default rather than trying
    /// to guess a missing component. Returns the value to use plus whether it
    /// changed (properties can't be passed by `ref`, so this returns rather
    /// than mutates).</summary>
    private static (double[] value, bool changed) SanitizeColor(double[]? color, double[] defaults) =>
        color is { Length: 4 } && Array.TrueForAll(color, double.IsFinite)
            ? (color, false)
            : ((double[])defaults.Clone(), true);

    /// <summary>Persists the current in-memory Config — call after any runtime
    /// change (e.g. main.py's resize handler keeping window.width/height current
    /// before save; do the same here before calling Save()).</summary>
    public void Save() => SaveInternal(Config, GetConfigPath());

    private void SaveInternal(ChronoTermConfig config, string path)
    {
        try
        {
            File.WriteAllText(path, Serializer.Serialize(config));
            Console.WriteLine($"[Config] Saved to: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] Error saving config file: {ex.Message}");
        }
    }
}
