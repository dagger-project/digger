using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Digger.Protocol;

// Wire types for the Debug Adapter Protocol (https://microsoft.github.io/debug-adapter-protocol/).
// Only the fields the adapter reads or writes are modelled; unknown fields are ignored.

// ---------------------------------------------------------------------------------------
// Request arguments
//
// Mutable setters on purpose: with init-only properties the JSON source generator assigns
// every property through an object initializer, overwriting the defaults below (e.g.
// JustMyCode = true) with false whenever the client omits the field.
// ---------------------------------------------------------------------------------------

public sealed record InitializeArguments
{
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? AdapterId { get; set; }
    public bool LinesStartAt1 { get; set; } = true;
    public bool ColumnsStartAt1 { get; set; } = true;
    public string? PathFormat { get; set; }
    public bool SupportsVariableType { get; set; }
    public bool SupportsRunInTerminalRequest { get; set; }
}

/// <summary>
/// Launch configuration. Field names follow vscode-csharp/netcoredbg so existing
/// launch.json / debug.json files work unchanged.
/// </summary>
public sealed record LaunchArguments
{
    public string? Program { get; set; }
    public List<string>? Args { get; set; }
    public string? Cwd { get; set; }
    public Dictionary<string, string?>? Env { get; set; }
    public bool StopAtEntry { get; set; }
    public bool JustMyCode { get; set; } = true;
    public bool NoDebug { get; set; }
    public bool EnableStepFiltering { get; set; } = true;

    /// <summary>Evaluate property getters when expanding objects (func-eval).</summary>
    public bool EvaluateProperties { get; set; } = true;

    /// <summary>Explicit path to the <c>dotnet</c> host used to run a .dll program.</summary>
    public string? DotnetPath { get; set; }

    /// <summary>
    /// <c>internalConsole</c> (default: output goes to the debug console, stdin is empty),
    /// <c>integratedTerminal</c> or <c>externalTerminal</c> (the editor runs the program in a
    /// terminal, so it can read input).
    /// </summary>
    public string? Console { get; set; }

    /// <summary>Show properties as "click to evaluate" instead of running every getter on expand.</summary>
    public bool LazyProperties { get; set; }

    /// <summary>Download PDBs from the Microsoft and NuGet symbol servers for modules without symbols.</summary>
    public bool SymbolServer { get; set; }

    /// <summary>Fetch sources through Source Link when the file is not available locally.</summary>
    public bool SourceLink { get; set; } = true;

    /// <summary>Maps source paths recorded at build time to local paths (prefix replacement).</summary>
    public Dictionary<string, string>? SourceFileMap { get; set; }
}

public sealed record AttachArguments
{
    [JsonConverter(typeof(FlexibleInt32Converter))]
    public int ProcessId { get; set; }

    public bool JustMyCode { get; set; } = true;
    public bool EvaluateProperties { get; set; } = true;
    public bool LazyProperties { get; set; }
    public bool SymbolServer { get; set; }
    public bool SourceLink { get; set; } = true;
    public Dictionary<string, string>? SourceFileMap { get; set; }
}

public sealed record Source
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public int? SourceReference { get; set; }
    public string? PresentationHint { get; set; }
    public string? Origin { get; set; }
}

public sealed record SourceBreakpoint
{
    public int Line { get; set; }
    public int? Column { get; set; }
    public string? Condition { get; set; }
    public string? HitCondition { get; set; }
    public string? LogMessage { get; set; }
}

public sealed record SetBreakpointsArguments
{
    public required Source Source { get; set; }
    public List<SourceBreakpoint>? Breakpoints { get; set; }
    public bool SourceModified { get; set; }
}

public sealed record FunctionBreakpoint
{
    public required string Name { get; set; }
    public string? Condition { get; set; }
    public string? HitCondition { get; set; }
}

public sealed record SetFunctionBreakpointsArguments
{
    public List<FunctionBreakpoint> Breakpoints { get; set; } = [];
}

public sealed record SetExceptionBreakpointsArguments
{
    public List<string> Filters { get; set; } = [];
    public List<ExceptionFilterOptions>? FilterOptions { get; set; }
}

/// <summary>A filter enabled with a condition (the client set <c>supportsExceptionFilterOptions</c>).</summary>
public sealed record ExceptionFilterOptions
{
    public string FilterId { get; set; } = "";
    public string? Condition { get; set; }
}

public sealed record ThreadArguments
{
    public int ThreadId { get; set; }
    public bool SingleThread { get; set; }
}

public sealed record StackTraceArguments
{
    public int ThreadId { get; set; }
    public int? StartFrame { get; set; }
    public int? Levels { get; set; }
}

public sealed record ScopesArguments
{
    public int FrameId { get; set; }
}

public sealed record ValueFormat
{
    public bool Hex { get; set; }
}

