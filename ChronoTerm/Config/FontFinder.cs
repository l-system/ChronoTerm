using System.Diagnostics;
using System.Linq;

namespace ChronoTerm.Config;

/// <summary>One discoverable font for the settings-panel browser: the file on
/// disk plus a human-readable name (fontconfig's family name when available,
/// otherwise just the filename).</summary>
public readonly record struct FontEntry(string Path, string DisplayName);

/// <summary>Port of find_suitable_font()'s Linux branch (this project's current
/// scope). Same search order: known-good paths first, then a directory walk
/// for anything with "mono" in the filename, then fontconfig's fc-match, then
/// any TTF at all as a last resort.</summary>
public static class FontFinder
{
    private static readonly string[] KnownPaths =
    {
        "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
        "/usr/share/fonts/truetype/ubuntu/UbuntuMono-R.ttf",
        "/usr/share/fonts/truetype/noto/NotoMono-Regular.ttf",
        "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
        "/usr/share/fonts/TTF/LiberationMono-Regular.ttf",
        "/usr/share/fonts/liberation/LiberationMono-Regular.ttf",
        "/usr/share/fonts/dejavu/DejaVuSansMono.ttf",
        "/usr/share/fonts/noto/NotoMono-Regular.ttf",
        "/usr/share/fonts/liberation-mono/LiberationMono-Regular.ttf",
    };

    private static readonly string[] SearchDirs =
    {
        "/usr/share/fonts",
        "/usr/local/share/fonts",
    };

    public static string? FindSuitableFont()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>(KnownPaths)
        {
            Path.Combine(home, ".fonts", "LiberationMono-Regular.ttf"),
            Path.Combine(home, ".local", "share", "fonts", "LiberationMono-Regular.ttf"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                Console.WriteLine($"[Config] Found suitable font: {path}");
                return path;
            }
        }

        // Directory walk for anything with "mono" in the name — matches the
        // Python version's os.walk pass over common font directories.
        var userDirs = new[]
        {
            Path.Combine(home, ".fonts"),
            Path.Combine(home, ".local", "share", "fonts"),
        };
        foreach (var dir in SearchDirs.Concat(userDirs))
        {
            string? found = SearchDirectory(dir, f => f.Contains("mono", StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                Console.WriteLine($"[Config] Found suitable font: {found}");
                return found;
            }
        }

        // fontconfig fallback.
        string? fcMatch = TryFcMatch();
        if (fcMatch is not null)
        {
            Console.WriteLine($"[Config] Found font via fontconfig: {fcMatch}");
            return fcMatch;
        }

        // Last resort: any TTF at all.
        foreach (var dir in SearchDirs.Append(Path.Combine(home, ".fonts")))
        {
            string? found = SearchDirectory(dir, _ => true);
            if (found is not null)
            {
                Console.WriteLine($"[Config] Using fallback font: {found}");
                return found;
            }
        }

        return null;
    }

    /// <summary>Full listing of installed fonts for the settings-panel font
    /// browser (distinct from FindSuitableFont's early-exit search for a
    /// single startup default).
    ///
    /// Font locations vary a lot across Linux distros and BSDs — Debian-family
    /// systems use /usr/share/fonts, BSD ports commonly put things under
    /// /usr/local/share/fonts, Flatpak sandboxes expose /run/host/fonts, and
    /// personal fonts might live wherever XDG_DATA_HOME points, not just the
    /// hardcoded ~/.local/share/fonts. Rather than guess every combination,
    /// this asks fontconfig itself via `fc-list` — since fontconfig already
    /// has each system's real, configured font paths baked into its own
    /// config (including any user overrides), it's a strictly better source
    /// of truth than a hardcoded directory list. The directory walk below
    /// only runs as a fallback for the rare system without fontconfig
    /// installed at all.</summary>
    public static List<FontEntry> DiscoverFonts()
    {
        var viaFontconfig = TryFcList();
        return viaFontconfig ?? DiscoverFontsByWalkingDirectories();
    }

    private static List<FontEntry>? TryFcList()
    {
        try
        {
            var psi = new ProcessStartInfo("fc-list", "--format=%{file}\\t%{family[0]}\\n")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            if (process.ExitCode != 0) return null;

            // Dedup by file path — a single variable-font or TTC file can be
            // listed by fc-list once per named instance/face, but the atlas
            // only cares about the file, and only the first-seen family name
            // is kept as the display label.
            var byPath = new Dictionary<string, string>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length != 2) continue;
                string file = parts[0].Trim();
                string family = parts[1].Trim();
                if (file.Length == 0 || !IsSupportedFontFile(file)) continue;
                byPath.TryAdd(file, family.Length > 0 ? family : Path.GetFileNameWithoutExtension(file));
            }

            return byPath.Count == 0
                ? null // fc-list ran but returned nothing usable — fall back to walking directories
                : byPath.Select(kv => new FontEntry(kv.Key, kv.Value))
                    .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null; // fontconfig not installed
        }
    }

    /// <summary>Fallback for systems without fontconfig: walks a broadened
    /// candidate directory list covering the common Linux/BSD/macOS locations
    /// plus the XDG base-directory spec (XDG_DATA_HOME / XDG_DATA_DIRS), since
    /// a user's fonts may not live at the hardcoded ~/.local/share/fonts if
    /// they've customized their XDG environment.</summary>
    private static List<FontEntry> DiscoverFontsByWalkingDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dh
            ? dh
            : Path.Combine(home, ".local", "share");
        var xdgDataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dd
                ? dd
                : "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);

        var dirs = new List<string>(SearchDirs)
        {
            Path.Combine(xdgDataHome, "fonts"),
            Path.Combine(home, ".fonts"),
            "/usr/local/share/fonts",   // primary location on many BSD ports
            "/usr/X11R6/lib/X11/fonts", // legacy X11 location, still present on some BSDs
            "/run/host/fonts",          // Flatpak sandbox host-font exposure
            "/Library/Fonts",           // macOS, in case fontconfig isn't installed there
            "/System/Library/Fonts",
            Path.Combine(home, "Library", "Fonts"),
        };
        dirs.AddRange(xdgDataDirs.Select(d => Path.Combine(d, "fonts")));

        var found = new List<string>();
        foreach (var dir in dirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                found.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(IsSupportedFontFile));
            }
            catch (UnauthorizedAccessException) { /* skip unreadable subdirs */ }
        }

        return found
            .Distinct()
            .Select(f => new FontEntry(f, Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Restricted to formats SixLabors.Fonts can actually load for
    /// the atlas — fontconfig happily lists bitmap/Type1 formats (.pcf, .pfa,
    /// .pfb) too, which would just crash FontAtlas if picked.</summary>
    private static bool IsSupportedFontFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SearchDirectory(string dir, Func<string, bool> fileNamePredicate)
    {
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
            {
                if (fileNamePredicate(Path.GetFileName(file)))
                    return file;
            }
        }
        catch (UnauthorizedAccessException) { /* skip unreadable subdirs, same as os.walk's default behavior */ }
        return null;
    }

    private static string? TryFcMatch()
    {
        try
        {
            var psi = new ProcessStartInfo("fc-match", "--format=%{file} monospace")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            return process.ExitCode == 0 && output.Length > 0 && File.Exists(output) ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null; // fontconfig not installed — not fatal, just skip this step
        }
    }
}
