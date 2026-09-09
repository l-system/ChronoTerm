using Silk.NET.OpenGL;

namespace ChronoTerm.Rendering;

internal static class Shader
{
    public static uint Build(GL gl, string vertexSrc, string fragmentSrc)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, vertexSrc);
        uint fs = Compile(gl, ShaderType.FragmentShader, fragmentSrc);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        gl.DetachShader(program, vs);
        gl.DetachShader(program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        return program;
    }

    private static uint Compile(GL gl, ShaderType type, string src)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, src);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }
}
