using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Digger.Engine.Infrastructure;

/// <summary>
/// Process-wide diagnostic log. Disabled unless <see cref="Open"/> is called: stdout and
/// stderr belong to the protocol and the debuggee respectively.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? s_writer;

    public static bool IsEnabled => s_writer is not null;

    public static void Open(string path)
    {
        lock (Gate)
        {
            s_writer?.Dispose();
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            s_writer = new StreamWriter(stream) { AutoFlush = true };
        }
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        if (Volatile.Read(ref s_writer) is null)
        {
            return;
        }

        lock (Gate)
        {
            s_writer?.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId,3}] {message}"));
        }
    }
}
