using Silk.NET.GLFW;

namespace ChronoTerm.Config;

/// <summary>Replaces _get_screen_dimensions()'s four-method fallback chain
/// (SDL3, raw Xlib, xrandr, xdpyinfo) — GLFW already has a direct monitor query,
/// so there's no need to reimplement Python's SDL-less workarounds here.</summary>
public static class ScreenInfo
{
    public static (int width, int height) GetPrimaryMonitorSize()
    {
        try
        {
            var glfw = Glfw.GetApi();
            if (!glfw.Init())
            {
                Console.WriteLine("[Config] glfwInit failed while querying monitor size — using fallback 1024x768");
                return (1024, 768);
            }

            unsafe
            {
                var monitor = glfw.GetPrimaryMonitor();
                if (monitor is null) return (1024, 768);

                var mode = glfw.GetVideoMode(monitor);
                if (mode is null) return (1024, 768);

                return (mode->Width, mode->Height);
            }
            // Deliberately not calling glfw.Terminate() — the real window
            // (created right after config load) will initialize GLFW again;
            // redundant Init() calls are a documented no-op, and tearing down
            // here would be pure churn with no window yet alive to protect.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Config] Monitor query failed: {ex.Message} — using fallback 1024x768");
            return (1024, 768);
        }
    }
}
