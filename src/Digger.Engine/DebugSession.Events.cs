using System;
using System.Collections.Generic;
using System.Text;
using Digger.Engine.Breakpoints;
using Digger.Engine.Infrastructure;
using Digger.Engine.Inspection;
using Digger.Engine.Runtime;
using Digger.Interop.CorDebug;

namespace Digger.Engine;

/// <content>ICorDebug callback handling and stepping.</content>
public sealed partial class DebugSession
{
    private const int MaxStepAttempts = 32;

    private ICorDebugStepper? _stepper;
    private StepKind _stepKind;
    private int _stepAttempts;
    private AsyncStep? _asyncStep;
    private AsyncStepOut? _asyncStepOut;

    /// <summary>
    /// A step out of an async method that already yielded (it runs on a thread-pool thread,
    /// so its awaiting caller is not on the stack). The method's task is asked to notify the
    /// debugger when an awaiter observes its completion, which happens in
    /// <c>Task.NotifyDebuggerOfWaitCompletion</c>, called from the caller's continuation.
    /// </summary>
    private sealed record AsyncStepOut(UserBreakpoint Notification, ICorDebugHandleValue Task);

    /// <summary>
    /// A step over an <c>await</c>: if the state machine yields, the stepper finishes in the
    /// caller, so instead we run until <see cref="Resume"/> is hit by the same state machine
    /// instance (<see cref="StateMachine"/>), then finish the statement with a normal step.
    /// </summary>
    private sealed record AsyncStep(UserBreakpoint Resume, ICorDebugHandleValue? StateMachine, uint MethodToken);

    // ---- Callback entry (runtime callback thread) -------------------------------------

    void ICallbackSink.OnCallback(ICorDebugController controller, DebugEvent debugEvent)
    {
        switch (debugEvent.Kind)
        {
            case DebugEventKind.EvalComplete:
                _funcEval.OnCompleted(exception: false);
                return;
            case DebugEventKind.EvalException:
                _funcEval.OnCompleted(exception: true);
                return;
        }

        if (_funcEval.IsRunning)
        {
            HandleDuringEvaluation(controller, debugEvent);
            return;
        }

        _dispatcher.Post(() => Dispatch(controller, debugEvent));
    }

    /// <summary>
    /// Runs on the callback thread while the engine thread waits for a func-eval. Nothing
    /// stops here: the event is noted (modules must still be set up before their code runs)
    /// and the process is continued so the evaluation can finish.
    /// </summary>
    private void HandleDuringEvaluation(ICorDebugController controller, DebugEvent debugEvent)
    {
        switch (debugEvent.Kind)
        {
            case DebugEventKind.ExitProcess:
                _funcEval.Abandon();
                _dispatcher.Post(() => Dispatch(controller, debugEvent));
                return;
            case DebugEventKind.LoadModule when debugEvent.Module is not null:
                var module = _modules.Register(debugEvent.Module, _options.JustMyCode);
                ConfigureModule(module);
                _dispatcher.Post(() => AfterModuleLoaded(module));
                break;
            case DebugEventKind.CreateThread or DebugEventKind.ExitThread when debugEvent.Thread is not null:
                var started = debugEvent.Kind == DebugEventKind.CreateThread;
                if (debugEvent.Thread.GetID(out var threadId) >= 0)
                {
                    _dispatcher.Post(() => NotifyThread((int)threadId, started));
                }

                break;
        }

        _ = controller.Continue(false);
    }

    // ---- Dispatch (engine thread) -----------------------------------------------------

    private void Dispatch(ICorDebugController controller, DebugEvent debugEvent)
    {
        bool keepStopped;
        try
        {
            keepStopped = Handle(debugEvent);
        }
        catch (Exception ex)
        {
            Log.Error($"Handling {debugEvent.Kind} failed", ex);
            keepStopped = false;
        }

        if (!keepStopped && debugEvent.Kind != DebugEventKind.ExitProcess)
        {
            var hr = controller.Continue(false);
            if (hr < 0 && hr != HResults.CORDBG_E_PROCESS_TERMINATED)
            {
                Log.Warn($"Continue after {debugEvent.Kind} failed (0x{hr:X8})");
            }
        }
    }

