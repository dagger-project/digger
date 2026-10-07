using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Digger.Engine;
using Digger.Engine.Inspection;
using Digger.Interop.CorDebug;

namespace Digger.Cli;

internal sealed partial class Repl
{
    private readonly List<Command> _commands;
    private readonly List<string> _displays = [];
    private string? _lastCommand;
    private (string Path, int Next)? _listing;

    private enum Completes
    {
        Nothing,
        Location,
        Expression,
    }

    private sealed record Command(string[] Names, string Group, string Usage, string Summary, Action<string> Run)
    {
        /// <summary>What tab completes in the arguments.</summary>
        public Completes Arguments { get; init; }

        public bool IsList => Names[0] == "list";

        /// <summary>Whether an empty line repeats it (Delve repeats execution and listing commands).</summary>
        public bool Repeats { get; init; }

        /// <summary>Longer help shown by <c>help &lt;command&gt;</c>.</summary>
        public string? Details { get; init; }
    }

    private const string RunningGroup = "Running the program";
    private const string BreakpointGroup = "Manipulating breakpoints";
    private const string DataGroup = "Viewing program variables";
    private const string ThreadGroup = "Listing and switching between threads";
    private const string StackGroup = "Viewing the call stack and selecting frames";
    private const string OtherGroup = "Other commands";

