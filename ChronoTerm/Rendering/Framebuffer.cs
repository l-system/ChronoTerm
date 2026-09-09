using Silk.NET.OpenGL;

namespace ChronoTerm.Rendering;

/// <summary>
/// Wraps a single-color-attachment GL framebuffer + its backing texture.
/// Mirrors renderer.py's _setup_fbos()/_cleanup_fbos() pattern: on resize, the
/// whole thing is torn down and recreated at the new size rather than trying
/// to resize a texture in place — same approach, ported directly.
/// </summary>
public sealed unsafe class Framebuffer : IDisposable
{
    private readonly GL _gl;
    public uint FboHandle { get; private set; }
    public uint TextureHandle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Framebuffer(GL gl, int width, int height)
    {
        _gl = gl;
        Create(width, height);
    }

    private void Create(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        TextureHandle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, TextureHandle);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        FboHandle = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FboHandle);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, TextureHandle, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            Console.WriteLine($"[Rendering] Framebuffer incomplete after creation: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>No-op if the size hasn't actually changed — Resize gets called
    /// every frame from TerminalGlRenderer.Resize in some call patterns, and
    /// tearing down/recreating a same-size FBO every frame would be wasteful.</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        ReleaseGlResources();
        Create(width, height);
    }

    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FboHandle);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public static void BindDefault(GL gl, int width, int height)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
    }

    private void ReleaseGlResources()
    {
        _gl.DeleteFramebuffer(FboHandle);
        _gl.DeleteTexture(TextureHandle);
    }

    public void Dispose() => ReleaseGlResources();
}
