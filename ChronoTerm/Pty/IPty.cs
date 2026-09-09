namespace ChronoTerm.Pty;

/// <summary>
/// Cross-platform handle to a spawned shell attached to a pseudo-terminal.
/// Linux impl: LinuxPty (openpty + posix_spawn, no CLR fork).
/// Windows impl (future): ConPTY (CreatePseudoConsole).
/// </summary>
public interface IPty : IDisposable
{
    int ProcessId { get; }
    Stream Input { get; }   // write bytes to send to the shell
    Stream Output { get; }  // read bytes produced by the shell

    void Resize(int cols, int rows);
    bool HasExited { get; }
}
