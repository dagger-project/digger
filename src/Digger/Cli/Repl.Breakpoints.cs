using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Digger.Engine;
using Digger.Engine.Breakpoints;
using Digger.Engine.Symbols;

namespace Digger.Cli;

internal sealed partial class Repl
{
    private readonly List<CliBreakpoint> _breakpoints = [];
    private readonly Dictionary<int, CliBreakpoint> _byEngineId = [];
    private readonly HashSet<string> _knownSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _moduleSources = new(StringComparer.Ordinal);
    private int _nextBreakpointId = 1;

    /// <summary>A breakpoint as the user sees it. Ids are stable across restarts.</summary>
    private sealed class CliBreakpoint
    {
        public required int Id { get; init; }

        public string? Path { get; init; }

        public int Line { get; init; }

        public string? Function { get; init; }

        public string? Condition { get; set; }

        public string? HitCondition { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>One-shot breakpoint for <c>continue &lt;location&gt;</c>.</summary>
        public bool Temporary { get; init; }

        public int Hits { get; set; }

        /// <summary>Where it actually binds, if known.</summary>
        public int? BoundLine { get; set; }

        /// <summary>For function breakpoints: the file of the function, if known.</summary>
        public string? FunctionPath { get; set; }

        /// <summary>Method containing the location, if known.</summary>
        public string? Method { get; set; }

        public bool Verified { get; set; }
    }

    /// <summary>A parsed location: a source line or a function name.</summary>
    private readonly record struct Location(string? Path, int Line, string? Function);

    // ---- Location parsing ---------------------------------------------------------------

    /// <summary>
    /// Parses Delve-style locations: <c>file.cs:42</c>, <c>42</c> (current file), <c>+3</c> /
    /// <c>-3</c> (relative to the current line) and function names (<c>Program.Main</c>).
    /// </summary>
    private Location ParseLocation(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0)
        {
            throw new CommandException("A location is required, e.g. Program.cs:12, 12 or Program.Main.");
        }

        if (spec[0] is '+' or '-' && int.TryParse(spec.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            var (path, line) = CurrentLine() ?? throw new CommandException("There is no current line to offset from.");
            return new Location(path, spec[0] == '+' ? line + offset : line - offset, null);
        }

        if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
        {
            var path = CurrentLine()?.Path ?? DefaultSourceFile()
                ?? throw new CommandException("There is no current file; use file:line.");
            return new Location(path, bare, null);
        }

        var colon = spec.LastIndexOf(':');
        if (colon > 0 && int.TryParse(spec.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var fileLine))
        {
            return new Location(ResolveSourceFile(spec[..colon]), fileLine, null);
        }

        if (spec.AsSpan().IndexOfAny(" \t:") >= 0)
        {
            throw new CommandException($"'{spec}' is not a location; use file:line, a line number or a method name.");
        }

        return new Location(null, 0, spec);
    }

    /// <summary>The selected frame's source line, if stopped in code with source.</summary>
    private (string Path, int Line)? CurrentLine()
    {
        if (_state != State.Stopped || CurrentFrame() is not { SourcePath: { } path } frame)
        {
            return null;
        }

        return (path, frame.Line);
    }

    /// <summary>Before the program runs, a bare line number refers to the file with Main.</summary>
    private string? DefaultSourceFile()
    {
        if (_program?.Symbols is not { } symbols || _program.EntryPointToken == 0)
        {
            return null;
        }

        var entry = _program.EntryPointToken;
        foreach (var token in (ReadOnlySpan<uint>)[entry, .. _program.FindMethodsByName("Main")])
        {
            var method = symbols.GetMoveNextMethod(token) ?? token;
            if (symbols.GetVisiblePoints(method) is [var first, ..])
            {
                return first.Location.Document;
            }
        }

        return null;
    }

