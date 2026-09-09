using Silk.NET.OpenGL;

namespace ChronoTerm.Rendering;

/// <summary>
/// Ports renderer.py's Quad class: one NDC-space quad (position + UV), reused
/// across every full-screen shader pass by just switching the bound program
/// and textures before calling Draw().
/// </summary>
public sealed unsafe class FullscreenQuad : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;

    // pos.xy, uv.xy per vertex. v=1 at NDC-top, v=0 at NDC-bottom: OpenGL
    // render-to-texture stores texel row 0 as the BOTTOM of what was rendered
    // (the classic "GL images are bottom-up" fact), so this is the mapping that
    // correctly un-flips content rendered into an FBO by our other shaders —
    // every full-screen pass (blur, burn-in, composite) reuses this same quad,
    // so getting this right once here fixes it everywhere downstream too.
    private static readonly float[] Vertices =
    {
        -1f, -1f,   0f, 0f,
         1f, -1f,   1f, 0f,
         1f,  1f,   1f, 1f,
        -1f,  1f,   0f, 1f,
    };
    private static readonly uint[] Indices = { 0, 1, 2, 2, 3, 0 };

    public FullscreenQuad(GL gl)
    {
        _gl = gl;
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();

        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, Vertices, BufferUsageARB.StaticDraw);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, Indices, BufferUsageARB.StaticDraw);

        const uint stride = 4 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));

        gl.BindVertexArray(0);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}