    /// <summary>Returns true when the debuggee should stay stopped.</summary>
    private bool Handle(DebugEvent e)
    {
        switch (e.Kind)
        {
            case DebugEventKind.CreateProcess:
                _process ??= e.Process;
                return false;
            case DebugEventKind.CreateAppDomain:
                _ = e.AppDomain?.Attach();
                return false;
            case DebugEventKind.LoadModule when e.Module is not null:
                var module = _modules.Register(e.Module, _options.JustMyCode);
                ConfigureModule(module);
                AfterModuleLoaded(module);
                return false;
            case DebugEventKind.UnloadModule when e.Module is not null:
                OnUnloadModule(e.Module);
                return false;
            case DebugEventKind.CreateThread or DebugEventKind.ExitThread when e.Thread is not null:
                if (e.Thread.GetID(out var threadId) >= 0)
                {
                    NotifyThread((int)threadId, e.Kind == DebugEventKind.CreateThread);
                }

                return false;
            case DebugEventKind.NameChange when e.Thread is not null && e.Thread.GetID(out var renamed) >= 0:
                _ = _threadNames.Remove((int)renamed);
                return false;
            case DebugEventKind.Breakpoint:
                return OnBreakpoint(e);
            case DebugEventKind.StepComplete:
                return OnStepComplete(e);
            case DebugEventKind.Break:
                return StopFor(StopReason.Pause, e.Thread, "Debugger.Break()");
            case DebugEventKind.ExceptionV2:
                return OnException(e);
            case DebugEventKind.LogMessage when e.Message is not null:
                _events.Output("console", e.Message);
                return false;
            case DebugEventKind.BreakpointSetError:
                Log.Warn($"BreakpointSetError: {e.ErrorCode}");
                return false;
            case DebugEventKind.ExitProcess:
                return OnExitProcess();
            default:
                return false;
        }
    }

    private void NotifyThread(int threadId, bool started)
    {
        if (started)
        {
            _ = _knownThreads.Add(threadId);
            _events.ThreadStarted(threadId);
        }
        else
        {
            _ = _knownThreads.Remove(threadId);
            _ = _threadNames.Remove(threadId);
            _events.ThreadExited(threadId);
        }
    }

    // ---- Modules ----------------------------------------------------------------------

    /// <summary>Just-My-Code and JIT settings; must run before the module's code is jitted.</summary>
    private void ConfigureModule(LoadedModule module)
    {
        if (module.Module is not ICorDebugModule2 module2)
        {
            return;
        }

        if (module.IsUserCode)
        {
            // Unoptimized code keeps every local alive and makes stepping line-accurate.
            // Fails harmlessly for faked LoadModule callbacks after attach.
            _ = module2.SetJITCompilerFlags(CorDebugJitCompilerFlags.DisableOptimization);
        }

        if (_options.JustMyCode)
        {
            unsafe
            {
                _ = module2.SetJMCStatus(module.IsUserCode, 0, null);
            }
        }
    }

    private void AfterModuleLoaded(LoadedModule module)
    {
        Log.Info($"Loaded {module.Name} (symbols: {module.Symbols is not null}, user: {module.IsUserCode})");
        _events.ModuleLoaded(ToView(module));
        foreach (var breakpoint in _breakpoints.OnModuleLoaded(module))
        {
            _events.BreakpointChanged(ToView(breakpoint));
        }

        if (_launchOptions is { StopAtEntry: true } && !_entryBreakpointSet && module.IsUserCode
            && module.Metadata is { EntryPointToken: not 0 })
        {
            SetEntryBreakpoint(module);
        }
    }

