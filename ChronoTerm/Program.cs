using ChronoTerm.App;
using ChronoTerm.Config;
using ChronoTerm.Pty;

// Config drives window size/padding/titlebar, font path/size/spacing, colors,
// cursor blink+shape, and effects (glow/vignette/scanlines/curvature/burn-in
// are all live, read fresh every frame — see ChronoTermConfig.cs). First run
// creates ~/.config/ChronoTerm/config.yaml (Linux-only — see LinuxPty) with
// defaults; named presets live alongside it in ~/.config/ChronoTerm/configs/.

if (args.Contains("-v") || args.Contains("--version"))
{
    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    Console.WriteLine($"ChronoTerm {version?.ToString(3) ?? "(unknown)"}");
    return;
}

if (args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("""
        ChronoTerm — a GPU-rendered terminal emulator

        Usage: chronoterm [options]

        Options:
          -h, --help       Show this help and exit
          -v, --version    Show version and exit

        Config file:    ~/.config/ChronoTerm/config.yaml
        Saved presets:  ~/.config/ChronoTerm/configs/

        Right-click anywhere in the window to open settings.
        """);
    return;
}

var configManager = new ConfigManager();

// Respect the user's actual login shell instead of assuming bash — not every
// system has bash (fish/zsh are common, and bash isn't guaranteed present on
// a minimal install), and hardcoding it would silently misbehave or fail to
// spawn entirely on those setups. /bin/sh is POSIX-guaranteed to exist as a
// last resort if $SHELL is unset or points somewhere that doesn't exist.
string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } configured && File.Exists(configured)
    ? configured
    : "/bin/sh";

try
{
    using var pty = LinuxPty.Spawn(shell);
    Console.WriteLine($"[app] spawned pid={pty.ProcessId} shell={shell}");

    using var window = new TerminalWindow(pty, configManager.Config, configManager);
    window.Run();

    Console.WriteLine("[app] window closed");
}
catch (Exception ex)
{
    // A raw unhandled-exception stack trace is a poor first impression for a
    // published build — this covers the realistic startup failure modes
    // (missing libglfw.so.3, no GPU/display/driver, $SHELL spawn failure)
    // with an actionable message, while still exiting non-zero so scripting
    // and package sanity-checks (e.g. an AUR .install hook) can detect failure.
    Console.Error.WriteLine($"[app] Fatal error: {ex.Message}");
    Console.Error.WriteLine("If this looks like a missing dependency, check that libglfw.so.3, an OpenGL driver, and a monospace font are installed.");
    Environment.Exit(1);
}