    private List<Command> CreateCommands() =>
    [
        new(["continue", "c"], RunningGroup, "continue [<location>]", "Run until breakpoint or program termination.", Continue)
        {
            Arguments = Completes.Location,
            Repeats = true,
            Details = "With a location, runs until that location is reached (a one-shot breakpoint), e.g. 'continue Program.cs:42'.",
        },
        new(["next", "n"], RunningGroup, "next [<count>]", "Step over to next source line.", args => StepCommand(StepKind.Over, args)) { Repeats = true },
        new(["step", "s"], RunningGroup, "step", "Single step through program.", args => StepCommand(StepKind.In, args)) { Repeats = true },
        new(["stepout", "so"], RunningGroup, "stepout", "Step out of the current function.", args => StepCommand(StepKind.Out, args)) { Repeats = true },
        new(["call"], RunningGroup, "call <expression>", "Run a method call, letting the whole program run while it executes.", Call)
        {
            Arguments = Completes.Expression,
            Details = """
                Like print, but other threads run during the call and there is no time limit, so
                the method may wait on them (locks, tasks). Ctrl-C aborts it. E.g. 'call list.Clear()'.
                Breakpoints hit by other threads during the call are ignored.
                """,
        },
        new(["restart", "r"], RunningGroup, "restart", "Restart the process (breakpoints are kept).", Restart),
        new(["rebuild"], RunningGroup, "rebuild", "Rebuild the project and restart the process (digger debug and test only).", Rebuild),
        new(["exit", "quit", "q"], RunningGroup, "exit", "Exit the debugger (kills a launched program).", _ => Quit(force: false)),

        new(["break", "b"], BreakpointGroup, "break <location> [if <condition>]", "Set a breakpoint.", Break)
        {
            Arguments = Completes.Location,
            Details = """
                Locations:
                  Program.cs:42       line 42 of a file (any unique path suffix works)
                  42                  line 42 of the current file
                  +3, -3              relative to the current line
                  Program.Main        a method (Namespace.Type.Method, Type.Method or Method)
                The condition is a C# expression evaluated each time, e.g. 'break 42 if i == 10'.
                """,
        },
        new(["trace", "t"], BreakpointGroup, "trace [-stack <n>] <location> [if <condition>]", "Set a tracepoint.", Trace)
        {
            Arguments = Completes.Location,
            Details = """
                A tracepoint prints the function and its arguments each time it is reached, then
                lets the program continue. -stack <n> also prints <n> frames of stack.
                Locations are the same as for 'break'.
                """,
        },
        new(["on"], BreakpointGroup, "on <id> <command>", "Execute a command when a breakpoint is hit.", On)
        {
            Details = """
                on <id> print <expression>   print an expression (also locals, args, whatis, stack, display)
                on <id> trace                turn the breakpoint into a tracepoint (print and go on)
                on <id> cond <expression>    set the condition, like 'condition'
                on <id> -clear               remove the commands (a tracepoint stops again)
                """,
        },
        new(["breakpoints", "bp"], BreakpointGroup, "breakpoints", "Print out info for active breakpoints.", _ => ListBreakpoints()),
        new(["clear"], BreakpointGroup, "clear <id>...", "Delete breakpoints.", Clear),
        new(["clearall"], BreakpointGroup, "clearall", "Delete all breakpoints.", _ => ClearAll()),
        new(["condition", "cond"], BreakpointGroup, "condition <id> <expr>", "Set a breakpoint condition.", Condition)
        {
            Details = """
                condition <id> <expr>                 stop only when <expr> is true
                condition -hitcount <id> <op> <n>     stop by hit count; op is ==, >, >=, <, <= or %
                condition -clear <id>                 remove the condition and hit count condition
                """,
        },
        new(["toggle"], BreakpointGroup, "toggle <id>", "Enable or disable a breakpoint.", Toggle),
        new(["catch"], BreakpointGroup, "catch [all | off | <type>... | unhandled on|off]", "Stop when exceptions are thrown.", Catch)
        {
            Details = """
                catch                        show which exceptions stop the program
                catch all                    stop whenever an exception is thrown
                catch <type>...              stop when one of these types (or a derived type) is thrown
                catch off                    stop on thrown exceptions no more (unhandled ones still do)
                catch unhandled on|off       stop on unhandled exceptions (on by default)
                Types are full or simple names (InvalidOperationException, System.IO.IOException) or a
                namespace (System.IO.*); !Type excludes, e.g. 'catch all !OperationCanceledException'.
                With Just My Code, only exceptions thrown in or through your code stop.
                """,
        },

        new(["print", "p"], DataGroup, "print [-x] <expression>", "Evaluate an expression.", Print)
        {
            Arguments = Completes.Expression,
            Details = "Objects are expanded one level. -x prints integers in hexadecimal.",
        },
        new(["locals"], DataGroup, "locals [-v]", "Print local variables and arguments (-v expands objects).", Locals),
        new(["args"], DataGroup, "args [-v]", "Same as locals: arguments are listed with the locals.", Locals),
        new(["whatis"], DataGroup, "whatis <expression>", "Print the type of an expression.", WhatIs) { Arguments = Completes.Expression },
        new(["set"], DataGroup, "set <variable> = <value>", "Change the value of a variable.", Set) { Arguments = Completes.Expression },
        new(["vars"], DataGroup, "vars [-v] [<regex>]", "Print static variables of your code (-v expands objects).", Vars),
        new(["display"], DataGroup, "display [-a <expression>] [-d <number>]", "Print the value of an expression every time the program stops.", Display) { Arguments = Completes.Expression },

        new(["threads"], ThreadGroup, "threads", "Print out info for every thread.", _ => Threads()),
        new(["thread", "tr"], ThreadGroup, "thread <id>", "Switch to the specified thread.", SwitchThread),
        new(["tasks", "goroutines", "grs"], ThreadGroup, "tasks [-a]", "List async methods in flight (-a includes framework code).", Tasks)
        {
            Details = """
                Lists the async methods that have started and not finished: where each one waits
                (or 'running' if it is executing on a thread) and which method awaits it. The
                program's memory is searched, so this can take a moment in a large process.
                Numbers are valid until the program runs again. 'task <id>' switches to one.
                """,
        },
        new(["task", "goroutine", "gr"], ThreadGroup, "task <id>", "Switch to an async method's logical call stack.", SwitchTask),

        new(["stack", "bt"], StackGroup, "stack [<depth>] [-full]", "Print stack trace (-full includes locals).", Stack),
        new(["frame"], StackGroup, "frame <n> [<command>]", "Set the current frame, or run a command in another frame.", Frame),
        new(["up"], StackGroup, "up [<n>]", "Move the current frame up (towards the caller).", args => MoveFrame(+ParseCount(args))),
        new(["down"], StackGroup, "down [<n>]", "Move the current frame down.", args => MoveFrame(-ParseCount(args))),

        new(["list", "ls", "l"], OtherGroup, "list [<location>]", "Show source code.", List) { Repeats = true, Arguments = Completes.Location },
        new(["sources"], OtherGroup, "sources [<regex>]", "Print list of source files.", Sources),
        new(["funcs"], OtherGroup, "funcs [-a] [<regex>]", "Print list of functions (-a includes framework assemblies).", Funcs),
        new(["types"], OtherGroup, "types [-a] [<regex>]", "Print list of types (-a includes framework assemblies).", Types),
        new(["libraries", "modules"], OtherGroup, "libraries", "List loaded assemblies.", _ => Libraries()),
        new(["config"], OtherGroup, "config [-list | -save | <setting> [<value>]]", "Change configuration parameters.", Config)
        {
            Details = """
                config -list                          show every setting
                config <setting> <value>              change a setting, e.g. 'config max-array-values 100'
                config -save                          save the settings to ~/.config/digger/config (run at startup)
                config substitute-path <from> <to>    map a build-time source path prefix to a local one
                config substitute-path <from>         remove a rule ('-clear' removes all)
                config alias <command> <alias>        add an alias ('config alias <alias>' removes it)
                """,
        },
        new(["source"], OtherGroup, "source <file>", "Execute a file containing a list of debugger commands.", Source),
        new(["edit", "ed"], OtherGroup, "edit [<location>]", "Open the current location (or another one) in $EDITOR.", Edit) { Arguments = Completes.Location },
        new(["transcript"], OtherGroup, "transcript [-t] <file> | -off", "Append the session's output to a file (-t truncates it first).", TranscriptCommand),
        new(["help", "h"], OtherGroup, "help [<command>]", "Prints the help message.", Help),
    ];

    /// <summary>Runs one command line. Errors are reported, never thrown.</summary>
    private void Execute(string line)
    {
        line = line.Trim();
        var repeating = line.Length == 0;
        if (repeating)
        {
            if (_lastCommand is null)
            {
                return;
            }

            line = _lastCommand;
        }

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? line : line[..space];
        var arguments = space < 0 ? "" : line[(space + 1)..].Trim();
        var command = FindCommand(name);
        if (command is null)
        {
            Console.WriteLine($"Command failed: command not available: {name}");
            return;
        }

        _lastCommand = command.Repeats ? line : null;
        if (!repeating && !command.IsList)
        {
            _listing = null;
        }

        try
        {
            command.Run(repeating && command.IsList ? "\0more" : arguments);
        }
        catch (Exception ex) when (ex is CommandException or InvalidOperationException or ArgumentException or CorDebugException
            or IOException or FormatException or NotSupportedException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Command failed: {ex.Message}");
        }
    }