public sealed record VariablesArguments
{
    public int VariablesReference { get; set; }
    public string? Filter { get; set; }
    public int? Start { get; set; }
    public int? Count { get; set; }
    public ValueFormat? Format { get; set; }
}

public sealed record SetVariableArguments
{
    public int VariablesReference { get; set; }
    public required string Name { get; set; }
    public required string Value { get; set; }
    public ValueFormat? Format { get; set; }
}

public sealed record EvaluateArguments
{
    public required string Expression { get; set; }
    public int? FrameId { get; set; }
    public string? Context { get; set; }
    public ValueFormat? Format { get; set; }
}

public sealed record GotoTargetsArguments
{
    public required Source Source { get; set; }
    public int Line { get; set; }
    public int? Column { get; set; }
}

public sealed record GotoArguments
{
    public int ThreadId { get; set; }
    public int TargetId { get; set; }
}

public sealed record CompletionsArguments
{
    public int? FrameId { get; set; }
    public required string Text { get; set; }
    public int Column { get; set; }
    public int? Line { get; set; }
}

public sealed record DisconnectArguments
{
    public bool Restart { get; set; }
    public bool? TerminateDebuggee { get; set; }
}

// ---------------------------------------------------------------------------------------
// Response bodies and shared types
// ---------------------------------------------------------------------------------------

public sealed record ExceptionBreakpointsFilter
{
    public required string Filter { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public bool Default { get; init; }
    public bool SupportsCondition { get; init; }
    public string? ConditionDescription { get; init; }
}

public sealed record Capabilities
{
    public bool SupportsConfigurationDoneRequest { get; init; }
    public bool SupportsFunctionBreakpoints { get; init; }
    public bool SupportsConditionalBreakpoints { get; init; }
    public bool SupportsHitConditionalBreakpoints { get; init; }
    public bool SupportsEvaluateForHovers { get; init; }
    public List<ExceptionBreakpointsFilter>? ExceptionBreakpointFilters { get; init; }
    public bool SupportsSetVariable { get; init; }
    public bool SupportsExceptionInfoRequest { get; init; }
    public bool SupportsTerminateRequest { get; init; }
    public bool SupportsLogPoints { get; init; }
    public bool SupportsModulesRequest { get; init; }
    public bool SupportsValueFormattingOptions { get; init; }
    public bool SupportsDelayedStackTraceLoading { get; init; }
    public bool SupportTerminateDebuggee { get; init; }
    public bool SupportsSingleThreadExecutionRequests { get; init; }
    public bool SupportsGotoTargetsRequest { get; init; }
    public bool SupportsCompletionsRequest { get; init; }
    public bool SupportsExceptionFilterOptions { get; init; }
    public List<string>? CompletionTriggerCharacters { get; init; }
}

public sealed record Breakpoint
{
    public int? Id { get; init; }
    public bool Verified { get; init; }
    public string? Message { get; init; }
    public Source? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? Reason { get; init; }
}

public sealed record BreakpointsResponseBody(List<Breakpoint> Breakpoints);

public sealed record ContinueResponseBody(bool AllThreadsContinued);

public sealed record DapThread(int Id, string Name);

public sealed record ThreadsResponseBody(List<DapThread> Threads);

public sealed record StackFrame
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public Source? Source { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? PresentationHint { get; init; }
    public string? ModuleId { get; init; }
}

public sealed record StackTraceResponseBody(List<StackFrame> StackFrames, int TotalFrames);

public sealed record Scope
{
    public required string Name { get; init; }
    public string? PresentationHint { get; init; }
    public int VariablesReference { get; init; }
    public bool Expensive { get; init; }
}

public sealed record ScopesResponseBody(List<Scope> Scopes);

public sealed record VariablePresentationHint
{
    public string? Kind { get; init; }
    public List<string>? Attributes { get; init; }
    public string? Visibility { get; init; }
    public bool? Lazy { get; init; }
}

public sealed record Variable
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string? Type { get; init; }
    public VariablePresentationHint? PresentationHint { get; init; }
    public string? EvaluateName { get; init; }
    public int VariablesReference { get; init; }
    public int? NamedVariables { get; init; }
    public int? IndexedVariables { get; init; }
}

public sealed record VariablesResponseBody(List<Variable> Variables);

public sealed record SetVariableResponseBody
{
    public required string Value { get; init; }
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
}

public sealed record EvaluateResponseBody
{
    public required string Result { get; init; }
    public string? Type { get; init; }
    public VariablePresentationHint? PresentationHint { get; init; }
    public int VariablesReference { get; init; }
    public int? NamedVariables { get; init; }
    public int? IndexedVariables { get; init; }
}