    /// <summary>Stops at the first user statement of Main (or the async Main / top-level statements body).</summary>
    private void SetEntryBreakpoint(LoadedModule module)
    {
        var metadata = module.Metadata!;
        var symbols = module.Symbols!;
        var token = metadata.EntryPointToken;

        // Async Main compiles to a synthesized "<Main>" that calls the user's Main.
        var name = metadata.GetMethodName(token);
        if (name.StartsWith('<') && !symbols.HasSequencePoints(token))
        {
            var declaring = metadata.GetDeclaringType(token);
            var userMain = metadata.FindMethod(declaring, name.Trim('<', '>'), metadata.GetParameterCount(token));
            if (userMain == 0)
            {
                userMain = metadata.FindMethod(declaring, "<Main>$", metadata.GetParameterCount(token));
            }

            if (userMain != 0)
            {
                token = userMain;
            }
        }

        token = symbols.GetMoveNextMethod(token) ?? token;
        if (symbols.GetFirstStatementOffset(token) is not { } offset)
        {
            return;
        }

        _entryBreakpoint = _breakpoints.AddInternal(module, token, offset);
        _entryBreakpointSet = _entryBreakpoint is not null;
    }

    private void OnUnloadModule(ICorDebugModule corModule)
    {
        using var module = _modules.Unregister(corModule);
        if (module is null)
        {
            return;
        }

        foreach (var breakpoint in _breakpoints.OnModuleUnloaded(module))
        {
            _events.BreakpointChanged(ToView(breakpoint));
        }

        _events.ModuleUnloaded(ToView(module));
    }

    // ---- Breakpoints ------------------------------------------------------------------

    private bool OnBreakpoint(DebugEvent e)
    {
        if (e.Breakpoint is null || e.Thread is null)
        {
            return false;
        }

        var hits = _breakpoints.Match(e.Breakpoint);
        if (hits.Count == 0)
        {
            return false;
        }

        if (_asyncStep is { } asyncStep && IndexOf(hits, asyncStep.Resume) >= 0)
        {
            return OnAsyncResume(e.Thread, asyncStep);
        }

        if (_asyncStepOut is { } asyncStepOut && IndexOf(hits, asyncStepOut.Notification) >= 0)
        {
            return OnAsyncStepOutNotification(e.Thread, asyncStepOut);
        }

        if (_entryBreakpoint is not null && IndexOf(hits, _entryBreakpoint) >= 0)
        {
            _breakpoints.Remove(_entryBreakpoint);
            _entryBreakpoint = null;
            return StopFor(StopReason.Entry, e.Thread, "Stopped at entry");
        }

        var hitIds = new List<int>();
        FrameScope? scope = null;
        var stopReason = StopReason.Breakpoint;
        foreach (var breakpoint in hits)
        {
            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
            {
                scope ??= CreateTopFrameScope(e.Thread);
                if (scope is not null)
                {
                    var hit = _expressions.EvaluateCondition(breakpoint.Condition, scope, out var error);
                    if (error is not null)
                    {
                        _events.Output("console", $"Breakpoint condition '{breakpoint.Condition}' failed: {error}\n");
                    }

                    if (!hit)
                    {
                        continue;
                    }
                }
            }

            breakpoint.HitCount++;
            if (!HitCondition.IsSatisfied(breakpoint.HitCondition, breakpoint.HitCount))
            {
                continue;
            }

            if (breakpoint.LogMessage is { } logMessage)
            {
                scope ??= CreateTopFrameScope(e.Thread);
                _events.Output("console", Interpolate(logMessage, scope) + "\n");
                continue;
            }

            if (breakpoint.Kind == BreakpointKind.Function)
            {
                stopReason = StopReason.FunctionBreakpoint;
            }

            hitIds.Add(breakpoint.Id);
        }

        if (hitIds.Count == 0)
        {
            _frames.Clear();
            return false;
        }

        return StopFor(stopReason, e.Thread, hitIds: hitIds);
    }