    private Command? FindCommand(string name)
    {
        if (_aliases.TryGetValue(name, out var target))
        {
            name = target;
        }

        return _commands.Find(c => c.Names.Contains(name, StringComparer.Ordinal));
    }

    // ---- Running ------------------------------------------------------------------------

    private void Continue(string arguments)
    {
        CliBreakpoint? temporary = null;
        if (arguments.Length > 0)
        {
            temporary = AddBreakpoint(ParseLocation(arguments), condition: null, temporary: true);
        }

        try
        {
            if (_state == State.NotStarted)
            {
                Start(stopAtEntry: false);
            }
            else
            {
                _ = Resume(static session => session.Continue());
            }
        }
        finally
        {
            if (temporary is not null && _breakpoints.Contains(temporary))
            {
                RemoveBreakpoint(temporary);
            }
        }

        ReportStop();
    }

    private void StepCommand(StepKind kind, string arguments)
    {
        if (_state == State.NotStarted && _target.IsTest)
        {
            throw new CommandException("The tests start in the test framework's generated Main; set a breakpoint in a test and use 'continue'.");
        }

        if (_state == State.NotStarted)
        {
            // Like the first 'next' in Delve: start the program and stop at the top of Main.
            Start(stopAtEntry: true);
            ReportStop();
            return;
        }

        if (_threadId < 0)
        {
            throw new CommandException("A task waiting at an await cannot be stepped; switch to a thread with 'thread <id>' first.");
        }

        var count = kind == StepKind.Over ? ParseCount(arguments) : 1;
        StopInfo? stop = null;
        for (var i = 0; i < count; i++)
        {
            var thread = _threadId;
            stop = Resume(session => session.Step(thread, kind));
            if (stop is null || stop.Reason != StopReason.Step)
            {
                break;
            }
        }

        ReportStop();
    }

    private void Restart(string arguments)
    {
        if (IsAttach)
        {
            throw new InvalidOperationException("restart is not supported for attached processes.");
        }

        EndSession(terminate: true);
        Console.WriteLine("Process restarted; breakpoints are kept. Type 'continue' to run.");
    }

    private void Rebuild(string arguments)
    {
        if (_target.Build is not { } build)
        {
            throw new InvalidOperationException("rebuild only works with 'digger debug' and 'digger test'.");
        }

        EndSession(terminate: true);
        _program?.Dispose();
        _program = null;
        if (ProjectBuilder.Build(build)?.TargetPath is not { } program)
        {
            throw new InvalidOperationException("The build failed; fix it and run 'rebuild' again.");
        }

        _target = _target with { Program = program };
        OpenProgram();
        Console.WriteLine("Process rebuilt; breakpoints are kept. Type 'continue' to run.");
    }

    // ---- Stop reporting -----------------------------------------------------------------

    /// <summary>Prints where the program stopped, Delve style, followed by the source around it.</summary>
    private void ReportStop()
    {
        if (_state != State.Stopped)
        {
            return;
        }

        var stop = _lastStop;
        var frames = Frames();
        if (frames.Count == 0)
        {
            Console.WriteLine($"> Thread {_threadId} stopped with no managed code on its stack.");
            return;
        }

        var tag = stop?.Reason switch
        {
            StopReason.Breakpoint or StopReason.FunctionBreakpoint when stop.HitBreakpointIds.Count > 0 => HitTag(stop),
            StopReason.Exception => Ansi.Red("[exception] "),
            StopReason.Pause => "[paused] ",
            _ => "",
        };

        Console.WriteLine($"> {tag}{DescribeFrame(frames[0])}{HitCounts(stop)}");
        if (stop?.Reason == StopReason.Exception && _session is { } session
            && Engine(() => session.GetExceptionInfo(_threadId)) is { } exception)
        {
            Console.WriteLine(Ansi.Red($"{exception.TypeName}: {exception.Message}"));
            for (var inner = exception.Inner; inner is not null; inner = inner.Inner)
            {
                Console.WriteLine(Ansi.Red($" ---> {inner.TypeName}: {inner.Message}"));
            }
        }

        // Paused in framework code: show the nearest frame that has source.
        if (frames[0].SourcePath is null && frames.FindIndex(f => f.SourcePath is not null) is > 0 and var index)
        {
            _frameIndex = index;
            Console.WriteLine($"Frame {index}: {DescribeFrame(frames[index])}");
        }

        PrintSourceAround(CurrentFrame());
        PrintDisplays();
        if (stop is not null)
        {
            RunOnCommands(HitBreakpoints(stop));
        }
    }

    private string HitTag(StopInfo stop)
    {
        var breakpoints = HitBreakpoints(stop).FindAll(b => !b.Temporary);
        return breakpoints.Count == 0 ? "" : $"[Breakpoint {string.Join(", ", breakpoints.Select(b => b.Id))}] ";
    }

    private string HitCounts(StopInfo? stop)
    {
        if (stop is null)
        {
            return "";
        }

        var breakpoints = HitBreakpoints(stop).FindAll(b => !b.Temporary);
        return breakpoints.Count == 1 ? Ansi.Dim($" (hits: {breakpoints[0].Hits})") : "";
    }

    private string DescribeFrame(FrameView frame)
    {
        var name = FunctionName(frame.Name);
        if (frame.SourcePath is { } path)
        {
            _ = _knownSources.Add(path);
            return $"{Ansi.Bold(name)} {DisplayPath(path)}:{frame.Line}";
        }

        return $"{Ansi.Bold(name)} {Ansi.Dim("(no source)")}";
    }

    private static string FunctionName(string name) => name.Contains('(', StringComparison.Ordinal) ? name : name + "()";

