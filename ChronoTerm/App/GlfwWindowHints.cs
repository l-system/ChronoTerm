using Silk.NET.GLFW;

namespace ChronoTerm.App;

/// <summary>Raw Silk.NET.GLFW calls that Silk.NET.Windowing's IWindow doesn't
/// expose a portable API for. Deliberately kept in its own file rather than
/// folded into TerminalWindow.cs: Silk.NET.GLFW defines its own MouseButton
/// and KeyModifiers types, both of which collide with types TerminalWindow.cs
/// already uses constantly (Silk.NET.Input.MouseButton, and ChronoTerm's own
/// Input.KeyModifiers) — a blanket `using Silk.NET.GLFW;` there makes every
/// unqualified reference to either name ambiguous. Scoping the using
/// directive to just this file, which only ever needs the GLFW types, avoids
/// the collision entirely instead of requiring every call site elsewhere to
/// fully-qualify one or the other.</summary>
internal static class GlfwWindowHints
{
    private static Glfw? _api;

    /// <summary>Undoes a Silk.NET.Windowing side effect: its WindowBorder.Hidden
    /// case bundles in SetWindowAttrib(Resizable, false) alongside removing
    /// decorations (confirmed directly in Silk.NET's GlfwWindow.cs source) —
    /// GLFW's own two hints are independent at the native level; this is
    /// purely a C#-side conflation. That side effect is what breaks
    /// window-manager tiling/snapping (e.g. KDE's Meta+Arrow quick-tile) the
    /// moment a titlebar is hidden: those features need to resize the
    /// window, and a window that's declared itself non-resizable via
    /// WM_NORMAL_HINTS (min size == max size) is correctly refused by any
    /// ICCCM-compliant window manager. See TerminalWindow.SetWindowBorder for
    /// where this gets called from.</summary>
    public static void ForceResizable(nint? glfwWindowHandle)
    {
        if (glfwWindowHandle is null) return;

        try
        {
            _api ??= Glfw.GetApi();
            unsafe
            {
                _api.SetWindowAttrib((WindowHandle*)glfwWindowHandle.Value, WindowAttributeSetter.Resizable, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[app] Could not restore window resizability: {ex.Message}");
        }
    }
}