    /// <summary>Turns what the user typed into a full path: on disk, or a unique match among known sources.</summary>
    private string ResolveSourceFile(string spec)
    {
        if (File.Exists(spec))
        {
            return Path.GetFullPath(spec);
        }

        var wanted = spec.Replace('\\', '/');
        var matches = KnownSources()
            .Where(path => path.Replace('\\', '/') is var normalized
                && (normalized == wanted || normalized.EndsWith("/" + wanted.TrimStart('.', '/'), StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CommandException($"No source file matches '{spec}'."),
            _ => throw new CommandException($"'{spec}' is ambiguous:\n  " + string.Join("\n  ", matches.Select(DisplayPath))),
        };
    }

    /// <summary>Source files of the program plus every file seen in a stack so far.</summary>
    private IEnumerable<string> KnownSources()
    {
        if (_program?.Symbols is { } symbols)
        {
            foreach (var path in symbols.GetDocumentPaths())
            {
                yield return path;
            }
        }

        foreach (var path in _knownSources)
        {
            yield return path;
        }

        foreach (var path in ModuleSources())
        {
            yield return path;
        }
    }

    /// <summary>Source files of every loaded user assembly (other projects, or all of them after attach).</summary>
    private List<string> ModuleSources()
    {
        var result = new List<string>();
        if (_session is not { } session || _state is State.NotStarted or State.Exited)
        {
            return result;
        }

        foreach (var module in Engine(session.GetModules))
        {
            if (!module.IsUserCode || !module.HasSymbols)
            {
                continue;
            }

            if (!_moduleSources.TryGetValue(module.Path, out var documents))
            {
                using var metadata = ModuleMetadata.TryOpen(module.Path, loadSymbols: true);
                documents = metadata?.Symbols?.GetDocumentPaths().ToArray() ?? [];
                _moduleSources[module.Path] = documents;
            }

            result.AddRange(documents);
        }

        return result;
    }

    /// <summary>Uses the program's PDB to say where a location lands before anything is loaded.</summary>
    private (int Line, string? Method)? ResolveInProgram(string path, int line)
    {
        if (_program?.Symbols?.ResolveBreakpoint(path, line) is not { } resolution)
        {
            return null;
        }

        var method = resolution.Targets.Count > 0 ? _program.GetMethodDisplayName(resolution.Targets[0].MethodToken) : null;
        return (resolution.Location.StartLine, method);
    }

    /// <summary>First line of a function in the program, for <c>list Program.Main</c>.</summary>
    private (string Path, int Line)? ResolveFunctionInProgram(string name)
    {
        if (_program?.Symbols is not { } symbols)
        {
            return null;
        }

        foreach (var token in _program.FindMethodsByName(name))
        {
            var method = symbols.GetMoveNextMethod(token) ?? token;
            if (symbols.GetVisiblePoints(method) is [var first, ..])
            {
                return (first.Location.Document, first.Location.StartLine);
            }
        }

        return null;
    }

    /// <summary>
    /// Function breakpoints match in every module, so 'Add' would also stop in List.Add. A
    /// name the program defines is pinned to its declaring type; ambiguous names are refused.
    /// </summary>
    private string QualifyFunction(string name)
    {
        if (_program is null)
        {
            return name;
        }

        var qualified = _program.FindMethodsByName(name)
            .Select(token => _program.GetTypeName(_program.GetDeclaringType(token)) + "." + _program.GetMethodName(token))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return qualified.Count switch
        {
            0 => name,
            1 => qualified[0],
            _ => throw new CommandException($"'{name}' is ambiguous:\n  " + string.Join("\n  ", qualified)),
        };
    }

    // ---- Creating and syncing -----------------------------------------------------------

    private CliBreakpoint AddBreakpoint(Location location, string? condition, bool temporary)
    {
        CliBreakpoint breakpoint;
        if (location.Function is { } function)
        {
            function = QualifyFunction(function);
            breakpoint = new CliBreakpoint { Id = _nextBreakpointId++, Function = function, Condition = condition, Temporary = temporary };
            if (ResolveFunctionInProgram(function) is { } start)
            {
                breakpoint.BoundLine = start.Line;
                breakpoint.FunctionPath = start.Path;
                breakpoint.Verified = true;
            }
        }
        else
        {
            var path = location.Path!;
            var resolved = ResolveInProgram(path, location.Line);
            if (resolved is null && _program?.Symbols?.ContainsDocument(path) == true)
            {
                throw new CommandException($"There is no code at or after {DisplayPath(path)}:{location.Line}.");
            }

            breakpoint = new CliBreakpoint
            {
                Id = _nextBreakpointId++,
                Path = path,
                Line = location.Line,
                Condition = condition,
                Temporary = temporary,
                BoundLine = resolved?.Line,
                Method = resolved?.Method,
                Verified = resolved is not null,
            };
        }

        _breakpoints.Add(breakpoint);
        try
        {
            Sync(breakpoint);
        }
        catch
        {
            _ = _breakpoints.Remove(breakpoint);
            throw;
        }

        return breakpoint;
    }

    private void RemoveBreakpoint(CliBreakpoint breakpoint)
    {
        _ = _breakpoints.Remove(breakpoint);
        Sync(breakpoint);
    }

    /// <summary>Sends the breakpoints of the file (or the function breakpoints) that <paramref name="changed"/> belongs to.</summary>
    private void Sync(CliBreakpoint changed)
    {
        if (_session is not { } session || _state is State.NotStarted or State.Exited)
        {
            return;
        }

        Engine(() =>
        {
            if (changed.Path is { } path)
            {
                SyncFile(session, path);
            }
            else
            {
                SyncFunctions(session);
            }
        });
    }

    /// <summary>Engine thread: sends every breakpoint to a new session.</summary>
    private void ApplyAllBreakpoints(DebugSession session)
    {
        _byEngineId.Clear();
        foreach (var path in _breakpoints.Where(b => b.Path is not null).Select(b => b.Path!).Distinct(StringComparer.Ordinal).ToList())
        {
            SyncFile(session, path);
        }

        if (_breakpoints.Any(b => b.Function is not null))
        {
            SyncFunctions(session);
        }
    }

    private void SyncFile(DebugSession session, string path)
    {
        var active = _breakpoints.FindAll(b => b.Path == path && b.Enabled);
        var specs = active.ConvertAll(b => new SourceBreakpointSpec(b.Line, Condition: b.Condition, HitCondition: b.HitCondition));
        Record(active, session.SetSourceBreakpoints(path, specs));
    }

    private void SyncFunctions(DebugSession session)
    {
        var active = _breakpoints.FindAll(b => b.Function is not null && b.Enabled);
        var specs = active.ConvertAll(b => new FunctionBreakpointSpec(b.Function!, b.Condition, b.HitCondition));
        Record(active, session.SetFunctionBreakpoints(specs));
    }

    /// <summary>Engine results come back in request order; remember which engine id is which.</summary>
    private void Record(List<CliBreakpoint> sent, List<BreakpointView> views)
    {
        lock (_gate)
        {
            foreach (var stale in _byEngineId.Where(pair => !_breakpoints.Contains(pair.Value) || sent.Contains(pair.Value)).ToList())
            {
                _ = _byEngineId.Remove(stale.Key);
            }

            for (var i = 0; i < sent.Count && i < views.Count; i++)
            {
                _byEngineId[views[i].Id] = sent[i];
                Update(sent[i], views[i]);
            }
        }
    }

    /// <summary>Engine thread: a pending breakpoint bound (or a bound one moved).</summary>
    private void OnBreakpointChanged(BreakpointView view)
    {
        lock (_gate)
        {
            if (_byEngineId.TryGetValue(view.Id, out var breakpoint))
            {
                Update(breakpoint, view);
            }
        }
    }

    private static void Update(CliBreakpoint breakpoint, BreakpointView view)
    {
        if (view.Verified)
        {
            breakpoint.Verified = true;
            breakpoint.BoundLine = view.Line ?? breakpoint.BoundLine;
        }
    }

    private void CountHits(StopInfo stop)
    {
        lock (_gate)
        {
            foreach (var id in stop.HitBreakpointIds)
            {
                if (_byEngineId.TryGetValue(id, out var breakpoint))
                {
                    breakpoint.Hits++;
                }
            }
        }
    }

    private void ResetHitCounts()
    {
        foreach (var breakpoint in _breakpoints)
        {
            breakpoint.Hits = 0;
        }
    }

    /// <summary>The user breakpoints behind a stop, for "[Breakpoint 1]".</summary>
    private List<CliBreakpoint> HitBreakpoints(StopInfo stop)
    {
        lock (_gate)
        {
            var result = new List<CliBreakpoint>();
            foreach (var id in stop.HitBreakpointIds)
            {
                if (_byEngineId.TryGetValue(id, out var breakpoint) && !result.Contains(breakpoint))
                {
                    result.Add(breakpoint);
                }
            }

            return result;
        }
    }

    private CliBreakpoint FindBreakpoint(string text)
    {
        var id = int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new CommandException($"'{text}' is not a breakpoint number.");
        return _breakpoints.Find(b => b.Id == id && !b.Temporary) ?? throw new CommandException($"There is no breakpoint {id}.");
    }

    private static string DescribeBreakpoint(CliBreakpoint breakpoint)
    {
        if (breakpoint.Function is { } function)
        {
            return !breakpoint.Verified ? $"{function}() (pending)"
                : breakpoint.FunctionPath is { } file ? $"{function}() {DisplayPath(file)}:{breakpoint.BoundLine}"
                : $"{function}()";
        }

        var line = breakpoint.BoundLine ?? breakpoint.Line;
        var where = $"{DisplayPath(breakpoint.Path!)}:{line}";
        var method = breakpoint.Method is { } name ? FunctionName(name) + " " : "";
        return breakpoint.Verified ? method + where : where + " (pending)";
    }
}