    /// <summary>Paths under the working directory are shown relative to it, like Delve.</summary>
    private static string DisplayPath(string path)
    {
        var cwd = Environment.CurrentDirectory.TrimEnd('/') + "/";
        return path.StartsWith(cwd, StringComparison.Ordinal) ? "./" + path[cwd.Length..] : path;
    }

    // ---- Source listing -----------------------------------------------------------------

    private readonly Dictionary<string, string[]?> _sourceCache = new(StringComparer.Ordinal);

    private string[]? ReadSource(string path)
    {
        if (!_sourceCache.TryGetValue(path, out var lines))
        {
            try
            {
                lines = File.Exists(path) ? File.ReadAllLines(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines = null;
            }

            _sourceCache[path] = lines;
        }

        return lines;
    }

    private void PrintSourceAround(FrameView? frame)
    {
        if (frame?.SourcePath is { } path)
        {
            PrintSource(path, frame.Line - _sourceListLineCount, frame.Line + _sourceListLineCount, current: frame.Line);
        }
    }

    private void PrintSource(string path, int first, int last, int current)
    {
        if (ReadSource(path) is not { } lines)
        {
            Console.WriteLine(Ansi.Dim($"(source not available: {path})"));
            return;
        }

        first = Math.Max(1, first);
        last = Math.Min(lines.Length, last);
        var width = last.ToString(CultureInfo.InvariantCulture).Length;
        var breakpointLines = _breakpoints
            .Where(b => b.Path == path && b.Enabled && !b.Temporary)
            .Select(b => b.BoundLine ?? b.Line)
            .ToHashSet();

        var builder = new StringBuilder();
        for (var line = first; line <= last; line++)
        {
            var number = line.ToString(CultureInfo.InvariantCulture).PadLeft(width);
            var marker = line == current ? Ansi.Green("=>") : "  ";
            var dot = breakpointLines.Contains(line) ? Ansi.Red("●") : " ";
            var text = lines[line - 1].Replace("\t", "    ", StringComparison.Ordinal);
            _ = builder.Append(marker).Append(dot).Append(' ')
                .Append(line == current ? Ansi.Bold(number) : Ansi.Dim(number))
                .Append(":  ")
                .Append(line == current ? Ansi.Bold(text) : text)
                .Append('\n');
        }

        Console.Write(builder.ToString());
        _listing = (path, last + 1);
    }

    private void List(string arguments)
    {
        if (arguments == "\0more")
        {
            if (_listing is { } listing)
            {
                PrintSource(listing.Path, listing.Next, listing.Next + (2 * _sourceListLineCount), current: CurrentLineIfStopped(listing.Path));
            }

            return;
        }

        if (arguments.Length == 0)
        {
            var frame = _state == State.Stopped ? CurrentFrame() : null;
            if (frame?.SourcePath is null)
            {
                throw new InvalidOperationException("There is no current source location; use 'list <location>'.");
            }

            PrintSourceAround(frame);
            return;
        }

        var location = ParseLocation(arguments);
        var (path, line) = location.Function is { } function
            ? ResolveFunctionInProgram(function) ?? throw new CommandException($"Function '{function}' was not found in the program.")
            : (location.Path!, location.Line);
        PrintSource(path, line - _sourceListLineCount, line + _sourceListLineCount,
            current: CurrentLineIfStopped(path) is var here && Math.Abs(here - line) <= _sourceListLineCount ? here : 0);
    }

    private int CurrentLineIfStopped(string path) => CurrentLine() is { } current && current.Path == path ? current.Line : 0;

    private void Funcs(string arguments) => PrintCatalog(arguments, static (session, filter, all) => session.FindFunctions(filter, !all), static (metadata, type) =>
        metadata.GetMethods(type).Where(static m => !m.Name.Contains('<', StringComparison.Ordinal)).Select(m => metadata.GetTypeName(type) + "." + m.Name));

    private void Types(string arguments) => PrintCatalog(arguments, static (session, filter, all) => session.FindTypes(filter, !all), static (metadata, type) =>
        [metadata.GetTypeName(type)]);

    /// <summary>
    /// funcs / types: from every loaded module once the program runs, from the program's own
    /// assembly before that.
    /// </summary>
    private void PrintCatalog(
        string arguments,
        Func<DebugSession, Func<string, bool>, bool, List<string>> fromSession,
        Func<Digger.Engine.Symbols.ModuleMetadata, uint, IEnumerable<string>> fromProgram)
    {
        var (all, filter) = ParseListFilter(arguments, "-a");
        List<string> names;
        if (_session is { } session && _state is State.Stopped or State.Running)
        {
            names = Engine(() => fromSession(session, filter, all));
        }
        else
        {
            var program = _program ?? throw new InvalidOperationException("The program is not running yet and its assembly could not be read.");
            names = [.. program.GetTypeTokens().SelectMany(type => fromProgram(program, type)).Where(filter).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        foreach (var name in names)
        {
            Console.WriteLine(name);
        }
    }

    private void Vars(string arguments)
    {
        var (verbose, filter) = ParseListFilter(arguments, "-v");
        var session = RequireStopped();
        foreach (var variable in Engine(() => session.GetStaticVariables(filter, hex: false)))
        {
            Console.WriteLine($"{variable.Name} = {FormatValue(variable)}");
            if (verbose)
            {
                PrintChildren(session, variable, "  ", hex: false);
            }
        }
    }

    /// <summary>Parses <c>[flag] [regex]</c>.</summary>
    private static (bool Flag, Func<string, bool> Filter) ParseListFilter(string arguments, string flag)
    {
        var hasFlag = arguments == flag || arguments.StartsWith(flag + " ", StringComparison.Ordinal);
        var pattern = (hasFlag ? arguments[flag.Length..] : arguments).Trim();
        if (pattern.Length == 0)
        {
            return (hasFlag, static _ => true);
        }

        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return (hasFlag, regex.IsMatch);
    }

    private void Sources(string arguments)
    {
        var filter = arguments.Length == 0 ? null : new Regex(arguments, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var path in KnownSources().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (filter is null || filter.IsMatch(path))
            {
                Console.WriteLine(path);
            }
        }
    }

    private void Libraries()
    {
        var session = RequireSession();
        var modules = Engine(session.GetModules);
        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            var flags = module.IsUserCode ? "user" : module.HasSymbols ? "symbols" : "";
            Console.WriteLine($"{i,3}  {module.Path} {Ansi.Dim(flags)}");
        }
    }

    // ---- Breakpoints --------------------------------------------------------------------

    private void Break(string arguments)
    {
        var breakpoint = AddBreakpoint(arguments, "break");
        Console.WriteLine($"Breakpoint {breakpoint.Id} set at {DescribeBreakpoint(breakpoint)}");
    }

    /// <summary>Parses <c>[location] [if condition]</c> and adds the breakpoint.</summary>
    private CliBreakpoint AddBreakpoint(string arguments, string command)
    {
        string? condition = null;
        var ifIndex = arguments.IndexOf(" if ", StringComparison.Ordinal);
        if (ifIndex >= 0)
        {
            condition = arguments[(ifIndex + 4)..].Trim();
            arguments = arguments[..ifIndex];
        }

        if (arguments.Trim().Length == 0)
        {
            // Delve's bare 'break' sets a breakpoint at the current line.
            var (path, line) = CurrentLine() ?? throw new CommandException($"{command} needs a location, e.g. '{command} Program.cs:12'.");
            arguments = $"{path}:{line}";
        }

        return AddBreakpoint(ParseLocation(arguments), condition, temporary: false);
    }

    private void Trace(string arguments)
    {
        var stack = 0;
        if (arguments.StartsWith("-stack ", StringComparison.Ordinal))
        {
            var parts = arguments.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            stack = parts.Length > 1 ? ParseCount(parts[1]) : 0;
            arguments = parts.Length > 2 ? parts[2] : "";
        }

        var breakpoint = AddBreakpoint(arguments, "trace");
        breakpoint.Tracepoint = true;
        breakpoint.TraceStack = stack;
        Console.WriteLine($"Tracepoint {breakpoint.Id} set at {DescribeBreakpoint(breakpoint)}");
    }

    /// <summary>Commands <c>on</c> accepts: the ones that only look at the stopped program.</summary>
    private static readonly HashSet<string> OnCommandNames = new(["print", "locals", "args", "whatis", "stack", "display"], StringComparer.Ordinal);

    private void On(string arguments)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new CommandException("usage: on <id> <command>");
        }

        var breakpoint = FindBreakpoint(parts[0]);
        var command = parts[1];
        var name = command.Split(' ', 2)[0];
        switch (name)
        {
            case "-clear":
                breakpoint.OnCommands.Clear();
                breakpoint.Tracepoint = false;
                return;
            case "trace":
                breakpoint.Tracepoint = true;
                return;
            case "cond" or "condition":
                Condition($"{breakpoint.Id} {command[name.Length..].Trim()}");
                return;
        }

        if (FindCommand(name) is not { } found || !OnCommandNames.Contains(found.Names[0]))
        {
            throw new CommandException($"'{name}' cannot run on a breakpoint; use print, locals, args, whatis, stack, display, trace or cond.");
        }

        breakpoint.OnCommands.Add(command);
    }

