using System.Collections.Generic;

namespace Digger.Engine;

public enum StopReason
{
    Entry,
    Breakpoint,
    FunctionBreakpoint,
    Step,
    Exception,
    Pause,
    Goto,
}

public enum StepKind
{
    Over,
    In,
    Out,
}

/// <summary>Why and where the debuggee stopped.</summary>
public sealed record StopInfo(StopReason Reason, int ThreadId)
{
    public string? Description { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<int> HitBreakpointIds { get; init; } = [];
}

/// <summary>Options for launching a program under the debugger.</summary>
public sealed record LaunchOptions(string Program)
{
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public bool StopAtEntry { get; init; }

    public string? DotnetPath { get; init; }

    /// <summary>When set, the program runs in the editor's terminal instead of under digger's stdio.</summary>
    public ITerminalHost? Terminal { get; init; }
}

/// <summary>The editor's ability to run a command in a terminal (DAP <c>runInTerminal</c>).</summary>
public interface ITerminalHost
{
    /// <summary>Command that starts digger itself (the launch shim is digger in another mode).</summary>
    IReadOnlyList<string> ShimCommand { get; }

    /// <summary>Asks the editor to run <paramref name="arguments"/>; reports failure asynchronously.</summary>
    void RunInTerminal(string title, string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment, System.Action<string> onFailure);
}

/// <summary>Behavior shared by launch and attach.</summary>
public sealed record SessionOptions
{
    public bool JustMyCode { get; init; } = true;

    public bool EvaluateProperties { get; init; } = true;

    public bool StepFiltering { get; init; } = true;

    public bool LazyProperties { get; init; }

    /// <summary>Look up missing PDBs on the Microsoft and NuGet symbol servers.</summary>
    public bool SymbolServer { get; init; }

    /// <summary>Download sources via Source Link when the file is missing locally.</summary>
    public bool SourceLink { get; init; } = true;

    /// <summary>Build-time path prefix → local path prefix.</summary>
    public IReadOnlyDictionary<string, string>? SourceFileMap { get; init; }
}

public sealed record ThreadView(int Id, string Name);

public sealed record FrameView(int Id, string Name)
{
    public string? SourcePath { get; init; }

    public int Line { get; init; }

    public int Column { get; init; }

    public int EndLine { get; init; }

    public int EndColumn { get; init; }

    public bool IsUserCode { get; init; }

    public string? ModuleId { get; init; }

    /// <summary>A separator row, e.g. "[Async Call Stack]".</summary>
    public bool IsLabel { get; init; }
}

public sealed record ScopeView(string Name, int Reference);

/// <summary>A debug console completion item.</summary>
public sealed record CompletionView(string Label, string Type, int Start, int Length);

/// <summary>A place "set next statement" can move to.</summary>
public sealed record GotoTargetView(int Id, int Line, int Column, int EndLine, int EndColumn);

public sealed record ModuleView(string Id, string Name, string Path)
{
    public bool IsOptimized { get; init; }

    public bool IsUserCode { get; init; }

    public bool HasSymbols { get; init; }

    public string? SymbolPath { get; init; }
}

public sealed record BreakpointView(int Id, bool Verified)
{
    public string? Message { get; init; }

    public string? Path { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    public int? EndLine { get; init; }

    public int? EndColumn { get; init; }
}

public sealed record ExceptionView(string TypeName, string BreakMode)
{
    public string? Message { get; init; }

    public string? StackTrace { get; init; }

    public ExceptionView? Inner { get; init; }
}

/// <summary>Engine → protocol notifications. Called on the engine thread (output may come from any thread).</summary>
public interface IDebuggerEvents
{
    void ProcessStarted(string name, int processId, bool attached);

    void Stopped(StopInfo info);

    void Continued(int threadId);

    void ThreadStarted(int threadId);

    void ThreadExited(int threadId);

    void ModuleLoaded(ModuleView loadedModule);

    void ModuleUnloaded(ModuleView unloadedModule);

    void BreakpointChanged(BreakpointView breakpoint);

    void Output(string category, string text);

    void Exited(int exitCode);

    void Terminated();
}
