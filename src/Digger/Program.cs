using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Digger;
using Digger.Cli;
using Digger.Engine.Infrastructure;
using Digger.Engine.Runtime;
using Digger.Interop.Native;
using Digger.Protocol;

// digger — a debugger for .NET.
//
//   digger debug [project] [-- args]   build and debug a project in the terminal (like `dlv debug`)
//   digger exec <program> [-- args]    debug a built program in the terminal (like `dlv exec`)
//   digger attach <pid>                attach to a running process in the terminal
//   digger dap                         speak DAP over stdin/stdout (what editors run)
//   digger dap --server[=4711]         listen on a TCP port instead (handy for debugging digger)
//   digger dap --log=/tmp/digger.log   write a diagnostic log (add --trace for full protocol traffic)
//   digger dap --dbgshim=/path/libdbgshim.so
//   digger version | help
//   `dap` accepts and ignores --interpreter=vscode, for netcoredbg-style editor setups.

const string Usage = """
    Digger is a debugger for .NET (CoreCLR) applications.

    usage: digger <command> [options]

    commands:
      debug     build the project in the current directory (or the given one) and debug it
      exec      debug an already built program (.dll or apphost executable)
      attach    attach to a running .NET process
      dap       run a Debug Adapter Protocol server for an editor, over stdin/stdout
      version   print the version
      help      show this help

    examples:
      digger debug                              build ./ and debug it
      digger debug src/MyApp -- --port 8080     arguments after -- go to the program
      digger exec bin/Debug/net10.0/MyApp.dll
      digger attach 1234

    dap options:
      --server[=port]   listen on 127.0.0.1:port (default 4711) instead of stdin/stdout
      --log=path        write a diagnostic log (or set DIGGER_LOG)
      --trace           also log every protocol message
      --dbgshim=path    use a specific libdbgshim (or set DIGGER_DBGSHIM)

    """ + TerminalCommand.Usage;

// Launch shim mode: runs in the editor's terminal for "console": "integratedTerminal".
if (args.Length > 0 && args[0].StartsWith("--launch-shim=", StringComparison.Ordinal))
{
    var separator = Array.IndexOf(args, "--");
    DbgShim.UseLibrary(Environment.GetEnvironmentVariable("DIGGER_DBGSHIM"));
    return LaunchShim.Run(args[0]["--launch-shim=".Length..], separator < 0 ? args[1..] : args[(separator + 1)..]);
}

switch (args.Length > 0 ? args[0] : "help")
{
    case "dap":
        break;
    case "debug" or "exec" or "attach":
        return TerminalCommand.Run(args[0], args[1..]);
    case "version" or "--version":
        await Console.Out.WriteLineAsync(typeof(DapServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
        return 0;
    case "help" or "--help" or "-h":
        await Console.Out.WriteLineAsync(Usage);
        return 0;
    default:
        await Console.Error.WriteLineAsync($"digger: unknown command '{args[0]}'\n");
        await Console.Error.WriteLineAsync(Usage);
        return 2;
}

string? logPath = Environment.GetEnvironmentVariable("DIGGER_LOG");
string? dbgshimPath = Environment.GetEnvironmentVariable("DIGGER_DBGSHIM");
int? serverPort = null;
var trace = false;

foreach (var argument in args[1..])
{
    var (name, value) = SplitOption(argument);
    switch (name)
    {
        case "--log":
            logPath = value;
            break;
        case "--trace":
            trace = true;
            break;
        case "--dbgshim":
            dbgshimPath = value;
            break;
        case "--server":
            serverPort = value is null ? 4711 : int.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--interpreter" or "--engineLogging":
            break;
        case "--help" or "-h":
            await Console.Out.WriteLineAsync(Usage);
            return 0;
        default:
            await Console.Error.WriteLineAsync($"digger dap: unknown option '{argument}'");
            return 2;
    }
}

if (logPath is { Length: > 0 })
{
    Log.Open(logPath);
}

DbgShim.UseLibrary(dbgshimPath);
Log.Info($"digger starting (pid {Environment.ProcessId}, server: {serverPort?.ToString(CultureInfo.InvariantCulture) ?? "stdio"})");

using var stdio = StdioRedirector.Create(protocolOnStdio: serverPort is null);
Stream input = stdio.ProtocolInput;
Stream output = stdio.ProtocolOutput;
TcpClient? client = null;
if (serverPort is { } port)
{
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    Log.Info($"Waiting for a DAP client on 127.0.0.1:{port}");
    client = await listener.AcceptTcpClientAsync();
    client.NoDelay = true;
    input = output = client.GetStream();
}

using var cancellation = new CancellationTokenSource();
using var connection = new DapConnection(input, output, trace && Log.IsEnabled ? Log.Info : null);
using var dispatcher = new EngineDispatcher();
using var server = new DapServer(connection, dispatcher);
stdio.StartPumping(server.Output);

try
{
    await server.RunAsync(cancellation.Token);
}
finally
{
    await cancellation.CancelAsync();
    client?.Dispose();
    Log.Info("digger exiting");
}

return 0;

static (string Name, string? Value) SplitOption(string argument)
{
    var equals = argument.IndexOf('=', StringComparison.Ordinal);
    return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
}

/// <summary>Exists so tooling has a type to anchor the top-level program on.</summary>
internal static partial class Program;
