using System;
using System.Runtime.InteropServices;

namespace Digger.Interop.Native;

/// <summary>Minimal POSIX bindings used for stdio redirection and child reaping.</summary>
public static partial class Libc
{
    private const string Library = "libc";

    public const int O_RDONLY = 0;
    public const int O_WRONLY = 1;
    public const int F_SETFD = 2;
    public const int FD_CLOEXEC = 1;
    public const int WNOHANG = 1;
    public const int SIGKILL = 9;

    [LibraryImport(Library, SetLastError = true)]
    public static partial int dup(int fd);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int dup2(int oldfd, int newfd);

    [LibraryImport(Library, SetLastError = true)]
    public static unsafe partial int pipe(int* fds);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int waitpid(int pid, out int status, int options);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int kill(int pid, int sig);

    [LibraryImport(Library, SetLastError = true)]
    public static partial int setpgid(int pid, int pgid);

    [LibraryImport(Library, SetLastError = true)]
    private static unsafe partial int waitid(int idtype, int id, byte* infop, int options);

    /// <summary>
    /// Blocks until child <paramref name="pid"/> exits and returns its exit code WITHOUT
    /// reaping it (WNOWAIT), so whoever else waits on the child (the CoreCLR PAL inside
    /// mscordbi does) still can.
    /// </summary>
    public static unsafe bool TryPeekExitCode(int pid, out int exitCode)
    {
        const int P_PID = 1;
        const int WEXITED = 4;
        const int CLD_EXITED = 1;
        var wnowait = OperatingSystem.IsMacOS() ? 0x20 : 0x01000000;

        // siginfo_t is 128 bytes on both Linux and macOS.
        var info = stackalloc byte[128];
        new Span<byte>(info, 128).Clear();
        int result;
        do
        {
            result = waitid(P_PID, pid, info, WEXITED | wnowait);
        }
        while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR

        if (result != 0)
        {
            exitCode = 0;
            return false;
        }

        var code = *(int*)(info + 8);
        var status = OperatingSystem.IsMacOS() ? *(int*)(info + 20) : *(int*)(info + 24);
        exitCode = code == CLD_EXITED ? status : 128 + status;
        return true;
    }

    /// <summary>Decodes a <c>waitpid</c> status into a process exit code (128+signal for signals).</summary>
    public static int DecodeExitStatus(int status)
    {
        var signal = status & 0x7f;
        return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
    }
}
