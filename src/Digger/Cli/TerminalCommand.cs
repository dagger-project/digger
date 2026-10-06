using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Digger.Engine.Infrastructure;
using Digger.Interop.Native;

namespace Digger.Cli;

/// <summary>
/// <c>digger debug</c>, <c>digger exec</c> and <c>digger attach</c>: parse the command line,
/// build if needed, and hand over to the interactive <see cref="Repl"/>.
/// </summary>
internal static class TerminalCommand
{
    public const string Usage = """
        debug options (digger debug [project] [-- args...]):
          --configuration=name  build configuration (default Debug)
          --framework=tfm       target framework of a multi-targeted project
          --build-flags=flags   extra arguments for dotnet build, e.g. --build-flags="-p:Foo=1"
          --no-build            debug what was built last

        debug / exec / attach options:
          --wd=dir              working directory of the program (default: current directory)
          --init=file           run the debugger commands in file at startup
          --log=path            write a diagnostic log
          --dbgshim=path        use a specific libdbgshim
        """;

    public static int Run(string command, string[] args)
    {
        string? positional = null;
        string? configuration = null;
        string? framework = null;
        string? buildFlags = null;
        string? workingDirectory = null;
        string? initFile = null;
        string? logPath = Environment.GetEnvironmentVariable("DIGGER_LOG");
        string? dbgshimPath = Environment.GetEnvironmentVariable("DIGGER_DBGSHIM");
        var noBuild = false;
        List<string> programArguments = [];

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument == "--")
            {
                programArguments.AddRange(args[(i + 1)..]);
                break;
            }

            if (!argument.StartsWith('-') || argument == "-")
            {
                if (positional is null)
                {
                    positional = argument;
                    continue;
                }

                // Delve also accepts program arguments without '--' after the target.
                programArguments.AddRange(args[i..]);
                break;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            string Value() => equals >= 0 ? argument[(equals + 1)..]
                : i + 1 < args.Length ? args[++i]
                : throw new CommandException($"{name} needs a value");

            try
            {
                switch (name)
                {
                    case "--configuration" when command == "debug": configuration = Value(); break;
                    case "--framework" or "-f" when command == "debug": framework = Value(); break;
                    case "--build-flags" when command == "debug": buildFlags = Value(); break;
                    case "--no-build" when command == "debug": noBuild = true; break;
                    case "--wd": workingDirectory = Value(); break;
                    case "--init": initFile = Value(); break;
                    case "--log": logPath = Value(); break;
                    case "--dbgshim": dbgshimPath = Value(); break;
                    case "--help" or "-h":
                        Console.WriteLine(Usage);
                        return 0;
                    default:
                        Console.Error.WriteLine($"digger {command}: unknown option '{argument}'");
                        return 2;
                }
            }
            catch (CommandException ex)
            {
                Console.Error.WriteLine($"digger {command}: {ex.Message}");
                return 2;
            }
        }

        if (logPath is { Length: > 0 })
        {
            Log.Open(logPath);
        }

        DbgShim.UseLibrary(dbgshimPath);

        DebugTarget target;
        switch (command)
        {
            case "debug":
                var build = new BuildSettings(positional)
                {
                    Configuration = configuration ?? "Debug",
                    Framework = framework,
                    ExtraArguments = SplitFlags(buildFlags),
                };
                var program = noBuild ? ProjectBuilder.FindTargetPath(build) : ProjectBuilder.Build(build);
                if (program is null)
                {
                    return 1;
                }

                target = new DebugTarget { Program = program, Build = build };
                break;
            case "exec":
                if (positional is null)
                {
                    Console.Error.WriteLine("digger exec: the program to run is required, e.g. digger exec bin/Debug/net10.0/MyApp.dll");
                    return 2;
                }

                if (!File.Exists(positional))
                {
                    Console.Error.WriteLine($"digger exec: '{positional}' does not exist");
                    return 1;
                }

                target = new DebugTarget { Program = Path.GetFullPath(positional) };
                break;
            default:
                if (positional is null || !int.TryParse(positional, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
                {
                    Console.Error.WriteLine("digger attach: a process id is required, e.g. digger attach 1234");
                    return 2;
                }

                target = new DebugTarget { ProcessId = processId };
                break;
        }

        target = target with
        {
            Arguments = programArguments,
            WorkingDirectory = workingDirectory is null ? Environment.CurrentDirectory : Path.GetFullPath(workingDirectory),
        };

        var initialCommands = new List<string>();
        if (initFile is not null)
        {
            try
            {
                foreach (var line in File.ReadAllLines(initFile))
                {
                    if (line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith('#'))
                    {
                        initialCommands.Add(trimmed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"digger {command}: cannot read {initFile}: {ex.Message}");
                return 1;
            }
        }

        using var repl = new Repl(target);
        return repl.Run(initialCommands);
    }

    /// <summary>Splits <c>--build-flags</c> on spaces, honoring single and double quotes.</summary>
    internal static List<string> SplitFlags(string? flags)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(flags))
        {
            return result;
        }

        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;
        foreach (var c in flags)
        {
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else
                {
                    _ = current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    _ = current.Clear();
                    inToken = false;
                }
            }
            else
            {
                _ = current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