public sealed record GotoTarget
{
    public int Id { get; init; }
    public required string Label { get; init; }
    public int Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

public sealed record GotoTargetsResponseBody(List<GotoTarget> Targets);

public sealed record CompletionItem
{
    public required string Label { get; init; }
    public string? Text { get; init; }
    public string? Type { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
}

public sealed record CompletionsResponseBody(List<CompletionItem> Targets);

public sealed record ExceptionDetails
{
    public string? Message { get; init; }
    public string? TypeName { get; init; }
    public string? FullTypeName { get; init; }
    public string? StackTrace { get; init; }
    public List<ExceptionDetails>? InnerException { get; init; }
}

public sealed record ExceptionInfoResponseBody
{
    public required string ExceptionId { get; init; }
    public string? Description { get; init; }
    public required string BreakMode { get; init; }
    public ExceptionDetails? Details { get; init; }
}

public sealed record DapModule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public bool? IsOptimized { get; init; }
    public bool? IsUserCode { get; init; }
    public string? SymbolStatus { get; init; }
    public string? SymbolFilePath { get; init; }
}

public sealed record ModulesResponseBody(List<DapModule> Modules, int TotalModules);

// ---------------------------------------------------------------------------------------
// Event bodies
// ---------------------------------------------------------------------------------------

public sealed record StoppedEventBody
{
    public required string Reason { get; init; }
    public string? Description { get; init; }
    public int? ThreadId { get; init; }
    public string? Text { get; init; }
    public bool AllThreadsStopped { get; init; }
    public List<int>? HitBreakpointIds { get; init; }
}

public sealed record ContinuedEventBody(int ThreadId, bool AllThreadsContinued);

public sealed record ExitedEventBody(int ExitCode);

public sealed record ThreadEventBody(string Reason, int ThreadId);

public sealed record OutputEventBody
{
    public string? Category { get; init; }
    public required string Output { get; init; }
}

public sealed record BreakpointEventBody(string Reason, Breakpoint Breakpoint);

public sealed record ModuleEventBody(string Reason, DapModule Module);

// ---------------------------------------------------------------------------------------
// Reverse requests (adapter -> client)
// ---------------------------------------------------------------------------------------

public sealed record RunInTerminalRequestArguments
{
    public string? Kind { get; init; }
    public string? Title { get; init; }
    public required string Cwd { get; init; }
    public required List<string> Args { get; init; }
    public Dictionary<string, string?>? Env { get; init; }
}

public sealed record ProcessEventBody
{
    public required string Name { get; init; }
    public int? SystemProcessId { get; init; }
    public bool IsLocalProcess { get; init; } = true;
    public string? StartMethod { get; init; }
}

// ---------------------------------------------------------------------------------------
// Serializer context (source generated, reflection free)
// ---------------------------------------------------------------------------------------

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(InitializeArguments))]
[JsonSerializable(typeof(LaunchArguments))]
[JsonSerializable(typeof(AttachArguments))]
[JsonSerializable(typeof(SetBreakpointsArguments))]
[JsonSerializable(typeof(SetFunctionBreakpointsArguments))]
[JsonSerializable(typeof(SetExceptionBreakpointsArguments))]
[JsonSerializable(typeof(ThreadArguments))]
[JsonSerializable(typeof(StackTraceArguments))]
[JsonSerializable(typeof(ScopesArguments))]
[JsonSerializable(typeof(VariablesArguments))]
[JsonSerializable(typeof(SetVariableArguments))]
[JsonSerializable(typeof(EvaluateArguments))]
[JsonSerializable(typeof(DisconnectArguments))]
[JsonSerializable(typeof(GotoTargetsArguments))]
[JsonSerializable(typeof(GotoArguments))]
[JsonSerializable(typeof(CompletionsArguments))]
[JsonSerializable(typeof(GotoTargetsResponseBody))]
[JsonSerializable(typeof(CompletionsResponseBody))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(BreakpointsResponseBody))]
[JsonSerializable(typeof(ContinueResponseBody))]
[JsonSerializable(typeof(ThreadsResponseBody))]
[JsonSerializable(typeof(StackTraceResponseBody))]
[JsonSerializable(typeof(ScopesResponseBody))]
[JsonSerializable(typeof(VariablesResponseBody))]
[JsonSerializable(typeof(SetVariableResponseBody))]
[JsonSerializable(typeof(EvaluateResponseBody))]
[JsonSerializable(typeof(ExceptionInfoResponseBody))]
[JsonSerializable(typeof(ModulesResponseBody))]
[JsonSerializable(typeof(StoppedEventBody))]
[JsonSerializable(typeof(ContinuedEventBody))]
[JsonSerializable(typeof(ExitedEventBody))]
[JsonSerializable(typeof(ThreadEventBody))]
[JsonSerializable(typeof(OutputEventBody))]
[JsonSerializable(typeof(BreakpointEventBody))]
[JsonSerializable(typeof(ModuleEventBody))]
[JsonSerializable(typeof(ProcessEventBody))]
[JsonSerializable(typeof(RunInTerminalRequestArguments))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class DapJsonContext : JsonSerializerContext;