    private void ListBreakpoints()
    {
        var any = false;
        foreach (var breakpoint in _breakpoints.Where(b => !b.Temporary))
        {
            any = true;
            var state = breakpoint.Enabled ? "" : Ansi.Yellow(" (disabled)");
            var kind = breakpoint.Tracepoint ? "Tracepoint" : "Breakpoint";
            Console.WriteLine($"{kind} {breakpoint.Id}{state} at {DescribeBreakpoint(breakpoint)} {Ansi.Dim($"(hits: {breakpoint.Hits})")}");
            if (breakpoint.Condition is { } condition)
            {
                Console.WriteLine($"\tcond {condition}");
            }

            if (breakpoint.HitCondition is { } hitCondition)
            {
                Console.WriteLine($"\tcond -hitcount {hitCondition}");
            }

            foreach (var command in breakpoint.OnCommands)
            {
                Console.WriteLine($"\t{command}");
            }
        }

        if (!any)
        {
            Console.WriteLine("No breakpoints.");
        }
    }

    private void Clear(string arguments)
    {
        if (arguments.Length == 0)
        {
            throw new CommandException("clear needs a breakpoint number; 'clearall' deletes all of them.");
        }

        foreach (var id in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var breakpoint = FindBreakpoint(id);
            RemoveBreakpoint(breakpoint);
            Console.WriteLine($"Breakpoint {breakpoint.Id} cleared at {DescribeBreakpoint(breakpoint)}");
        }
    }

    private void ClearAll()
    {
        foreach (var breakpoint in _breakpoints.Where(b => !b.Temporary).ToList())
        {
            RemoveBreakpoint(breakpoint);
            Console.WriteLine($"Breakpoint {breakpoint.Id} cleared at {DescribeBreakpoint(breakpoint)}");
        }
    }

