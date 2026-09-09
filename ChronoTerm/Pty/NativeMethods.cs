using System.Runtime.InteropServices;

namespace ChronoTerm.Pty;

/// <summary>
/// Deliberately does NOT call fork() from managed code. Forking a multithreaded
/// CLR process (GC threads, thread pool, finalizer thread all still "exist" in the
/// child's copied memory but aren't running) is unsafe if the child touches anything
/// managed before exec(). Instead: openpty() for the fds, then posix_spawn() to do
/// fork+exec atomically via the C library, and hand off controlling-terminal setup
/// to `setsid --ctty` (util-linux, present on Arch by default) instead of doing the
/// setsid()+ioctl(TIOCSCTTY) dance ourselves between fork and exec.
/// </summary>
internal static class NativeMethods
{
    private const string Libc = "libc";

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    // openpty(int *amaster, int *aslave, char *name, const struct termios *termp, const struct winsize *winp)
    [DllImport(Libc, SetLastError = true)]
    public static extern int openpty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref WinSize winp);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, ref WinSize winsize);

    // TIOCSWINSZ - Linux x86_64/arm64 value (0x5414). Same on all Linux arches that matter here.
    public const ulong TIOCSWINSZ = 0x5414;

    // --- posix_spawn plumbing ---
    // posix_spawn_file_actions_t is opaque in glibc; 128 bytes is comfortably
    // larger than glibc's real definition (~80 bytes on x86_64) and gives headroom
    // across the arches we care about (x86_64, aarch64).
    public const int FileActionsSize = 128;

    [DllImport(Libc, SetLastError = true)]
    public static extern int posix_spawn_file_actions_init(IntPtr file_actions);

    [DllImport(Libc, SetLastError = true)]
    public static extern int posix_spawn_file_actions_destroy(IntPtr file_actions);

    [DllImport(Libc, SetLastError = true)]
    public static extern int posix_spawn_file_actions_adddup2(IntPtr file_actions, int fd, int newfd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addclose(IntPtr file_actions, int fd);

    // int posix_spawnp(pid_t *pid, const char *file, const posix_spawn_file_actions_t *file_actions,
    //                   const posix_spawnattr_t *attrp, char *const argv[], char *const envp[]);
    [DllImport(Libc, SetLastError = true)]
    public static extern int posix_spawnp(
        out int pid,
        string file,
        IntPtr file_actions,
        IntPtr attrp,
        string?[] argv,
        string?[] envp);
}
