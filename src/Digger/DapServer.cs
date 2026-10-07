using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Digger.Engine;
using Digger.Engine.Breakpoints;
using Digger.Engine.Infrastructure;
using Digger.Engine.Inspection;
using Digger.Protocol;
using StackFrame = Digger.Protocol.StackFrame;

namespace Digger;

/// <summary>
/// Maps Debug Adapter Protocol requests onto the engine and engine events back onto DAP.
/// Requests are read on a background task and executed in order on the engine thread.
/// </summary>
internal sealed class DapServer : IDebuggerEvents, ITerminalHost, IDisposable
{
    private static readonly DapJsonContext Json = DapJsonContext.Default;

    private readonly DapConnection _connection;
    private readonly EngineDispatcher _dispatcher;
    private readonly DebugSession _session;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _linesStartAt1 = true;
    private bool _columnsStartAt1 = true;
    private bool _supportsRunInTerminal;
    private string _terminalKind = "integrated";
    private Process? _noDebugProcess;
    private int _terminatedSent;

    public DapServer(DapConnection connection, EngineDispatcher dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _session = new DebugSession(dispatcher, this);
    }

    /// <summary>Processes requests until the client disconnects or closes the stream.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);
        _ = await Task.WhenAny(reader, _shutdown.Task);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _connection.ReadRequestsAsync(cancellationToken))
            {
                _dispatcher.Post(() => Handle(request));
            }
        }
        catch (Exception ex) when (ex is IOException or DapException or OperationCanceledException)
        {
            Log.Warn($"Protocol input closed: {ex.Message}");
        }

        // Client went away without "disconnect": clean up like a disconnect would.
        _dispatcher.Post(() =>
        {
            _session.Disconnect(terminateDebuggee: null);
            _shutdown.TrySetResult();
        });
    }

    // ---- Requests ---------------------------------------------------------------------

    private void Handle(DapRequest request)
    {
        try
        {
            Log.Info($"Request {request.Seq}: {request.Command}");
            switch (request.Command)
            {
                case "initialize": Initialize(request); break;
                case "launch": Launch(request); break;
                case "attach": Attach(request); break;
                case "configurationDone": ConfigurationDone(request); break;
                case "setBreakpoints": SetBreakpoints(request); break;
                case "setFunctionBreakpoints": SetFunctionBreakpoints(request); break;
                case "setExceptionBreakpoints": SetExceptionBreakpoints(request); break;
                case "threads": Threads(request); break;
                case "stackTrace": StackTrace(request); break;
                case "scopes": Scopes(request); break;
                case "variables": Variables(request); break;
                case "setVariable": SetVariable(request); break;
                case "evaluate": Evaluate(request); break;
                case "continue": Continue(request); break;
                case "next": Step(request, StepKind.Over); break;
                case "stepIn": Step(request, StepKind.In); break;
                case "stepOut": Step(request, StepKind.Out); break;
                case "pause": Pause(request); break;
                case "exceptionInfo": ExceptionInfo(request); break;
                case "gotoTargets": GotoTargets(request); break;
                case "goto": Goto(request); break;
                case "completions": Completions(request); break;
                case "modules": Modules(request); break;
                case "terminate": Terminate(request); break;
                case "disconnect": Disconnect(request); break;
                default: _connection.SendError(request, $"Request '{request.Command}' is not supported."); break;
            }
        }
        catch (Exception ex) when (ex is DapException or InvalidOperationException or FileNotFoundException
            or ArgumentException or NotSupportedException or Interop.CorDebug.CorDebugException or IOException)
        {
            Log.Warn($"{request.Command} failed: {ex.Message}");
            _connection.SendError(request, ex.Message);
        }
        catch (Exception ex)
        {
            // Last line of defense: an unexpected failure must still answer the request.
            Log.Error($"{request.Command} crashed", ex);
            _connection.SendError(request, $"Internal error: {ex.Message}");
        }
    }

    private void Initialize(DapRequest request)
    {
        var arguments = request.GetArgumentsOrDefault(Json.InitializeArguments, new InitializeArguments());
        _linesStartAt1 = arguments.LinesStartAt1;
        _columnsStartAt1 = arguments.ColumnsStartAt1;
        _supportsRunInTerminal = arguments.SupportsRunInTerminalRequest;

        _connection.SendResponse(request, new Capabilities
        {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = true,
            SupportsConditionalBreakpoints = true,
            SupportsHitConditionalBreakpoints = true,
            SupportsEvaluateForHovers = true,
            SupportsSetVariable = true,
            SupportsExceptionInfoRequest = true,
            SupportsTerminateRequest = true,
            SupportsLogPoints = true,
            SupportsModulesRequest = true,
            SupportsValueFormattingOptions = true,
            SupportsDelayedStackTraceLoading = true,
            SupportTerminateDebuggee = true,
            SupportsSingleThreadExecutionRequests = true,
            SupportsGotoTargetsRequest = true,
            SupportsCompletionsRequest = true,
            SupportsExceptionFilterOptions = true,
            CompletionTriggerCharacters = ["."],
            ExceptionBreakpointFilters =
            [
                new ExceptionBreakpointsFilter
                {
                    Filter = "all",
                    Label = "All Exceptions",
                    Description = "Break when any exception is thrown (in user code when Just My Code is on).",
                    SupportsCondition = true,
                    ConditionDescription = "Exception types, e.g. 'InvalidOperationException, System.IO.*, !OperationCanceledException' (derived types match too; ! excludes).",
                },
                new ExceptionBreakpointsFilter { Filter = "unhandled", Label = "Unhandled Exceptions", Default = true },
            ],
        }, Json.Capabilities);

        // Breakpoints may be configured right away; the debuggee only starts at configurationDone.
        _connection.SendEvent("initialized");
    }

    private void Launch(DapRequest request)
    {
        var arguments = request.GetArguments(Json.LaunchArguments);
        if (string.IsNullOrWhiteSpace(arguments.Program))
        {
            throw new DapException("'program' is required: the path to the built .dll (or apphost executable).");
        }

        if (arguments.NoDebug)
        {
            _noDebugProcess = NoDebugRunner.Start(arguments, Output, exitCode => _dispatcher.Post(() =>
            {
                Exited(exitCode);
                Terminated();
            }));
            _connection.SendResponse(request);
            return;
        }

        ITerminalHost? terminal = null;
        if (arguments.Console is "integratedTerminal" or "externalTerminal")
        {
            if (_supportsRunInTerminal)
            {
                _terminalKind = arguments.Console == "externalTerminal" ? "external" : "integrated";
                terminal = this;
            }
            else
            {
                Output("console", $"This editor does not support running programs in a terminal; using the debug console (\"console\": \"{arguments.Console}\" ignored).\n");
            }
        }

        _session.Launch(
            new LaunchOptions(arguments.Program)
            {
                Arguments = arguments.Args ?? [],
                WorkingDirectory = arguments.Cwd,
                Environment = arguments.Env,
                StopAtEntry = arguments.StopAtEntry,
                DotnetPath = arguments.DotnetPath,
                Terminal = terminal,
            },
            new SessionOptions
            {
                JustMyCode = arguments.JustMyCode,
                EvaluateProperties = arguments.EvaluateProperties,
                StepFiltering = arguments.EnableStepFiltering,
                LazyProperties = arguments.LazyProperties,
                SymbolServer = arguments.SymbolServer,
                SourceLink = arguments.SourceLink,
                SourceFileMap = arguments.SourceFileMap,
            });
        _connection.SendResponse(request);
    }

    private void Attach(DapRequest request)
    {
        var arguments = request.GetArguments(Json.AttachArguments);
        if (arguments.ProcessId <= 0)
        {
            throw new DapException("'processId' is required for attach.");
        }

        _session.Attach(arguments.ProcessId, new SessionOptions
        {
            JustMyCode = arguments.JustMyCode,
            EvaluateProperties = arguments.EvaluateProperties,
            LazyProperties = arguments.LazyProperties,
            SymbolServer = arguments.SymbolServer,
            SourceLink = arguments.SourceLink,
            SourceFileMap = arguments.SourceFileMap,
        });
        _connection.SendResponse(request);
    }

    private void ConfigurationDone(DapRequest request)
    {
        _session.ConfigurationDone();
        _connection.SendResponse(request);
    }

    private void SetBreakpoints(DapRequest request)
    {
        var arguments = request.GetArguments(Json.SetBreakpointsArguments);
        var path = arguments.Source.Path ?? throw new DapException("Breakpoints need a source path.");
        var specs = new List<SourceBreakpointSpec>();
        foreach (var breakpoint in arguments.Breakpoints ?? [])
        {
            specs.Add(new SourceBreakpointSpec(
                FromClientLine(breakpoint.Line),
                breakpoint.Column,
                NullIfEmpty(breakpoint.Condition),
                NullIfEmpty(breakpoint.HitCondition),
                NullIfEmpty(breakpoint.LogMessage)));
        }

        var result = new List<Breakpoint>();
        foreach (var view in _session.SetSourceBreakpoints(path, specs))
        {
            result.Add(ToDap(view));
        }

        _connection.SendResponse(request, new BreakpointsResponseBody(result), Json.BreakpointsResponseBody);
    }

    private void SetFunctionBreakpoints(DapRequest request)
    {
        var arguments = request.GetArguments(Json.SetFunctionBreakpointsArguments);
        var specs = new List<FunctionBreakpointSpec>();
        foreach (var breakpoint in arguments.Breakpoints)
        {
            specs.Add(new FunctionBreakpointSpec(breakpoint.Name, NullIfEmpty(breakpoint.Condition), NullIfEmpty(breakpoint.HitCondition)));
        }

        var result = new List<Breakpoint>();
        foreach (var view in _session.SetFunctionBreakpoints(specs))
        {
            result.Add(ToDap(view));
        }

        _connection.SendResponse(request, new BreakpointsResponseBody(result), Json.BreakpointsResponseBody);
    }

    private void SetExceptionBreakpoints(DapRequest request)
    {
        var arguments = request.GetArguments(Json.SetExceptionBreakpointsArguments);
        var options = arguments.FilterOptions ?? [];
        var all = options.Find(static o => o.FilterId == "all");
        _session.SetExceptionFilters(
            breakOnAll: arguments.Filters.Contains("all") || all is not null,
            breakOnUnhandled: arguments.Filters.Contains("unhandled") || options.Exists(static o => o.FilterId == "unhandled"),
            thrownFilter: ExceptionFilter.Parse(all?.Condition));
        _connection.SendResponse(request);
    }

    private void Threads(DapRequest request)
    {
        var threads = new List<DapThread>();
        foreach (var thread in _session.GetThreads())
        {
            threads.Add(new DapThread(thread.Id, thread.Name));
        }

        _connection.SendResponse(request, new ThreadsResponseBody(threads), Json.ThreadsResponseBody);
    }

    private void StackTrace(DapRequest request)
    {
        var arguments = request.GetArguments(Json.StackTraceArguments);
        var (frames, total) = _session.GetStackTrace(arguments.ThreadId, arguments.StartFrame ?? 0, arguments.Levels ?? 0);
        var result = new List<StackFrame>(frames.Count);
        foreach (var frame in frames)
        {
            var hasSource = frame.SourcePath is not null;
            result.Add(new StackFrame
            {
                Id = frame.Id,
                Name = frame.Name,
                Source = hasSource ? new Source { Name = Path.GetFileName(frame.SourcePath), Path = frame.SourcePath } : null,
                Line = hasSource ? ToClientLine(frame.Line) : 0,
                Column = hasSource ? ToClientColumn(frame.Column) : 0,
                EndLine = hasSource ? ToClientLine(frame.EndLine) : null,
                EndColumn = hasSource ? ToClientColumn(frame.EndColumn) : null,
                PresentationHint = frame.IsLabel ? "label" : frame.IsUserCode && hasSource ? null : "subtle",
                ModuleId = frame.ModuleId,
            });
        }

        _connection.SendResponse(request, new StackTraceResponseBody(result, total), Json.StackTraceResponseBody);
    }

    private void Scopes(DapRequest request)
    {
        var arguments = request.GetArguments(Json.ScopesArguments);
        var scopes = new List<Scope>();
        foreach (var scope in _session.GetScopes(arguments.FrameId))
        {
            scopes.Add(new Scope { Name = scope.Name, VariablesReference = scope.Reference, PresentationHint = "locals" });
        }

        _connection.SendResponse(request, new ScopesResponseBody(scopes), Json.ScopesResponseBody);
    }

    private void Variables(DapRequest request)
    {
        var arguments = request.GetArguments(Json.VariablesArguments);
        var start = arguments.Start ?? 0;
        var count = arguments.Count ?? 0;
        var result = new List<Variable>();
        foreach (var view in _session.GetVariables(arguments.VariablesReference, start, count, arguments.Format?.Hex ?? false))
        {
            result.Add(ToDap(view));
        }

        _connection.SendResponse(request, new VariablesResponseBody(result), Json.VariablesResponseBody);
    }

    private void SetVariable(DapRequest request)
    {
        var arguments = request.GetArguments(Json.SetVariableArguments);
        var view = _session.SetVariable(arguments.VariablesReference, arguments.Name, arguments.Value, arguments.Format?.Hex ?? false);
        _connection.SendResponse(request, new SetVariableResponseBody
        {
            Value = view.Value,
            Type = view.Type,
            VariablesReference = view.Reference,
        }, Json.SetVariableResponseBody);
    }

    private void Evaluate(DapRequest request)
    {
        var arguments = request.GetArguments(Json.EvaluateArguments);
        if (!_session.IsStopped)
        {
            throw new DapException("Expressions can only be evaluated while the debuggee is paused.");
        }

        var view = _session.Evaluate(arguments.Expression, arguments.FrameId, arguments.Format?.Hex ?? false);
        _connection.SendResponse(request, new EvaluateResponseBody
        {
            Result = view.Value,
            Type = view.Type,
            VariablesReference = view.Reference,
            IndexedVariables = view.IndexedCount > 0 ? view.IndexedCount : null,
            PresentationHint = Hint(view),
        }, Json.EvaluateResponseBody);
    }

    private void Continue(DapRequest request)
    {
        var arguments = request.GetArgumentsOrDefault(Json.ThreadArguments, new ThreadArguments());
        _session.Continue(arguments.ThreadId, arguments.SingleThread);
        _connection.SendResponse(request, new ContinueResponseBody(AllThreadsContinued: !arguments.SingleThread), Json.ContinueResponseBody);
    }

    private void Step(DapRequest request, StepKind kind)
    {
        var arguments = request.GetArguments(Json.ThreadArguments);
        _session.Step(arguments.ThreadId, kind, arguments.SingleThread);
        _connection.SendResponse(request);
    }

    private void GotoTargets(DapRequest request)
    {
        var arguments = request.GetArguments(Json.GotoTargetsArguments);
        var path = arguments.Source.Path ?? throw new DapException("gotoTargets needs a source path.");
        var targets = new List<GotoTarget>();
        foreach (var target in _session.GetGotoTargets(path, FromClientLine(arguments.Line)))
        {
            targets.Add(new GotoTarget
            {
                Id = target.Id,
                Label = $"Line {ToClientLine(target.Line)}",
                Line = ToClientLine(target.Line),
                Column = ToClientColumn(target.Column),
                EndLine = ToClientLine(target.EndLine),
                EndColumn = ToClientColumn(target.EndColumn),
            });
        }

        _connection.SendResponse(request, new GotoTargetsResponseBody(targets), Json.GotoTargetsResponseBody);
    }

    private void Goto(DapRequest request)
    {
        var arguments = request.GetArguments(Json.GotoArguments);
        var threadId = _session.Goto(arguments.ThreadId, arguments.TargetId);

        // The spec orders it: response first, then a "stopped" event with reason "goto".
        _connection.SendResponse(request);
        Stopped(new StopInfo(StopReason.Goto, threadId));
    }

    private void Completions(DapRequest request)
    {
        var arguments = request.GetArguments(Json.CompletionsArguments);
        var columnBase = _columnsStartAt1 ? 1 : 0;
        var items = new List<CompletionItem>();
        if (_session.IsStopped)
        {
            foreach (var item in _session.GetCompletions(arguments.FrameId, arguments.Text, arguments.Column - columnBase))
            {
                items.Add(new CompletionItem { Label = item.Label, Type = item.Type, Start = item.Start + columnBase, Length = item.Length });
            }
        }

        _connection.SendResponse(request, new CompletionsResponseBody(items), Json.CompletionsResponseBody);
    }

    private void Pause(DapRequest request)
    {
        _session.Pause();
        _connection.SendResponse(request);
    }

    private void ExceptionInfo(DapRequest request)
    {
        var arguments = request.GetArguments(Json.ThreadArguments);
        var exception = _session.GetExceptionInfo(arguments.ThreadId)
            ?? throw new DapException("The thread has no current exception.");
        _connection.SendResponse(request, new ExceptionInfoResponseBody
        {
            ExceptionId = exception.TypeName,
            Description = exception.Message,
            BreakMode = exception.BreakMode,
            Details = ToDap(exception),
        }, Json.ExceptionInfoResponseBody);
    }

    private void Modules(DapRequest request)
    {
        var modules = new List<DapModule>();
        foreach (var module in _session.GetModules())
        {
            modules.Add(ToDap(module));
        }

        _connection.SendResponse(request, new ModulesResponseBody(modules, modules.Count), Json.ModulesResponseBody);
    }

    private void Terminate(DapRequest request)
    {
        if (_noDebugProcess is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
        }

        _session.Terminate();
        _connection.SendResponse(request);
    }

    private void Disconnect(DapRequest request)
    {
        var arguments = request.GetArgumentsOrDefault(Json.DisconnectArguments, new DisconnectArguments());
        if (_noDebugProcess is { HasExited: false } process && arguments.TerminateDebuggee != false)
        {
            process.Kill(entireProcessTree: true);
        }

        _session.Disconnect(arguments.TerminateDebuggee);
        _connection.SendResponse(request);
        _shutdown.TrySetResult();
    }

    // ---- Terminal (runInTerminal) -----------------------------------------------------

    public IReadOnlyList<string> ShimCommand
    {
        get
        {
            var processPath = Environment.ProcessPath ?? "digger";

            // Framework-dependent runs via `dotnet digger.dll` need the dll as well.
            return Path.GetFileNameWithoutExtension(processPath) == "dotnet"
                ? [processPath, Path.Combine(AppContext.BaseDirectory, "digger.dll")]
                : [processPath];
        }
    }

    public void RunInTerminal(string title, string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment, Action<string> onFailure)
    {
        var request = new RunInTerminalRequestArguments
        {
            Kind = _terminalKind,
            Title = title,
            Cwd = workingDirectory,
            Args = [.. arguments],
            Env = environment is null ? null : new Dictionary<string, string?>(environment, StringComparer.Ordinal),
        };

        _ = _connection.SendRequestAsync("runInTerminal", request, Json.RunInTerminalRequestArguments).ContinueWith(
            task =>
            {
                if (!task.Result.Success)
                {
                    onFailure(task.Result.Message ?? "runInTerminal failed");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ---- Events -----------------------------------------------------------------------

    public void ProcessStarted(string name, int processId, bool attached) =>
        _connection.SendEvent("process", new ProcessEventBody
        {
            Name = name,
            SystemProcessId = processId,
            StartMethod = attached ? "attach" : "launch",
        }, Json.ProcessEventBody);

    public void Stopped(StopInfo info) =>
        _connection.SendEvent("stopped", new StoppedEventBody
        {
            Reason = info.Reason switch
            {
                StopReason.Entry => "entry",
                StopReason.Breakpoint => "breakpoint",
                StopReason.FunctionBreakpoint => "function breakpoint",
                StopReason.Step => "step",
                StopReason.Exception => "exception",
                StopReason.Goto => "goto",
                _ => "pause",
            },
            Description = info.Description,
            Text = info.Text,
            ThreadId = info.ThreadId,
            AllThreadsStopped = true,
            HitBreakpointIds = info.HitBreakpointIds.Count > 0 ? [.. info.HitBreakpointIds] : null,
        }, Json.StoppedEventBody);

    public void Continued(int threadId) =>
        _connection.SendEvent("continued", new ContinuedEventBody(threadId, AllThreadsContinued: true), Json.ContinuedEventBody);

    public void ThreadStarted(int threadId) =>
        _connection.SendEvent("thread", new ThreadEventBody("started", threadId), Json.ThreadEventBody);

    public void ThreadExited(int threadId) =>
        _connection.SendEvent("thread", new ThreadEventBody("exited", threadId), Json.ThreadEventBody);

    public void ModuleLoaded(ModuleView loadedModule) =>
        _connection.SendEvent("module", new ModuleEventBody("new", ToDap(loadedModule)), Json.ModuleEventBody);

    public void ModuleUnloaded(ModuleView unloadedModule) =>
        _connection.SendEvent("module", new ModuleEventBody("removed", ToDap(unloadedModule)), Json.ModuleEventBody);

    public void BreakpointChanged(BreakpointView breakpoint) =>
        _connection.SendEvent("breakpoint", new BreakpointEventBody("changed", ToDap(breakpoint)), Json.BreakpointEventBody);

    public void Output(string category, string text) =>
        _connection.SendEvent("output", new OutputEventBody { Category = category, Output = text }, Json.OutputEventBody);

    public void Exited(int exitCode) =>
        _connection.SendEvent("exited", new ExitedEventBody(exitCode), Json.ExitedEventBody);

    public void Terminated()
    {
        if (Interlocked.Exchange(ref _terminatedSent, 1) == 0)
        {
            _connection.SendEvent("terminated");
        }
    }

    // ---- Mapping ----------------------------------------------------------------------

    private Breakpoint ToDap(BreakpointView view) => new()
    {
        Id = view.Id,
        Verified = view.Verified,
        Message = view.Message,
        Source = view.Path is null ? null : new Source { Name = Path.GetFileName(view.Path), Path = view.Path },
        Line = view.Line is { } line ? ToClientLine(line) : null,
        Column = view.Column is { } column ? ToClientColumn(column) : null,
        EndLine = view.EndLine is { } endLine ? ToClientLine(endLine) : null,
        EndColumn = view.EndColumn is { } endColumn ? ToClientColumn(endColumn) : null,
        Reason = view.Verified ? null : "pending",
    };

    private static Variable ToDap(VariableView view) => new()
    {
        Name = view.Name,
        Value = view.Value,
        Type = view.Type,
        VariablesReference = view.Reference,
        IndexedVariables = view.IndexedCount > 0 ? view.IndexedCount : null,
        EvaluateName = view.EvaluateName,
        PresentationHint = Hint(view),
    };

    private static VariablePresentationHint? Hint(VariableView view)
    {
        var kind = view.Kind switch
        {
            VariableKind.Property => "property",
            VariableKind.Method => "method",
            VariableKind.Class => "class",
            VariableKind.Virtual => "virtual",
            _ => null,
        };
        return kind is null && !view.IsReadOnly && !view.IsLazy
            ? null
            : new VariablePresentationHint
            {
                Kind = kind,
                Attributes = view.IsReadOnly && !view.IsLazy ? ["readOnly"] : null,
                Lazy = view.IsLazy ? true : null,
            };
    }

    private static DapModule ToDap(ModuleView module) => new()
    {
        Id = module.Id,
        Name = module.Name,
        Path = module.Path,
        IsOptimized = module.IsOptimized,
        IsUserCode = module.IsUserCode,
        SymbolStatus = module.HasSymbols ? "Symbols loaded." : "Symbols not loaded.",
        SymbolFilePath = module.SymbolPath,
    };

    private static ExceptionDetails ToDap(ExceptionView exception) => new()
    {
        Message = exception.Message,
        TypeName = exception.TypeName,
        FullTypeName = exception.TypeName,
        StackTrace = exception.StackTrace,
        InnerException = exception.Inner is null ? null : [ToDap(exception.Inner)],
    };

    private int ToClientLine(int line) => _linesStartAt1 ? line : line - 1;

    private int FromClientLine(int line) => _linesStartAt1 ? line : line + 1;

    private int ToClientColumn(int column) => _columnsStartAt1 ? column : column - 1;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Releases the session on the engine thread, where it lives.</summary>
    public void Dispose()
    {
        if (_dispatcher.IsEngineThread)
        {
            _session.Dispose();
            _noDebugProcess?.Dispose();
            return;
        }

        _dispatcher.Invoke(Dispose);
    }
}