    private static int IndexOf(IReadOnlyList<UserBreakpoint> list, UserBreakpoint item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Expands <c>{expression}</c> placeholders in a logpoint message.</summary>
    private string Interpolate(string message, FrameScope? scope)
    {
        var builder = new StringBuilder();
        var i = 0;
        while (i < message.Length)
        {
            var open = message.IndexOf('{', i);
            if (open < 0)
            {
                _ = builder.Append(message, i, message.Length - i);
                break;
            }

            _ = builder.Append(message, i, open - i);
            var close = message.IndexOf('}', open + 1);
            if (close < 0)
            {
                _ = builder.Append(message, open, message.Length - open);
                break;
            }

            var expression = message[(open + 1)..close];
            if (scope is null)
            {
                _ = builder.Append("<unavailable>");
            }
            else
            {
                var outcome = _expressions.Evaluate(expression, scope);
                _ = builder.Append(outcome switch
                {
                    { Error: { } error } => "<" + error + ">",
                    { Value: { } value } => value.Target is ICorDebugStringValue s ? s.GetStringValue() : _inspector.Format(value),
                    _ => outcome.Constant is string text ? text : ValueInspector.FormatPrimitive(outcome.Constant, hex: false),
                });
            }

            i = close + 1;
        }

        return builder.ToString();
    }

    // ---- Exceptions -------------------------------------------------------------------

    private bool OnException(DebugEvent e)
    {
        var stop = e.ExceptionType switch
        {
            // With Just My Code, "thrown" means "thrown through user code".
            CorDebugExceptionCallbackType.FirstChance => _breakOnAllExceptions && !_options.JustMyCode,
            CorDebugExceptionCallbackType.UserFirstChance => _breakOnAllExceptions && _options.JustMyCode,
            CorDebugExceptionCallbackType.Unhandled => _breakOnUnhandledExceptions,
            _ => false,
        };

        if (!stop || e.Thread is null)
        {
            return false;
        }

        var unhandled = e.ExceptionType == CorDebugExceptionCallbackType.Unhandled;
        if (!unhandled && !_thrownFilter.IsEmpty && !_thrownFilter.Matches(CurrentExceptionTypeChain(e.Thread)))
        {
            return false;
        }

        _lastExceptionBreakMode = unhandled ? "unhandled" : "always";
        var (typeName, message) = DescribeCurrentException(e.Thread);
        var text = message is null ? typeName : $"{typeName}: {message}";
        return StopFor(
            StopReason.Exception,
            e.Thread,
            unhandled ? "Unhandled exception" : "Exception thrown",
            text);
    }

    private (string TypeName, string? Message) DescribeCurrentException(ICorDebugThread thread)
    {
        if (thread.GetCurrentException(out var exception) < 0 || exception is null)
        {
            return ("Exception", null);
        }

        var info = ValueInspector.Analyze(exception);
        return (_inspector.GetTypeName(info), ReadStringField(info, "_message"));
    }

    /// <summary>The thrown exception's type name and its base types' names, most derived first.</summary>
    private List<string> CurrentExceptionTypeChain(ICorDebugThread thread)
    {
        var names = new List<string>();
        if (thread.GetCurrentException(out var exception) >= 0 && exception is not null)
        {
            foreach (var type in _inspector.GetTypeChain(ValueInspector.Analyze(exception).Type))
            {
                names.Add(type.Name);
            }
        }

        return names;
    }

    private string? ReadStringField(ValueInfo info, string name) =>
        _inspector.FindField(info, name) is { } field
            && ValueInspector.Analyze(field).Target is ICorDebugStringValue s
            ? s.GetStringValue(4096)
            : null;

    public ExceptionView? GetExceptionInfo(int threadId)
    {
        RequireStopped();
        var thread = GetThread(threadId);
        if (thread.GetCurrentException(out var exception) < 0 || exception is null)
        {
            return null;
        }

        return DescribeException(thread, ValueInspector.Analyze(exception), depth: 0);
    }

    private ExceptionView DescribeException(ICorDebugThread thread, ValueInfo info, int depth)
    {
        // Plain field reads first; the StackTrace getter is a func-eval, after which
        // previously read values may be stale.
        var typeName = _inspector.GetTypeName(info);
        var message = ReadStringField(info, "_message");
        ExceptionView? inner = null;
        if (depth < 4 && _inspector.FindField(info, "_innerException") is { } innerValue)
        {
            var innerInfo = ValueInspector.Analyze(innerValue);
            if (!innerInfo.IsNull)
            {
                inner = DescribeException(thread, innerInfo, depth + 1);
            }
        }

        string? stackTrace = null;
        if (depth == 0 && thread.GetCurrentException(out var current) >= 0 && current is not null
            && _inspector.InvokeProperty(thread, ValueInspector.Analyze(current), "StackTrace") is { Succeeded: true, Value: { } traceValue }
            && ValueInspector.Analyze(traceValue).Target is ICorDebugStringValue trace)
        {
            stackTrace = trace.GetStringValue();
        }

        return new ExceptionView(typeName, _lastExceptionBreakMode)
        {
            Message = message,
            StackTrace = stackTrace,
            Inner = inner,
        };
    }

    // ---- Stepping ---------------------------------------------------------------------

    private bool StartStep(ICorDebugThread thread, StepKind kind)
    {
        _ = thread.GetActiveFrame(out var frame);
        ICorDebugStepper stepper;
        var hr = frame is not null ? frame.CreateStepper(out stepper) : thread.CreateStepper(out stepper);
        if (hr < 0)
        {
            Log.Warn($"CreateStepper failed (0x{hr:X8})");
            return false;
        }

        _ = stepper.SetUnmappedStopMask(CorDebugUnmappedStop.None);
        _ = stepper.SetInterceptMask(CorDebugIntercept.None);
        if (stepper is ICorDebugStepper2 stepper2)
        {
            _ = stepper2.SetJMC(_options.JustMyCode);
        }

        if (kind == StepKind.Out)
        {
            hr = stepper.StepOut();
        }
        else if (frame is ICorDebugILFrame ilFrame && TryGetStepRange(ilFrame) is var (start, end))
        {
            hr = stepper.StepRange(kind == StepKind.In, start, end);
            if (hr >= 0)
            {
                ArmAsyncStep(ilFrame, start, end);
            }
        }
        else
        {
            hr = stepper.Step(kind == StepKind.In);
        }

        if (hr < 0)
        {
            Log.Warn($"Step({kind}) failed (0x{hr:X8})");
            return false;
        }

        _stepper = stepper;
        _stepKind = kind;
        return true;
    }

    private (uint Start, uint End)? TryGetStepRange(ICorDebugILFrame frame)
    {
        if (frame.GetIP(out var offset, out _) < 0
            || frame.GetFunctionToken(out var token) < 0
            || frame.GetFunction(out var function) < 0
            || function.GetModule(out var corModule) < 0
            || _modules.Find(corModule)?.Symbols is not { } symbols
            || !symbols.HasSequencePoints(token)
            || function.GetILCode(out var code) < 0
            || code.GetSize(out var size) < 0)
        {
            return null;
        }

        return symbols.GetStepRange(token, offset, size);
    }

    private bool OnStepComplete(DebugEvent e)
    {
        _stepper = null;
        if (e.Thread is null)
        {
            return false;
        }

        if (_asyncStep is { } asyncStep)
        {
            if (!IsOnVisibleLineOfAsyncStep(e.Thread, asyncStep))
            {
                // The state machine yielded at the await (we are in its hidden epilogue or in
                // the caller): let it run until the resume breakpoint is hit.
                return false;
            }

            // The await completed synchronously; this is an ordinary step.
            DisarmAsyncStep();
        }

        // Landed in a hidden sequence point or in code without symbols: keep going.
        if (_options.StepFiltering && _stepAttempts < MaxStepAttempts && NextStepKind(e.Thread) is { } again)
        {
            _stepAttempts++;
            if (StartStep(e.Thread, again))
            {
                return false;
            }
        }

        _stepAttempts = 0;
        return StopFor(StopReason.Step, e.Thread);
    }

    /// <summary>Null when the thread is on a user statement; otherwise how to keep stepping.</summary>
    private StepKind? NextStepKind(ICorDebugThread thread)
    {
        if (thread.GetActiveFrame(out var frame) < 0 || frame is not ICorDebugILFrame ilFrame)
        {
            return StepKind.Out;
        }

        if (ilFrame.GetIP(out var offset, out _) < 0
            || ilFrame.GetFunctionToken(out var token) < 0
            || ilFrame.GetFunction(out var function) < 0
            || function.GetModule(out var corModule) < 0)
        {
            return null;
        }

        var module = _modules.Find(corModule);
        if (module?.Symbols is not { } symbols || !symbols.HasSequencePoints(token))
        {
            return _options.JustMyCode ? StepKind.Out : null;
        }

        return symbols.IsHiddenOffset(token, offset)
            ? (_stepKind == StepKind.Out ? StepKind.Over : _stepKind)
            : null;
    }

    private void CancelStepper()
    {
        if (_stepper is not null)
        {
            _ = _stepper.Deactivate();
            _stepper = null;
        }

        DisarmAsyncStep();
    }

    // ---- Async stepping ---------------------------------------------------------------

    private void ArmAsyncStep(ICorDebugILFrame frame, uint start, uint end)
    {
        if (frame.GetFunctionToken(out var token) < 0
            || frame.GetFunction(out var function) < 0
            || function.GetModule(out var corModule) < 0
            || _modules.Find(corModule) is not { Symbols: { } symbols } module)
        {
            return;
        }

        foreach (var point in symbols.GetAwaitPoints(token))
        {
            if (point.YieldOffset < start || point.YieldOffset >= end)
            {
                continue;
            }

            var resume = _breakpoints.AddInternal(module, point.ResumeMethodToken, point.ResumeOffset);
            if (resume is null)
            {
                return;
            }

            // Pin the state machine so the resumed frame can be matched to this invocation.
            ICorDebugHandleValue? stateMachine = null;
            if (frame.GetArgument(0, out var self) >= 0
                && ValueInspector.Analyze(self).Heap is ICorDebugHeapValue2 heap
                && heap.CreateHandle(CorDebugHandleType.Strong, out var handle) >= 0)
            {
                stateMachine = handle;
            }

            _asyncStep = new AsyncStep(resume, stateMachine, point.ResumeMethodToken);
            return;
        }
    }

    private bool OnAsyncResume(ICorDebugThread thread, AsyncStep asyncStep)
    {
        if (!IsSameStateMachine(thread, asyncStep))
        {
            return false; // another invocation of the same async method
        }

        DisarmAsyncStep();

        // Resumed mid-statement (after the await): finish it with an ordinary step over.
        _stepAttempts = 0;
        return !StartStep(thread, StepKind.Over) && StopFor(StopReason.Step, thread);
    }

    private static bool IsOnVisibleLineOfAsyncStep(ICorDebugThread thread, AsyncStep asyncStep) =>
        thread.GetActiveFrame(out var frame) >= 0
        && frame is ICorDebugILFrame il
        && il.GetFunctionToken(out var token) >= 0
        && token == asyncStep.MethodToken
        && il.GetIP(out var offset, out _) >= 0
        && asyncStep.Resume.Bound.Count > 0
        && asyncStep.Resume.Bound[0].Module.Symbols is { } symbols
        && !symbols.IsHiddenOffset(token, offset)
        && IsSameStateMachine(thread, asyncStep);

    private static bool IsSameStateMachine(ICorDebugThread thread, AsyncStep asyncStep)
    {
        if (asyncStep.StateMachine is null)
        {
            return true;
        }

        if (thread.GetActiveFrame(out var frame) < 0 || frame is not ICorDebugILFrame il || il.GetArgument(0, out var self) < 0)
        {
            return false;
        }

        var current = ValueInspector.Analyze(self).Target;
        var expected = ValueInspector.Analyze(asyncStep.StateMachine).Target;
        return current is not null && expected is not null
            && current.GetAddress(out var currentAddress) >= 0
            && expected.GetAddress(out var expectedAddress) >= 0
            && currentAddress == expectedAddress;
    }

    private bool TryStartAsyncStepOut(ICorDebugThread thread, int threadId)
    {
        var frames = WalkStack(thread, threadId);
        if (frames.Count == 0 || frames[0].Module?.Symbols?.GetKickoffMethod(frames[0].MethodToken) is null)
        {
            return false;
        }

        // Still running synchronously under its caller: an ordinary step out gets there.
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].IsUserCode && frames[i].Location is not null)
            {
                return false;
            }
        }

        if (frames[0].Frame is not ICorDebugILFrame il || il.GetArgument(0, out var self) < 0
            || GetAsyncTask(ValueInspector.Analyze(self)) is not { Heap: ICorDebugHeapValue2 heap }
            || heap.CreateHandle(CorDebugHandleType.Strong, out var task) < 0)
        {
            return false;
        }

        var coreLibModule = _inspector.FindCoreLib();
        var coreLib = coreLibModule?.Metadata;
        var notify = coreLib is null ? 0 : coreLib.FindMethod(coreLib.FindType("System.Threading.Tasks.Task"), "NotifyDebuggerOfWaitCompletion", 0);
        if (coreLibModule is null || notify == 0
            || !TryCreateBoolean(thread, value: true, out var enable)
            || _inspector.InvokeMethod(thread, ValueInspector.Analyze(task), "SetNotificationForWaitCompletion", [enable]) is not { Succeeded: true }
            || _breakpoints.AddInternal(coreLibModule, notify, 0) is not { } notification)
        {
            _ = task.Dispose();
            return false;
        }

        _asyncStepOut = new AsyncStepOut(notification, task);
        return true;
    }

    private static unsafe bool TryCreateBoolean(ICorDebugThread thread, bool value, out ICorDebugValue result)
    {
        result = null!;
        if (FuncEvaluator.CreatePrimitive(thread, CorElementType.Boolean) is not ICorDebugGenericValue generic)
        {
            return false;
        }

        ulong bits = value ? 1UL : 0UL;
        result = generic;
        return generic.SetValue(&bits) >= 0;
    }

    private bool OnAsyncStepOutNotification(ICorDebugThread thread, AsyncStepOut asyncStepOut)
    {
        // NotifyDebuggerOfWaitCompletion runs on every task with notifications on; match ours.
        if (thread.GetActiveFrame(out var frame) < 0 || frame is not ICorDebugILFrame il || il.GetArgument(0, out var self) < 0
            || ValueInspector.Analyze(self).Target is not { } current
            || ValueInspector.Analyze(asyncStepOut.Task).Target is not { } expected
            || current.GetAddress(out var currentAddress) < 0 || expected.GetAddress(out var expectedAddress) < 0
            || currentAddress != expectedAddress)
        {
            return false;
        }

        DisarmAsyncStep();

        // Just My Code step out of the framework lands in the awaiting caller.
        _stepAttempts = 0;
        return !StartStep(thread, StepKind.Out) && StopFor(StopReason.Step, thread);
    }

    private void DisarmAsyncStep()
    {
        if (_asyncStepOut is { } asyncStepOut)
        {
            _asyncStepOut = null;
            _breakpoints.Remove(asyncStepOut.Notification);
            _ = asyncStepOut.Task.Dispose();
        }

        if (_asyncStep is { } asyncStep)
        {
            _asyncStep = null;
            _breakpoints.Remove(asyncStep.Resume);
            _ = asyncStep.StateMachine?.Dispose();
        }
    }
}
