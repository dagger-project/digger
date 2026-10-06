using System;
using System.IO;
using System.Text;
using System.Threading;
using Digger.Engine.Infrastructure;
using Digger.Interop.Native;
using Microsoft.Win32.SafeHandles;

namespace Digger;

/// <summary>
/// Separates the protocol channel from the debuggee's stdio (Unix).
///
/// dbgshim starts the debuggee with our file descriptors 0/1/2, and stdin/stdout carry the
/// protocol. So before anything else we move the protocol to private descriptors, point fd 0
/// at /dev/null and fds 1/2 at pipes, and turn whatever arrives on those pipes (the debuggee's
/// output, or anything else written to our stdout/stderr) into DAP output events.
/// </summary>
internal sealed class StdioRedirector : IDisposable
{
    private readonly int _stdoutRead;
    private readonly int _stderrRead;

    private StdioRedirector(Stream input, Stream output, int stdoutRead, int stderrRead)
    {
        ProtocolInput = input;
        ProtocolOutput = output;
        _stdoutRead = stdoutRead;
        _stderrRead = stderrRead;
    }

    public Stream ProtocolInput { get; }

    public Stream ProtocolOutput { get; }

    public static unsafe StdioRedirector Create(bool protocolOnStdio)
    {
        if (OperatingSystem.IsWindows())
        {
            return new StdioRedirector(Console.OpenStandardInput(), Console.OpenStandardOutput(), -1, -1);
        }

        var protocolIn = !protocolOnStdio ? -1 : CloseOnExec(Check(Libc.dup(0), "dup(0)"));
        var protocolOut = !protocolOnStdio ? -1 : CloseOnExec(Check(Libc.dup(1), "dup(1)"));

        var devNull = Check(Libc.open("/dev/null", Libc.O_RDONLY), "open(/dev/null)");
        _ = Check(Libc.dup2(devNull, 0), "dup2(stdin)");
        _ = Libc.close(devNull);

        var stdoutRead = RedirectToPipe(1);
        var stderrRead = RedirectToPipe(2);

        Stream input = protocolIn < 0 ? Stream.Null : OpenFd(protocolIn, FileAccess.Read);
        Stream output = protocolOut < 0 ? Stream.Null : OpenFd(protocolOut, FileAccess.Write);
        return new StdioRedirector(input, output, stdoutRead, stderrRead);

        static int RedirectToPipe(int target)
        {
            var fds = stackalloc int[2];
            _ = Check(Libc.pipe(fds), "pipe");
            _ = Check(Libc.dup2(fds[1], target), "dup2(pipe)");
            _ = Libc.close(fds[1]);
            return CloseOnExec(fds[0]);
        }
    }

    /// <summary>Starts background readers that forward debuggee output.</summary>
    public void StartPumping(Action<string, string> onOutput)
    {
        if (_stdoutRead >= 0)
        {
            Pump(_stdoutRead, "stdout", onOutput);
        }

        if (_stderrRead >= 0)
        {
            Pump(_stderrRead, "stderr", onOutput);
        }
    }

    private static void Pump(int fd, string category, Action<string, string> onOutput)
    {
        var thread = new Thread(() =>
        {
            using var stream = OpenFd(fd, FileAccess.Read);
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            try
            {
                int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var count = decoder.GetChars(bytes, 0, read, chars, 0);
                    if (count > 0)
                    {
                        onOutput(category, new string(chars, 0, count));
                    }
                }
            }
            catch (IOException ex)
            {
                Log.Warn($"{category} pump stopped: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "Digger " + category,
        };
        thread.Start();
    }

    // The FileStream takes ownership of the handle.
#pragma warning disable CA2000
    private static FileStream OpenFd(int fd, FileAccess access) =>
        new(new SafeFileHandle(fd, ownsHandle: true), access, bufferSize: 0);
#pragma warning restore CA2000

    private static int CloseOnExec(int fd)
    {
        _ = Libc.fcntl(fd, Libc.F_SETFD, Libc.FD_CLOEXEC);
        return fd;
    }

    private static int Check(int result, string what) =>
        result >= 0 ? result : throw new IOException($"{what} failed (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");

    public void Dispose()
    {
        ProtocolInput.Dispose();
        ProtocolOutput.Dispose();
    }
}
