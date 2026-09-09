using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace ChronoTerm;

/// <summary>
/// Ports clipboard.py's actual mechanism, not its fallback-tool shape: the
/// Python version used SDL3's SDL_SetClipboardText/SDL_GetClipboardText, which
/// talks to X11/Wayland/Win32/Cocoa internally — no xclip/xsel/wl-copy needed.
/// GLFW (our windowing backend, via Silk.NET) has the exact same built-in
/// clipboard functions, so this calls those directly instead of shelling out.
/// Works on every X11 DE, every Wayland compositor GLFW supports, and Windows —
/// no external packages required, unlike the process-based approach this replaces.
/// </summary>
public static unsafe class Clipboard
{
    private static Glfw? _glfw;
    private static WindowHandle* _windowHandle;

    /// <summary>Call once, after the window exists (e.g. in TerminalWindow.OnLoad —
    /// the native GLFW handle isn't guaranteed populated before that).</summary>
    public static void Initialize(IWindow window)
    {
        _glfw = Glfw.GetApi();
        nint? handle = window.Native?.Glfw;
        if (handle is null)
        {
            Console.WriteLine("[Clipboard] No GLFW handle on this window — is it actually using the GLFW backend?");
            return;
        }
        _windowHandle = (WindowHandle*)handle.Value;
    }

    public static bool CopyToClipboard(string text)
    {
        if (_glfw is null || _windowHandle is null)
        {
            Console.WriteLine("[Clipboard] Not initialized — Clipboard.Initialize(window) was never called or found no handle");
            return false;
        }

        try
        {
            _glfw.SetClipboardString(_windowHandle, text);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Clipboard] SetClipboardString failed: {ex.Message}");
            return false;
        }
    }

    public static string? PasteFromClipboard()
    {
        if (_glfw is null || _windowHandle is null)
        {
            Console.WriteLine("[Clipboard] Not initialized — Clipboard.Initialize(window) was never called or found no handle");
            return null;
        }

        try
        {
            return _glfw.GetClipboardString(_windowHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Clipboard] GetClipboardString failed: {ex.Message}");
            return null;
        }
    }
}