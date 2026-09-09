using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChronoTerm.Pty;

public sealed class LinuxPty : IPty
{
    private readonly SafeFileHandle _masterHandle;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Process _watchProcess; // used only to poll HasExited via /proc, see below

    public int ProcessId { get; }
    public Stream Input => _input;
    public Stream Output => _output;
    public bool HasExited => _watchProcess.HasExited;

    private LinuxPty(int master, int pid)
    {
        ProcessId = pid;
        _masterHandle = new SafeFileHandle((IntPtr)master, ownsHandle: true);
        // Master fd is duplex: one FileStream for write, one for read, same handle.
        // isAsync: false — openpty() doesn't set O_NONBLOCK, and .NET's Unix async
        // FileStream path requires that; ReadAsync/WriteAsync still work fine here,
        // they just run via a thread-pool wrapper instead of true overlapped I/O.
        _input = new FileStream(_masterHandle, FileAccess.Write, bufferSize: 1, isAsync: false);
        _output = new FileStream(_masterHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);

        // Process.GetProcessById lets us reuse the BCL's exit-detection (waitpid under
        // the hood) instead of hand-rolling a SIGCHLD handler for the spike.
        _watchProcess = Process.GetProcessById(pid);
    }

    /// <summary>
    /// Spawns <paramref name="shellPath"/> attached to a fresh pty. Uses
    /// `setsid --ctty` as the direct child so the shell gets a proper controlling
    /// terminal without us forking the .NET runtime ourselves.
    /// </summary>
    public static LinuxPty Spawn(string shellPath, int cols = 80, int rows = 24)
    {
        var winSize = new NativeMethods.WinSize
        {
            ws_row = (ushort)rows,
            ws_col = (ushort)cols,
            ws_xpixel = 0,
            ws_ypixel = 0,
        };

        if (NativeMethods.openpty(out int master, out int slave, IntPtr.Zero, IntPtr.Zero, ref winSize) != 0)
            throw new IOException($"openpty failed (errno {Marshal.GetLastWin32Error()})");

        IntPtr fileActions = Marshal.AllocHGlobal(NativeMethods.FileActionsSize);
        try
        {
            Check(NativeMethods.posix_spawn_file_actions_init(fileActions), "posix_spawn_file_actions_init");

            // Child: slave pty becomes stdin/stdout/stderr, then we don't need the
            // original slave/master fds anymore in the child.
            Check(NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slave, 0), "adddup2 stdin");
            Check(NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slave, 1), "adddup2 stdout");
            Check(NativeMethods.posix_spawn_file_actions_adddup2(fileActions, slave, 2), "adddup2 stderr");
            Check(NativeMethods.posix_spawn_file_actions_addclose(fileActions, slave), "addclose slave");
            Check(NativeMethods.posix_spawn_file_actions_addclose(fileActions, master), "addclose master");

            string?[] argv = { "setsid", "--ctty", shellPath, null };
            string?[] envp = BuildEnvp();

            int rc = NativeMethods.posix_spawnp(out int pid, "setsid", fileActions, IntPtr.Zero, argv, envp);
            if (rc != 0)
                throw new IOException($"posix_spawnp failed (errno {rc})");

            // Parent no longer needs the slave end; the child has its own dup'd copies.
            NativeMethods.close(slave);

            return new LinuxPty(master, pid);
        }
        finally
        {
            NativeMethods.posix_spawn_file_actions_destroy(fileActions);
            Marshal.FreeHGlobal(fileActions);
        }
    }

    public void Resize(int cols, int rows)
    {
        var winSize = new NativeMethods.WinSize
        {
            ws_row = (ushort)rows,
            ws_col = (ushort)cols,
            ws_xpixel = 0,
            ws_ypixel = 0,
        };
        if (NativeMethods.ioctl((int)_masterHandle.DangerousGetHandle(), NativeMethods.TIOCSWINSZ, ref winSize) != 0)
            throw new IOException($"ioctl(TIOCSWINSZ) failed (errno {Marshal.GetLastWin32Error()})");
    }

    private static string?[] BuildEnvp()
    {
        var vars = Environment.GetEnvironmentVariables();
        var list = new List<string?>(vars.Count + 1);
        foreach (System.Collections.DictionaryEntry entry in vars)
            list.Add($"{entry.Key}={entry.Value}");
        list.Add(null);
        return list.ToArray();
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0)
            throw new IOException($"{what} failed (errno {rc})");
    }

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
        _masterHandle.Dispose();
        _watchProcess.Dispose();
    }
}
