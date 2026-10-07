using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Digger.Engine;

namespace Digger.Cli;

/// <content>Debugger settings and session tools: config, source, edit and transcript.</content>
internal sealed partial class Repl
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _substitutePaths = new(StringComparer.Ordinal);
    private int _sourceListLineCount = 5;
    private int _maxArrayValues = 64;
    private bool _justMyCode = true;
    private bool _evaluateProperties = true;
    private bool _symbolServer;
    private bool _sourceLink = true;
    private Transcript? _transcript;

    /// <summary>A setting <c>config</c> can show and change.</summary>
    private sealed record Setting(string Name, string Description, Func<string> Get, Action<string> Set, bool NeedsRestart = false);

    private List<Setting> Settings() =>
    [
        new("source-list-line-count", "lines of source shown above and below the current line", () => Format(_sourceListLineCount), v => _sourceListLineCount = ParseNumber(v)),
        new("max-array-values", "elements of a collection printed by print and locals -v", () => Format(_maxArrayValues), v => _maxArrayValues = ParseNumber(v)),
        new("just-my-code", "stop and step only in your code", () => Format(_justMyCode), v => _justMyCode = ParseBool(v), NeedsRestart: true),
        new("evaluate-properties", "run property getters when printing objects", () => Format(_evaluateProperties), v => _evaluateProperties = ParseBool(v), NeedsRestart: true),
        new("symbol-server", "download missing symbols from the Microsoft and NuGet symbol servers", () => Format(_symbolServer), v => _symbolServer = ParseBool(v), NeedsRestart: true),
        new("source-link", "download sources through Source Link when they are not on disk", () => Format(_sourceLink), v => _sourceLink = ParseBool(v), NeedsRestart: true),
    ];

    private SessionOptions CreateSessionOptions() => new()
    {
        JustMyCode = _justMyCode,
        EvaluateProperties = _evaluateProperties,
        SymbolServer = _symbolServer,
        SourceLink = _sourceLink,
        SourceFileMap = _substitutePaths.Count == 0 ? null : new Dictionary<string, string>(_substitutePaths, StringComparer.Ordinal),
    };

    // ---- config -------------------------------------------------------------------------

    private void Config(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case [] or ["-list"]:
                ListConfig();
                return;
            case ["-save"]:
                SaveConfig();
                return;
            case ["alias", var command, var alias]:
                if (FindCommand(command) is not { } target || _aliases.ContainsKey(command))
                {
                    throw new CommandException($"There is no command '{command}'.");
                }

                if (_commands.Exists(c => c.Names.Contains(alias, StringComparer.Ordinal)))
                {
                    throw new CommandException($"'{alias}' is already a command.");
                }

                _aliases[alias] = target.Names[0];
                return;
            case ["alias", var alias]:
                if (!_aliases.Remove(alias))
                {
                    throw new CommandException($"There is no alias '{alias}'.");
                }

                return;
            case ["substitute-path", "-clear"]:
                _substitutePaths.Clear();
                NoteRestart();
                return;
            case ["substitute-path", var from, var to]:
                _substitutePaths[from] = to;
                NoteRestart();
                return;
            case ["substitute-path", var from]:
                if (!_substitutePaths.Remove(from))
                {
                    throw new CommandException($"There is no substitute-path rule for '{from}'.");
                }

                NoteRestart();
                return;
        }

        var setting = Settings().Find(s => s.Name == parts[0]) ?? throw new CommandException($"Unknown setting '{parts[0]}'; 'config -list' shows them.");
        if (parts.Length == 1)
        {
            Console.WriteLine($"{setting.Name} = {setting.Get()}");
            return;
        }

        setting.Set(string.Join(' ', parts.Skip(1)));
        if (setting.NeedsRestart)
        {
            NoteRestart();
        }
    }

    private void NoteRestart()
    {
        if (_state is State.Running or State.Stopped)
        {
            Console.WriteLine(Ansi.Dim("This takes effect when the program restarts."));
        }
    }

    private void ListConfig()
    {
        foreach (var setting in Settings())
        {
            Console.WriteLine($"{setting.Name,-24} {setting.Get(),-6} {Ansi.Dim(setting.Description)}");
        }

        foreach (var (from, to) in _substitutePaths)
        {
            Console.WriteLine($"substitute-path          {from} → {to}");
        }

        foreach (var (alias, command) in _aliases.OrderBy(static a => a.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"alias                    {alias} → {command}");
        }
    }

    /// <summary>The config file is a list of <c>config</c> commands, run at startup.</summary>
    private void SaveConfig()
    {
        var path = ConfigPath() ?? throw new InvalidOperationException("There is no home directory to save the configuration in.");
        var defaults = new Repl.Defaults();
        var builder = new StringBuilder("# digger configuration: written by 'config -save', run at startup.\n");
        foreach (var setting in Settings())
        {
            if (setting.Get() != defaults.Get(setting.Name))
            {
                _ = builder.Append(CultureInfo.InvariantCulture, $"config {setting.Name} {setting.Get()}\n");
            }
        }

        foreach (var (from, to) in _substitutePaths)
        {
            _ = builder.Append(CultureInfo.InvariantCulture, $"config substitute-path {from} {to}\n");
        }

        foreach (var (alias, command) in _aliases)
        {
            _ = builder.Append(CultureInfo.InvariantCulture, $"config alias {command} {alias}\n");
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString());
        Console.WriteLine($"Configuration saved to {path}");
    }

    /// <summary>Default values, to save only what differs.</summary>
    private sealed class Defaults
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal)
        {
            ["source-list-line-count"] = "5",
            ["max-array-values"] = "64",
            ["just-my-code"] = "true",
            ["evaluate-properties"] = "true",
            ["symbol-server"] = "false",
            ["source-link"] = "true",
        };

        public string? Get(string name) => _values.GetValueOrDefault(name);
    }

    /// <summary>Runs the saved configuration; errors are reported but do not stop startup.</summary>
    private void LoadConfig()
    {
        if (ConfigPath() is not { } path || !File.Exists(path))
        {
            return;
        }

        foreach (var line in ReadCommandFile(path))
        {
            Execute(line);
        }
    }

    private static string? ConfigPath() => ConfigDirectory() is { } directory ? Path.Combine(directory, "config") : null;

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";

    private static int ParseNumber(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new CommandException($"'{text}' is not a number.");

    private static bool ParseBool(string text) => text switch
    {
        "true" or "on" or "yes" or "1" => true,
        "false" or "off" or "no" or "0" => false,
        _ => throw new CommandException($"'{text}' is not true or false."),
    };

    // ---- source -------------------------------------------------------------------------

    private void Source(string arguments)
    {
        if (arguments.Length == 0)
        {
            throw new CommandException("usage: source <file>");
        }

        foreach (var line in ReadCommandFile(arguments))
        {
            Console.WriteLine(Ansi.Dim("(digger) " + line));
            Execute(line);
            if (_quit)
            {
                return;
            }
        }
    }

    /// <summary>Non-empty lines that are not <c>#</c> comments.</summary>
    internal static List<string> ReadCommandFile(string path)
    {
        var result = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Trim() is { Length: > 0 } trimmed && !trimmed.StartsWith('#'))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    // ---- edit ---------------------------------------------------------------------------

    private void Edit(string arguments)
    {
        string path;
        int line;
        if (arguments.Length > 0)
        {
            var location = ParseLocation(arguments);
            (path, line) = location.Function is { } function
                ? ResolveFunctionInProgram(function) ?? throw new CommandException($"Function '{function}' was not found in the program.")
                : (location.Path!, location.Line);
        }
        else
        {
            (path, line) = CurrentLine() ?? throw new CommandException("There is no current source location; use 'edit <location>'.");
        }

        var editor = Environment.GetEnvironmentVariable("DIGGER_EDITOR") is { Length: > 0 } custom ? custom
            : Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual ? visual
            : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } plain ? plain
            : throw new InvalidOperationException("Set $EDITOR (or $DIGGER_EDITOR) to the editor to open.");

        var command = TerminalCommand.SplitFlags(editor);
        var name = Path.GetFileName(command[0]);
        var lineText = line.ToString(CultureInfo.InvariantCulture);
        var info = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var argument in command.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        // Editors disagree on how to open a file at a line.
        switch (name)
        {
            case "code" or "codium" or "cursor":
                info.ArgumentList.Add("--goto");
                info.ArgumentList.Add($"{path}:{lineText}");
                break;
            case "zed" or "hx" or "helix" or "subl" or "kak":
                info.ArgumentList.Add($"{path}:{lineText}");
                break;
            default: // vi, vim, nvim, nano, emacs, micro, ...
                info.ArgumentList.Add("+" + lineText);
                info.ArgumentList.Add(path);
                break;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start '{editor}'.");
        process.WaitForExit();
        _sourceCache.Remove(path); // show the edited text from now on
    }

    // ---- transcript ---------------------------------------------------------------------

    /// <summary>Reads a line; the transcript gets the prompt and the line, not the editor's redraws.</summary>
    private string? ReadLine(string prompt)
    {
        if (_transcript is not { } transcript)
        {
            return _editor.ReadLine(prompt);
        }

        transcript.Paused = true;
        try
        {
            var line = _editor.ReadLine(prompt);
            transcript.Paused = false;
            transcript.LogInput(prompt + line);
            return line;
        }
        finally
        {
            transcript.Paused = false;
        }
    }

    private void TranscriptCommand(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["-off"])
        {
            if (_transcript is null)
            {
                throw new CommandException("No transcript is being written.");
            }

            StopTranscript();
            return;
        }

        var truncate = parts.Contains("-t");
        var file = parts.Where(static p => p != "-t").ToList();
        if (file.Count != 1)
        {
            throw new CommandException("usage: transcript [-t] <file> | transcript -off");
        }

        StopTranscript();
        _transcript = Transcript.Start(file[0], truncate);
        Console.WriteLine($"Writing a transcript to {Path.GetFullPath(file[0])}");
    }

    private void StopTranscript()
    {
        _transcript?.Dispose();
        _transcript = null;
    }

    /// <summary>Copies everything written to the terminal (and the commands typed) to a file.</summary>
    private sealed class Transcript : IDisposable
    {
        private readonly TextWriter _originalOut;
        private readonly TextWriter _originalError;
        private readonly StreamWriter _file;
        private readonly Lock _fileLock = new();
        private readonly Tee _out;
        private readonly Tee _error;

        private Transcript(StreamWriter file)
        {
            _file = file;
            _originalOut = Console.Out;
            _originalError = Console.Error;
            _out = new Tee(_originalOut, this);
            _error = new Tee(_originalError, this);
            Console.SetOut(TextWriter.Synchronized(_out));
            Console.SetError(TextWriter.Synchronized(_error));
        }

        public static Transcript Start(string path, bool truncate) =>
            new(new StreamWriter(path, append: !truncate, new UTF8Encoding(false)) { AutoFlush = true });

        /// <summary>While the line editor draws the input line, which is logged afterwards by <see cref="LogInput"/>.</summary>
        public bool Paused { get; set; }

        public void LogInput(string line) => Append(line + "\n", force: true);

        private void Append(string? text, bool force = false)
        {
            if (Paused && !force)
            {
                return;
            }

            lock (_fileLock)
            {
                _file.Write(Ansi.Strip(text));
            }
        }

        public void Dispose()
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
            _out.Dispose();
            _error.Dispose();
            lock (_fileLock)
            {
                _file.Dispose();
            }
        }

        private sealed class Tee(TextWriter terminal, Transcript transcript) : TextWriter
        {
            public override Encoding Encoding => terminal.Encoding;

            public override void Write(char value)
            {
                terminal.Write(value);
                transcript.Append(value.ToString());
            }

            public override void Write(string? value)
            {
                terminal.Write(value);
                transcript.Append(value);
            }

            public override void Flush() => terminal.Flush();
        }
    }
}