    private void Toggle(string arguments)
    {
        var breakpoint = FindBreakpoint(arguments);
        breakpoint.Enabled = !breakpoint.Enabled;
        Sync(breakpoint);
        Console.WriteLine($"Breakpoint {breakpoint.Id} {(breakpoint.Enabled ? "enabled" : "disabled")}");
    }

    private void Condition(string arguments)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new CommandException("usage: condition <id> <expr>");
        }

        switch (parts[0])
        {
            case "-clear" when parts.Length == 2:
                var cleared = FindBreakpoint(parts[1]);
                cleared.Condition = null;
                cleared.HitCondition = null;
                Sync(cleared);
                return;
            case "-hitcount" when parts.Length == 2:
                var hitParts = parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (hitParts.Length != 2)
                {
                    throw new CommandException("usage: condition -hitcount <id> <op> <n>");
                }

                var counted = FindBreakpoint(hitParts[0]);
                counted.HitCondition = hitParts[1];
                Sync(counted);
                return;
            default:
                if (parts.Length != 2)
                {
                    throw new CommandException("usage: condition <id> <expr>");
                }

                var breakpoint = FindBreakpoint(parts[0]);
                breakpoint.Condition = parts[1];
                Sync(breakpoint);
                return;
        }
    }

    // ---- Data ---------------------------------------------------------------------------

    private int? CurrentFrameId() => CurrentFrame()?.Id;

    private void Print(string arguments)
    {
        var hex = false;
        if (arguments.StartsWith("-x ", StringComparison.Ordinal))
        {
            hex = true;
            arguments = arguments[3..].Trim();
        }

        if (arguments.Length == 0)
        {
            throw new CommandException("print needs an expression.");
        }

        var session = RequireStopped();
        var frameId = CurrentFrameId();
        var view = Engine(() => session.Evaluate(arguments, frameId, hex));
        Console.WriteLine(FormatValue(view));
        PrintChildren(session, view, "  ", hex);
    }

    private void Call(string arguments)
    {
        if (arguments.Length == 0)
        {
            throw new CommandException("call needs an expression, e.g. 'call list.Clear()'.");
        }

        var session = RequireStopped();
        var frameId = CurrentFrameId();
        _calling = true;
        VariableView view;
        try
        {
            view = Engine(() => session.Call(arguments, frameId, hex: false));
        }
        finally
        {
            _calling = false;
            _frames = null; // the program ran: frames must be read again
        }

        if (view.Type == "void")
        {
            return; // like Delve: a call without a result prints nothing
        }

        Console.WriteLine(FormatValue(view));
        PrintChildren(session, view, "  ", hex: false);
    }

    private void WhatIs(string arguments)
    {
        var session = RequireStopped();
        var frameId = CurrentFrameId();
        var view = Engine(() => session.Evaluate(arguments, frameId, hex: false));
        Console.WriteLine(view.Type ?? "<unknown>");
    }

    private void Locals(string arguments)
    {
        var verbose = arguments.Trim() == "-v";
        var session = RequireStopped();
        var frame = CurrentFrame() ?? throw new InvalidOperationException("No frame selected.");
        PrintLocals(session, frame, "", verbose);
    }

    private void PrintLocals(DebugSession session, FrameView frame, string indent, bool verbose)
    {
        var variables = Engine(() =>
        {
            var scopes = session.GetScopes(frame.Id);
            return scopes.Count == 0 ? [] : session.GetVariables(scopes[0].Reference, 0, 0, hex: false);
        });

        if (variables.Count == 0)
        {
            Console.WriteLine($"{indent}(no locals)");
            return;
        }

        foreach (var variable in variables)
        {
            Console.WriteLine($"{indent}{variable.Name} = {FormatValue(variable)}");
            if (verbose)
            {
                PrintChildren(session, variable, indent + "  ", hex: false);
            }
        }
    }

    /// <summary>One level of members or elements under an object.</summary>
    private void PrintChildren(DebugSession session, VariableView parent, string indent, bool hex)
    {
        if (parent.Reference == 0)
        {
            return;
        }

        var count = parent.IndexedCount > 0 ? Math.Min(parent.IndexedCount, _maxArrayValues) : 0;
        var children = Engine(() => session.GetVariables(parent.Reference, 0, count, hex));
        foreach (var child in children)
        {
            if (child.Kind == VariableKind.Class || child.Name == "Raw View")
            {
                continue; // static members and the raw view are noise in a terminal
            }

            Console.WriteLine($"{indent}{child.Name}: {FormatValue(child)}");
        }

        if (parent.IndexedCount > _maxArrayValues)
        {
            Console.WriteLine($"{indent}{Ansi.Dim($"... +{parent.IndexedCount - _maxArrayValues} more")}");
        }
    }

    private static string FormatValue(VariableView view)
    {
        var type = view.Type is { Length: > 0 } t && view.Reference != 0 && !view.Value.Contains(t, StringComparison.Ordinal)
            ? Ansi.Dim($" ({t})")
            : "";
        return view.Value + type;
    }

    private void Set(string arguments)
    {
        var equals = arguments.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || (equals + 1 < arguments.Length && arguments[equals + 1] == '='))
        {
            throw new CommandException("usage: set <variable> = <value>");
        }

        var target = arguments[..equals].Trim();
        var value = arguments[(equals + 1)..].Trim();
        var session = RequireStopped();
        var frame = CurrentFrame() ?? throw new InvalidOperationException("No frame selected.");

        // a = 1 → the frame's locals; a.b = 1 → member b of a; a[0] = 1 → element [0] of a.
        var (parent, name) = SplitAssignmentTarget(target);
        var result = Engine(() =>
        {
            var reference = parent is null
                ? session.GetScopes(frame.Id)[0].Reference
                : session.Evaluate(parent, frame.Id, hex: false).Reference;
            if (reference == 0)
            {
                throw new InvalidOperationException($"'{parent}' has no members to assign.");
            }

            return session.SetVariable(reference, name, value, hex: false);
        });
        Console.WriteLine($"{target} = {FormatValue(result)}");
    }

    private static (string? Parent, string Name) SplitAssignmentTarget(string target)
    {
        if (target.EndsWith(']'))
        {
            var open = target.LastIndexOf('[');
            if (open > 0)
            {
                return (target[..open], target[open..]);
            }
        }

        var dot = target.LastIndexOf('.');
        return dot > 0 ? (target[..dot], target[(dot + 1)..]) : (null, target);
    }

    private void Display(string arguments)
    {
        if (arguments.StartsWith("-a ", StringComparison.Ordinal))
        {
            _displays.Add(arguments[3..].Trim());
            if (_state == State.Stopped)
            {
                PrintDisplays();
            }

            return;
        }

        if (arguments.StartsWith("-d ", StringComparison.Ordinal))
        {
            var index = int.Parse(arguments[3..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            if (index < 0 || index >= _displays.Count)
            {
                throw new CommandException($"There is no display {index}.");
            }

            _displays.RemoveAt(index);
            return;
        }

        if (arguments.Length > 0)
        {
            throw new CommandException("usage: display [-a <expression>] [-d <number>]");
        }

        if (_state == State.Stopped)
        {
            PrintDisplays();
        }
        else
        {
            for (var i = 0; i < _displays.Count; i++)
            {
                Console.WriteLine($"{i}: {_displays[i]}");
            }
        }
    }

    private void PrintDisplays()
    {
        if (_displays.Count == 0 || _session is not { } session)
        {
            return;
        }

        var frameId = CurrentFrameId();
        for (var i = 0; i < _displays.Count; i++)
        {
            var expression = _displays[i];
            try
            {
                var view = Engine(() => session.Evaluate(expression, frameId, hex: false));
                Console.WriteLine($"{i}: {expression} = {FormatValue(view)}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or CorDebugException)
            {
                Console.WriteLine($"{i}: {expression} = {Ansi.Dim(ex.Message)}");
            }
        }
    }

    // ---- Threads and frames -------------------------------------------------------------

    private void Threads()
    {
        var session = RequireStopped();
        var threads = Engine(session.GetThreads);
        foreach (var thread in threads)
        {
            var (frames, _) = Engine(() => session.GetStackTrace(thread.Id, 0, 0));
            var top = frames.Find(f => !f.IsLabel && f.SourcePath is not null) ?? frames.Find(f => !f.IsLabel);
            var where = top is null ? Ansi.Dim("(no managed code)") : DescribeFrame(top);
            var marker = thread.Id == _threadId ? "*" : " ";
            Console.WriteLine($"{marker} Thread {thread.Id} {Ansi.Cyan(thread.Name)} at {where}");
        }
    }

    private void Tasks(string arguments)
    {
        var all = arguments.Trim() == "-a";
        var session = RequireStopped();
        var tasks = Engine(() => session.GetAsyncTasks(userCodeOnly: !all));
        foreach (var task in tasks)
        {
            var marker = -task.Id == _threadId ? "*" : " ";
            var name = task.IsUserCode ? Ansi.Bold(FunctionName(task.Name)) : FunctionName(task.Name);
            var where = task.SourcePath is { } path ? $" {DisplayPath(path)}:{task.Line}" : "";
            var state = !task.IsRunning ? Ansi.Dim(" [awaiting]")
                : task.ThreadId != 0 ? Ansi.Green($" [running on thread {task.ThreadId}]")
                : Ansi.Green(" [running]");
            var awaitedBy = task.AwaitedBy is { } caller ? Ansi.Dim($" ← {FunctionName(caller)}") : "";
            Console.WriteLine($"{marker} Task {task.Id} - {name}{where}{state}{awaitedBy}");
        }

        Console.WriteLine($"[{tasks.Count} task{(tasks.Count == 1 ? "" : "s")}{(all ? "" : "; 'tasks -a' includes framework code")}]");
    }

    private void SwitchTask(string arguments)
    {
        var session = RequireStopped();
        if (!int.TryParse(arguments.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw new CommandException("usage: task <id> ('tasks' lists them)");
        }

        if (!Engine(() => session.GetAsyncTasks(userCodeOnly: false)).Exists(t => t.Id == id))
        {
            throw new CommandException($"There is no task {id}.");
        }

        _threadId = -id; // the engine serves a task's logical stack as thread -id
        _frameIndex = 0;
        _frames = null;
        Console.WriteLine($"Switched to task {id}");
        ReportFrame();
    }

    private void SwitchThread(string arguments)
    {
        var session = RequireStopped();
        var id = int.TryParse(arguments.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new CommandException("usage: thread <id>");
        if (!Engine(session.GetThreads).Exists(t => t.Id == id))
        {
            throw new CommandException($"There is no thread {id}.");
        }

        _threadId = id;
        _frameIndex = 0;
        _frames = null;
        Console.WriteLine($"Switched to thread {id}");
        ReportFrame();
    }

    private void Stack(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var full = parts.Contains("-full");
        var depth = parts.Where(p => p != "-full").Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture)).FirstOrDefault(50);
        var session = RequireStopped();
        var (frames, _) = Engine(() => session.GetStackTrace(_threadId, 0, 0));
        var index = 0;
        foreach (var frame in frames)
        {
            if (index > depth)
            {
                Console.WriteLine(Ansi.Dim($"... (use 'stack {frames.Count}' to see more)"));
                break;
            }

            if (frame.IsLabel)
            {
                Console.WriteLine(Ansi.Dim($"   --- {frame.Name.Trim('[', ']')} ---"));
                continue;
            }

            var marker = index == _frameIndex ? ">" : " ";
            var name = frame.IsUserCode && frame.SourcePath is not null ? Ansi.Bold(FunctionName(frame.Name)) : FunctionName(frame.Name);
            Console.WriteLine($"{marker}{index,3}  {name}");
            if (frame.SourcePath is { } path)
            {
                _ = _knownSources.Add(path);
                Console.WriteLine($"       at {DisplayPath(path)}:{frame.Line}");
            }

            if (full)
            {
                PrintLocals(session, frame, "           ", verbose: false);
            }

            index++;
        }
    }

    private void Frame(string arguments)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new CommandException("usage: frame <n> [<command>]");
        }

        if (index >= Frames().Count)
        {
            throw new CommandException($"Frame {index} does not exist (the stack has {Frames().Count} frames).");
        }

        if (parts.Length == 1)
        {
            _frameIndex = index;
            ReportFrame();
            return;
        }

        var saved = _frameIndex;
        _frameIndex = index;
        try
        {
            Execute(parts[1]);
        }
        finally
        {
            _frameIndex = saved;
        }
    }

    private void MoveFrame(int delta)
    {
        var target = _frameIndex + delta;
        var count = Frames().Count;
        if (target < 0 || target >= count)
        {
            throw new InvalidOperationException(delta > 0 ? "Already at the outermost frame." : "Already at the innermost frame.");
        }

        _frameIndex = target;
        ReportFrame();
    }

    private void ReportFrame()
    {
        if (CurrentFrame() is not { } frame)
        {
            Console.WriteLine("(no managed frames)");
            return;
        }

        Console.WriteLine($"> {Ansi.Dim($"Frame {_frameIndex}:")} {DescribeFrame(frame)}");
        PrintSourceAround(frame);
    }

    private static int ParseCount(string arguments) =>
        arguments.Trim().Length == 0 ? 1
        : int.TryParse(arguments.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 ? count
        : throw new CommandException($"'{arguments}' is not a positive number.");

    // ---- Help ---------------------------------------------------------------------------

    private void Help(string arguments)
    {
        if (arguments.Length > 0)
        {
            var command = FindCommand(arguments) ?? throw new CommandException($"Unknown command '{arguments}'.");
            Console.WriteLine(command.Summary);
            Console.WriteLine();
            Console.WriteLine($"\t{command.Usage}");
            if (command.Names.Length > 1)
            {
                Console.WriteLine($"\taliases: {string.Join(" ", command.Names.Skip(1))}");
            }

            if (command.Details is { } details)
            {
                Console.WriteLine();
                Console.WriteLine(details);
            }

            return;
        }

        Console.WriteLine("The following commands are available:");
        foreach (var group in _commands.GroupBy(c => c.Group, StringComparer.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine($"{group.Key}:");
            foreach (var command in group)
            {
                var names = command.Names.Length > 1 ? $"{command.Names[0]} ({string.Join(", ", command.Names.Skip(1))})" : command.Names[0];
                Console.WriteLine($"    {names,-22} {command.Summary}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Type help followed by a command for full documentation.");
        Console.WriteLine("Ctrl-C pauses the running program; an empty line repeats the last command.");
    }

    // ---- Completion ---------------------------------------------------------------------

    private Completion Complete(string line, int cursor)
    {
        var start = cursor;
        while (start > 0 && line[start - 1] != ' ')
        {
            start--;
        }

        var word = line[start..cursor];
        var head = line[..start].TrimStart();
        if (head.Length == 0)
        {
            return new Completion(start, [.. _commands.SelectMany(c => c.Names).Where(n => n.StartsWith(word, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
        }

        var commandName = head.Split(' ', 2)[0];
        var command = FindCommand(commandName);
        if (command is null)
        {
            return new Completion(start, []);
        }

        if (command.Arguments == Completes.Location)
        {
            var files = KnownSources()
                .Select(Path.GetFileName)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Where(name => name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .Select(name => name + ":")
                .Order(StringComparer.Ordinal)
                .ToList();
            return new Completion(start, files);
        }

        if (command.Arguments == Completes.Expression)
        {
            return CompleteExpression(line, cursor);
        }

        return new Completion(start, []);
    }

    private Completion CompleteExpression(string line, int cursor)
    {
        if (_state != State.Stopped || _session is not { } session)
        {
            return new Completion(cursor, []);
        }

        try
        {
            var frameId = CurrentFrameId();
            var items = Engine(() => session.GetCompletions(frameId, line, cursor));
            if (items.Count == 0)
            {
                return new Completion(cursor, []);
            }

            return new Completion(items[0].Start, [.. items.Select(i => i.Label).Distinct(StringComparer.Ordinal)]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or CorDebugException)
        {
            return new Completion(cursor, []);
        }
    }
}
