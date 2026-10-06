using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Digger.Engine.Infrastructure;
using Digger.Interop.CorDebug;
using Digger.Interop.Native;

namespace Digger.Engine.Runtime;

// "console": "integratedTerminal" support.
//
// The editor starts `digger --launch-shim=<socket> -- <program> <args>` in its terminal. The
// shim creates the program suspended (so it inherits the terminal as stdin/stdout), reports
// the pid over a Unix socket, waits until the adapter has registered for runtime startup and
// says "resume", then waits for the program and relays its exit code.
//
// Wire protocol (one line each):  shim -> "pid <n>" | "error <message>";  adapter -> "resume";
// shim -> "exit <code>".

/// <summary>Adapter side of the launch shim protocol.</summary>
public sealed class LaunchShimChannel : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cancel = new();
    private Socket? _peer;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _failure;

    private LaunchShimChannel(Socket listener, string path)
    {
        _listener = listener;
        SocketPath = path;
    }

    public string SocketPath { get; }

    public static LaunchShimChannel Create()
    {
        // Unix socket paths are limited to ~100 bytes: keep it short and in the temp dir.
        var path = Path.Combine(Path.GetTempPath(), $"digger-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40] + ".sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new LaunchShimChannel(listener, path);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>Called when the client reports that it could not start the terminal.</summary>
    public void Fail(string message)
    {
        _failure = message;
        _cancel.Cancel();
    }

    /// <summary>Blocks until the shim reports the debuggee's process id.</summary>
    public int WaitForProcess(TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        timeoutSource.CancelAfter(timeout);
        try
        {
            _peer = _listener.AcceptAsync(timeoutSource.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException(
                _failure is null
                    ? "The terminal did not start the program in time."
                    : $"The editor could not run the program in a terminal: {_failure}",
                ex);
        }

        var stream = new NetworkStream(_peer, ownsSocket: false);
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var line = _reader.ReadLine() ?? throw new InvalidOperationException("The launch shim exited before starting the program.");
        if (line.StartsWith("pid ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            return pid;
        }

        throw new InvalidOperationException(line.StartsWith("error ", StringComparison.Ordinal) ? line[6..] : $"Unexpected message from the launch shim: {line}");
    }

    public void Resume() => _writer?.WriteLine("resume");

    /// <summary>Reports the exit code relayed by the shim (on a background thread).</summary>
    public void WatchExit(Action<int> onExit)
    {
        var reader = _reader ?? throw new InvalidOperationException("No shim connected.");
        var thread = new Thread(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith("exit ", StringComparison.Ordinal)
                        && int.TryParse(line.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture, out var code))
                    {
                        onExit(code);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Log.Warn($"Launch shim connection closed: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "Digger shim exit",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _cancel.Dispose();
        _writer?.Dispose();
        _reader?.Dispose();
        _peer?.Dispose();
        _listener.Dispose();
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>Shim side: runs inside the editor's terminal.</summary>
public static class LaunchShim
{
    public static unsafe int Run(string socketPath, IReadOnlyList<string> command)
    {
        if (command.Count == 0)
        {
            Console.Error.WriteLine("digger --launch-shim: no program given");
            return 2;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        using var stream = new NetworkStream(socket);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var arguments = new string[command.Count - 1];
        for (var i = 1; i < command.Count; i++)
        {
            arguments[i - 1] = command[i];
        }

        var commandLine = (CommandLine.Build(command[0], arguments) + '\0').ToCharArray();
        int hr;
        uint pid;
        nint resumeHandle;
        fixed (char* line = commandLine)
        {
            hr = DbgShim.CreateProcessForLaunch(line, bSuspendProcess: true, null, null, out pid, out resumeHandle);
        }

        if (hr < 0)
        {
            writer.WriteLine($"error Failed to start '{command[0]}' (0x{hr:X8})");
            return 1;
        }

        writer.WriteLine($"pid {pid}");
        if (reader.ReadLine() != "resume")
        {
            // The adapter went away before the program started.
            _ = Libc.kill((int)pid, Libc.SIGKILL);
            _ = DbgShim.CloseResumeHandle(resumeHandle);
            return 1;
        }

        HResults.Check(DbgShim.ResumeProcess(resumeHandle));
        _ = DbgShim.CloseResumeHandle(resumeHandle);

        var exitCode = Libc.TryPeekExitCode((int)pid, out var code) ? code : 0;
        _ = Libc.waitpid((int)pid, out _, Libc.WNOHANG);
        try
        {
            writer.WriteLine($"exit {exitCode}");
        }
        catch (IOException)
        {
            // The adapter may already be gone.
        }

        return exitCode;
    }
}
