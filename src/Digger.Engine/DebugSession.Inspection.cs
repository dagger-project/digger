using System;
using System.Collections.Generic;
using Digger.Engine.Evaluation;
using Digger.Engine.Infrastructure;
using Digger.Engine.Inspection;
using Digger.Engine.Runtime;
using Digger.Engine.Symbols;
using Digger.Interop.CorDebug;

namespace Digger.Engine;

/// <summary>A managed stack frame captured while stopped.</summary>
internal sealed class FrameEntry
{
    public int Id { get; set; }

    public required int ThreadId { get; init; }

    public required int Depth { get; init; }

    /// <summary>The physical frame; null for logical async frames and labels.</summary>
    public ICorDebugFrame? Frame { get; set; }

    /// <summary>For a logical async frame: the awaiting state machine (its fields hold the locals).</summary>
    public IValueSource? StateMachine { get; init; }

    /// <summary>A separator such as "[Async Call Stack]".</summary>
    public bool IsLabel { get; init; }

    public LoadedModule? Module { get; init; }

    public uint MethodToken { get; init; }

    public uint ILOffset { get; init; }

    public SourceLocation? Location { get; init; }

    public required string Name { get; init; }

    public bool IsUserCode { get; init; }

    /// <summary>Func-eval generation the <see cref="Frame"/> object belongs to.</summary>
    public int Generation { get; set; }
}

/// <summary>Frames captured during the current stop. Cleared on resume.</summary>
internal sealed class FrameStore
{
    private readonly Dictionary<int, List<FrameEntry>> _byThread = [];
    private readonly List<FrameEntry> _byId = [];

    public bool TryGetThread(int threadId, out List<FrameEntry> frames) =>
        _byThread.TryGetValue(threadId, out frames!);

    public void AddThread(int threadId, List<FrameEntry> frames)
    {
        foreach (var frame in frames)
        {
            _byId.Add(frame);
            frame.Id = _byId.Count;
        }

        _byThread[threadId] = frames;
    }

    public FrameEntry? Get(int id) => id > 0 && id <= _byId.Count ? _byId[id - 1] : null;

    /// <summary>Suspended async methods found by a heap walk during this stop, if one ran.</summary>
    public List<TaskView>? Tasks { get; set; }

    public void Clear()
    {
        _byThread.Clear();
        _byId.Clear();
        Tasks = null;
    }
}

/// <content>Stack traces, variables and expression evaluation.</content>
public sealed partial class DebugSession
{
    private const int MaxFrames = 2000;

    // ---- Stack ------------------------------------------------------------------------

    public (List<FrameView> Frames, int Total) GetStackTrace(int threadId, int startFrame, int levels)
    {
        RequireStopped();
        var frames = GetFrames(threadId);
        var start = Math.Clamp(startFrame, 0, frames.Count);
        var count = levels <= 0 ? frames.Count - start : Math.Min(levels, frames.Count - start);
        var result = new List<FrameView>(count);
        for (var i = start; i < start + count; i++)
        {
            var frame = frames[i];
            result.Add(new FrameView(frame.Id, frame.Name)
            {
                SourcePath = frame.Location is { } location ? _sources.Resolve(frame.Module, location.Document) : null,
                Line = frame.Location?.StartLine ?? 0,
                Column = frame.Location?.StartColumn ?? 0,
                EndLine = frame.Location?.EndLine ?? 0,
                EndColumn = frame.Location?.EndColumn ?? 0,
                IsUserCode = frame.IsUserCode,
                ModuleId = frame.Module?.Id,
                IsLabel = frame.IsLabel,
            });
        }

        return (result, frames.Count);
    }

    private List<FrameEntry> GetFrames(int threadId)
    {
        if (_frames.TryGetThread(threadId, out var cached))
        {
            return cached;
        }

        if (threadId < 0)
        {
            // A task's logical stack (see GetAsyncTasks); found by the heap walk.
            _ = GetAsyncTasks(userCodeOnly: false);
            return _frames.TryGetThread(threadId, out var task) ? task : throw new InvalidOperationException($"There is no task {-threadId}.");
        }

        var frames = WalkStack(GetThread(threadId), threadId);
        frames.AddRange(GetAsyncCallers(frames, threadId));
        _frames.AddThread(threadId, frames);
        return frames;
    }

    private List<FrameEntry> WalkStack(ICorDebugThread thread, int threadId)
    {
        var frames = new List<FrameEntry>();
        if (thread is not ICorDebugThread3 thread3 || thread3.CreateStackWalk(out var walk) < 0)
        {
            return frames;
        }

        var depth = 0;
        while (frames.Count < MaxFrames)
        {
            if (walk.GetFrame(out var frame) == HResults.S_OK && frame is not null && Describe(frame, threadId, depth) is { } entry)
            {
                frames.Add(entry);
                depth++;
            }

            if (walk.Next() != HResults.S_OK)
            {
                break;
            }
        }

        return frames;
    }

    private FrameEntry? Describe(ICorDebugFrame frame, int threadId, int depth)
    {
        if (frame is not ICorDebugILFrame ilFrame
            || frame.GetFunctionToken(out var token) < 0
            || frame.GetFunction(out var function) < 0
            || function.GetModule(out var corModule) < 0)
        {
            return null;
        }

        _ = ilFrame.GetIP(out var offset, out _);
        var module = _modules.Find(corModule);
        if (_options.SymbolServer && module is { Metadata: { Symbols: null } metadata0 } && _symbolLookups.Add(module.BaseAddress)
            && SymbolServer.TryGet(metadata0) is { } pdb)
        {
            _ = metadata0.AttachSymbols(pdb);
        }

        var metadata = module?.Metadata;
        var name = metadata is null
            ? $"[{module?.Name ?? "unknown"}] 0x{token:X8}"
            : metadata.GetMethodDisplayName(token);

        return new FrameEntry
        {
            ThreadId = threadId,
            Depth = depth,
            Frame = frame,
            Module = module,
            MethodToken = token,
            ILOffset = offset,
            Location = module?.Symbols?.GetLocation(token, offset),
            Name = name,
            IsUserCode = module?.IsUserCode ?? false,
            Generation = _funcEval.Generation,
        };
    }

    /// <summary>
    /// The IL frame for <paramref name="entry"/>. ICorDebugFrame objects do not survive a
    /// func-eval (which continues the process), so they are re-acquired by depth.
    /// </summary>
    private ICorDebugILFrame? GetILFrame(FrameEntry entry)
    {
        if (entry.Frame is null)
        {
            return null;
        }

        if (entry.Generation != _funcEval.Generation)
        {
            var fresh = WalkStack(GetThread(entry.ThreadId), entry.ThreadId);
            if (entry.Depth < fresh.Count && fresh[entry.Depth].MethodToken == entry.MethodToken)
            {
                entry.Frame = fresh[entry.Depth].Frame;
                entry.Generation = _funcEval.Generation;
            }
        }

        return entry.Frame as ICorDebugILFrame;
    }

    // ---- Scopes and variables ---------------------------------------------------------

    public List<ScopeView> GetScopes(int frameId)
    {
        RequireStopped();
        var frame = _frames.Get(frameId) ?? throw new InvalidOperationException($"Unknown frame {frameId}.");
        return [new ScopeView("Locals", _variables.Add(new FrameLocalsContainer(this, frame)))];
    }

    /// <summary>
    /// Parameter names of the frame's method, in order (for trace output). Async and iterator
    /// methods report none: their arguments live on in the state machine as locals.
    /// </summary>
    public string[] GetParameterNames(int frameId)
    {
        RequireStopped();
        var frame = _frames.Get(frameId) ?? throw new InvalidOperationException($"Unknown frame {frameId}.");
        return frame.StateMachine is null && frame.Module?.Metadata is { } metadata ? metadata.GetParameterNames(frame.MethodToken) : [];
    }

    public List<VariableView> GetVariables(int reference, int start, int count, bool hex)
    {
        RequireStopped();
        var container = _variables.Get(reference) ?? throw new InvalidOperationException("The variable is no longer valid.");
        var context = ContextOf(container);

        // Each child is formatted as soon as it is produced: a later property getter
        // (func-eval) may invalidate values read before it. [DebuggerDisplay] runs code too,
        // so it is evaluated afterwards, from each value's pinned source.
        var result = new List<VariableView>();
        var displays = new List<(int Index, PendingDisplay Display)>();
        foreach (var child in container.GetChildren(start, count))
        {
            var (view, display) = ToView(child, hex, context);
            if (display is not null)
            {
                displays.Add((result.Count, display));
            }

            result.Add(view);
        }

        foreach (var (index, display) in displays)
        {
            result[index] = result[index] with { Value = EvaluateDisplay(display, result[index].Value) };
        }

        if (container.SortByName)
        {
            // Stable: groups ("Static members", "Raw View") keep their trailing position.
            var ordered = new List<VariableView>(result.Count);
            ordered.AddRange(result.FindAll(static v => v.Kind is not (VariableKind.Class or VariableKind.Virtual) || v.Reference == 0));
            ordered.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            ordered.AddRange(result.FindAll(static v => v.Kind is VariableKind.Class or VariableKind.Virtual && v.Reference != 0));
            result = ordered;
        }

        return result;
    }

    /// <summary>A <c>[DebuggerDisplay]</c> still to be evaluated for a formatted view.</summary>
    private sealed record PendingDisplay(string Format, IValueSource Source, ICorDebugThread Thread, bool Hex);

    private string EvaluateDisplay(PendingDisplay display, string fallback)
    {
        try
        {
            return _inspector.FormatDisplay(display.Format, display.Source, display.Thread, display.Hex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Interop.CorDebug.CorDebugException or InvalidCastException)
        {
            Log.Warn($"DebuggerDisplay '{display.Format}' failed: {ex.Message}");
            return fallback;
        }
    }

    private (VariableView View, PendingDisplay? Display) ToView(NamedValue child, bool hex, EvalContext context)
    {
        if (child.Group is { } group)
        {
            return (new VariableView(child.Name, child.Text ?? string.Empty)
            {
                Reference = _variables.Add(new ContextContainer(group, context)),
                IndexedCount = group.IndexedCount,
                Kind = child.Kind,
                IsReadOnly = true,
                IsLazy = child.IsLazy,
                EvaluateName = child.EvaluateName,
            }, null);
        }

        if (child.Value is null)
        {
            return (new VariableView(child.Name, child.Text ?? "null") { Kind = child.Kind, IsReadOnly = true }, null);
        }

        return ToView(child.Name, ValueInspector.Analyze(child.Value), hex, context, child.EvaluateName, child.Kind);
    }

    private (VariableView View, PendingDisplay? Display) ToView(string name, ValueInfo info, bool hex, EvalContext context, string? evaluateName, VariableKind kind)
    {
        // Pin (create the source) first, while the value is certainly valid.
        var source = IsExpandable(info) ? CreateSource(info, evaluateName, context) : null;
        var container = source is null ? null : _inspector.CreateContainer(info, evaluateName, context.Thread, source);
        var view = new VariableView(name, _inspector.Format(info, hex))
        {
            Type = _inspector.GetTypeName(info),
            Reference = container is null ? 0 : _variables.Add(new ContextContainer(container, context)),
            IndexedCount = container?.IndexedCount ?? 0,
            EvaluateName = evaluateName,
            Kind = kind,
        };

        var display = source is not null && context.Thread is not null && _inspector.GetDisplayFormat(info) is { } format
            ? new PendingDisplay(format, source, context.Thread, hex)
            : null;
        return (view, display);
    }

    private static bool IsExpandable(ValueInfo info) =>
        !info.IsNull && !info.IsPrimitive && info.ElementType is not (CorElementType.String or CorElementType.Ptr or CorElementType.FnPtr);

    /// <summary>
    /// How a container re-obtains its value. Heap objects are pinned with a GC handle right
    /// away, while the value is known to be valid; value types are re-evaluated from their
    /// expression if a func-eval has run since they were read.
    /// </summary>
    private IValueSource CreateSource(ValueInfo info, string? expression, EvalContext context) =>
        info.Heap is not null
            ? _variables.Pin(info)
            : new ExpressionSource(this, info, expression, context);

    private sealed class ExpressionSource(DebugSession session, ValueInfo info, string? expression, EvalContext context) : IValueSource
    {
        private readonly int _generation = session._funcEval.Generation;

        public ICorDebugValue? Resolve()
        {
            if (session._funcEval.Generation == _generation || expression is null || context.Frame is null || context.Thread is null)
            {
                return info.Raw;
            }

            var outcome = session._expressions.Evaluate(expression, new FrameScope(session, context.Frame, context.Thread));
            return outcome.Value?.Raw ?? info.Raw;
        }
    }

    /// <summary>Thread and frame a container's children are evaluated in.</summary>
    private readonly record struct EvalContext(ICorDebugThread? Thread, FrameEntry? Frame);

    private EvalContext ContextOf(IVariableContainer container) => container switch
    {
        FrameLocalsContainer locals => new EvalContext(TryGetThread(locals.Frame.ThreadId), locals.Frame),
        ContextContainer bound => bound.Context,
        _ => new EvalContext(TryGetThread(_lastStoppedThreadId), null),
    };

    private ICorDebugThread? TryGetThread(int threadId) =>
        _process is not null && _process.GetThread((uint)(threadId < 0 ? _lastStoppedThreadId : threadId), out var thread) >= 0 ? thread : null;

    /// <summary>Remembers the thread and frame a container belongs to.</summary>
    private sealed class ContextContainer(IVariableContainer inner, EvalContext context) : IVariableContainer
    {
        public EvalContext Context => context;

        public int IndexedCount => inner.IndexedCount;

        public bool SortByName => inner.SortByName;

        public IEnumerable<NamedValue> GetChildren(int start, int count) => inner.GetChildren(start, count);
    }

    /// <summary>Locals, arguments and <c>this</c> of a frame.</summary>
    private sealed class FrameLocalsContainer(DebugSession session, FrameEntry frame) : IVariableContainer
    {
        public FrameEntry Frame => frame;

        public int IndexedCount => 0;

        public IEnumerable<NamedValue> GetChildren(int start, int count) => session.GetFrameVariables(frame, includeException: true);
    }

    /// <summary>
    /// Everything named in a frame, as the user wrote it: compiler-generated closures and
    /// async state machines are flattened back into plain locals.
    /// </summary>
    private List<NamedValue> GetFrameVariables(FrameEntry frame, bool includeException)
    {
        var result = new List<NamedValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Logical async frame: the suspended method's locals live in its state machine.
        if (frame.StateMachine is { } stateMachine)
        {
            if (stateMachine.Resolve() is { } value)
            {
                Flatten(value, depth: 0);
            }

            return result;
        }

        if (GetILFrame(frame) is not { } il || frame.Module?.Metadata is not { } metadata)
        {
            return result;
        }

        if (includeException && frame.Depth == 0 && TryGetThread(frame.ThreadId) is { } thread
            && thread.GetCurrentException(out var exception) >= 0 && exception is not null)
        {
            Add("$exception", exception, VariableKind.Virtual);
        }

        var isStatic = metadata.IsStatic(frame.MethodToken);
        if (!isStatic && il.GetArgument(0, out var self) >= 0)
        {
            var declaringType = metadata.GetTypeName(metadata.GetDeclaringType(frame.MethodToken));
            if (IsCompilerGeneratedType(declaringType))
            {
                Flatten(self, depth: 0);
            }
            else
            {
                Add("this", self, VariableKind.Data);
            }
        }

        var parameterNames = metadata.GetParameterNames(frame.MethodToken);
        for (var i = 0; i < parameterNames.Length; i++)
        {
            if (il.GetArgument((uint)(i + (isStatic ? 0 : 1)), out var argument) >= 0)
            {
                Add(parameterNames[i], argument, VariableKind.Data);
            }
        }

        if (frame.Module.Symbols is { } symbols)
        {
            foreach (var local in symbols.GetLocals(frame.MethodToken, frame.ILOffset))
            {
                if (il.GetLocalVariable((uint)local.Index, out var value) < 0)
                {
                    continue; // not live at this IP
                }

                if (local.Name.StartsWith("CS$<>", StringComparison.Ordinal))
                {
                    Flatten(value, depth: 0);
                }
                else
                {
                    Add(local.Name, value, VariableKind.Data);
                }
            }
        }

        return result;

        void Add(string name, ICorDebugValue value, VariableKind kind)
        {
            if (seen.Add(name))
            {
                result.Add(new NamedValue(name) { Value = value, EvaluateName = name, Kind = kind });
            }
        }

        // Display classes (lambdas) and state machines (async/iterators) hold user variables as fields.
        void Flatten(ICorDebugValue container, int depth)
        {
            var info = ValueInspector.Analyze(container);
            if (info.IsNull || info.Target is not ICorDebugObjectValue obj || depth > 4)
            {
                return;
            }

            foreach (var level in _inspector.GetTypeChain(info.Type))
            {
                foreach (var field in level.Module.Metadata!.GetFields(level.Token))
                {
                    if (field.IsStatic || field.IsLiteral || obj.GetFieldValue(level.Class, field.Token, out var value) < 0)
                    {
                        continue;
                    }

                    var name = field.Name;
                    if (name == "<>4__this")
                    {
                        var thisInfo = ValueInspector.Analyze(value);
                        if (thisInfo.Type is not null && _inspector.Resolve(thisInfo) is { } thisType && IsCompilerGeneratedType(thisType.Name))
                        {
                            Flatten(value, depth + 1);
                        }
                        else
                        {
                            Add("this", value, VariableKind.Data);
                        }
                    }
                    else if (name.StartsWith("CS$<>8__locals", StringComparison.Ordinal) || name.StartsWith("<>8__", StringComparison.Ordinal))
                    {
                        Flatten(value, depth + 1);
                    }
                    else if (name.StartsWith('<') && name.Contains(">5__", StringComparison.Ordinal))
                    {
                        Add(name[1..name.IndexOf('>', StringComparison.Ordinal)], value, VariableKind.Data);
                    }
                    else if (!name.StartsWith('<'))
                    {
                        Add(name, value, VariableKind.Data);
                    }
                }
            }
        }
    }

    // ---- Async call stacks ------------------------------------------------------------

    /// <summary>
    /// When the outermost user frame is an async method resumed by the thread pool, its
    /// awaiting callers are not on the physical stack. They are found by following the
    /// method's task: <c>m_continuationObject</c> points at the awaiting method's state
    /// machine box (directly, through a delegate, or through a continuation wrapper), whose
    /// <c>&lt;&gt;1__state</c> says at which await it is suspended.
    /// </summary>
    private List<FrameEntry> GetAsyncCallers(List<FrameEntry> physical, int threadId)
    {
        var result = new List<FrameEntry>();
        var outermost = physical.FindLast(static f => f.IsUserCode && f.Location is not null);
        if (outermost?.Module?.Symbols?.GetKickoffMethod(outermost.MethodToken) is null
            || GetILFrame(outermost) is not { } il || il.GetArgument(0, out var self) < 0
            || GetAsyncTask(ValueInspector.Analyze(self)) is not { } task)
        {
            return result;
        }

        var callers = FollowContinuations(task, threadId);
        if (callers.Count > 0)
        {
            result.Add(new FrameEntry { ThreadId = threadId, Depth = -1, Name = "[Async Call Stack]", IsLabel = true });
            result.AddRange(callers);
        }

        return result;
    }

    /// <summary>The async methods awaiting the task <paramref name="box"/>, innermost first.</summary>
    private List<FrameEntry> FollowContinuations(ValueInfo box, int threadId)
    {
        var result = new List<FrameEntry>();
        for (var depth = 0; depth < 64; depth++)
        {
            if (_inspector.FindField(box, "m_continuationObject") is not { } continuation
                || FindStateMachineBox(ValueInspector.Analyze(continuation), 0) is not { } next
                || _inspector.FindField(next, "StateMachine") is not { } stateMachineValue
                || DescribeAsyncFrame(ValueInspector.Analyze(stateMachineValue), threadId) is not { } frame)
            {
                break;
            }

            result.Add(frame);
            box = next;
        }

        return result;
    }

    /// <summary>A logical frame for a suspended async method: where its state says it awaits, with its locals.</summary>
    private FrameEntry? DescribeAsyncFrame(ValueInfo stateMachine, int threadId, SourceLocation? where = null)
    {
        if (_inspector.Resolve(stateMachine) is not { Module.Metadata: { } metadata } type)
        {
            return null;
        }

        var moveNext = metadata.FindMethod(type.Token, "MoveNext", 0);
        if (moveNext == 0)
        {
            return null;
        }

        var location = where;
        if (location is null && type.Module.Symbols is { } symbols
            && _inspector.FindField(stateMachine, "<>1__state") is { } stateValue
            && ValueInspector.ReadPrimitive(stateValue, CorElementType.I4) is int state)
        {
            // Roslyn numbers await states in source order, matching the PDB's await list.
            var awaits = symbols.GetAwaitPoints(moveNext);
            location = state >= 0 && state < awaits.Length
                ? symbols.GetLocation(moveNext, awaits[state].YieldOffset)
                : symbols.GetLocation(moveNext, 0);
        }

        return new FrameEntry
        {
            ThreadId = threadId,
            Depth = -1,
            Module = type.Module,
            MethodToken = moveNext,
            Location = location,
            Name = metadata.GetMethodDisplayName(moveNext),
            IsUserCode = type.Module.IsUserCode,
            StateMachine = _variables.Pin(stateMachine),
        };
    }

    /// <summary>The task of an async state machine: its builder's <c>m_task</c>.</summary>
    private ValueInfo? GetAsyncTask(ValueInfo stateMachine)
    {
        if (_inspector.FindField(stateMachine, "<>t__builder") is not { } builderValue)
        {
            return null; // iterators have no builder
        }

        // AsyncTaskMethodBuilder<T>.m_task, or AsyncTaskMethodBuilder.m_builder.m_task.
        var builder = ValueInspector.Analyze(builderValue);
        var taskValue = _inspector.FindField(builder, "m_task")
            ?? (_inspector.FindField(builder, "m_builder") is { } inner ? _inspector.FindField(ValueInspector.Analyze(inner), "m_task") : null);
        return taskValue is null ? null : ValueInspector.Analyze(taskValue) is { IsNull: false } task ? task : null;
    }

    /// <summary>Unwraps a task continuation to the awaiting method's AsyncStateMachineBox.</summary>
    private ValueInfo? FindStateMachineBox(ValueInfo continuation, int depth)
    {
        if (continuation.IsNull || depth > 4 || _inspector.Resolve(continuation) is not { } type)
        {
            return null;
        }

        if (_inspector.FindField(continuation, "StateMachine") is not null)
        {
            return continuation;
        }

        // Action delegate (MoveNextAction) → its target is the box; AwaitTaskContinuation
        // and friends keep the delegate in m_action; several continuations come as a List.
        var inner = _inspector.FindField(continuation, "_target")
            ?? _inspector.FindField(continuation, "m_action")
            ?? (type.Name == "System.Collections.Generic.List" && _inspector.FindField(continuation, "_items") is { } items
                && ValueInspector.Analyze(items).Target is ICorDebugArrayValue array && array.GetElementAtPosition(0, out var first) >= 0
                    ? first
                    : null);
        return inner is null ? null : FindStateMachineBox(ValueInspector.Analyze(inner), depth + 1);
    }

    private static bool IsCompilerGeneratedType(string typeName)
    {
        var lastDot = typeName.LastIndexOf('.');
        var simple = lastDot < 0 ? typeName : typeName[(lastDot + 1)..];
        return simple.StartsWith('<');
    }

    // ---- Evaluation -------------------------------------------------------------------

    /// <summary>Evaluates an expression in a frame (hover, watch, REPL).</summary>
    public VariableView Evaluate(string expression, int? frameId, bool hex)
    {
        RequireStopped();
        var frame = frameId is { } id ? _frames.Get(id) : null;
        if (frame is null)
        {
            var frames = GetFrames(_lastStoppedThreadId);
            frame = frames.Count > 0 ? frames[0] : throw new InvalidOperationException("No frame to evaluate in.");
        }

        var scope = new FrameScope(this, frame, GetThread(frame.ThreadId));
        var outcome = _expressions.Evaluate(expression, scope);
        if (outcome.Error is { } error)
        {
            throw new InvalidOperationException(error);
        }

        if (outcome.Value is { } value)
        {
            var (view, display) = ToView(expression, value, hex, new EvalContext(scope.Thread, frame), expression, VariableKind.Data);
            return display is null ? view : view with { Value = EvaluateDisplay(display, view.Value) };
        }

        if (outcome.Constant is VoidResult)
        {
            return new VariableView(expression, "void") { Type = "void", IsReadOnly = true };
        }

        return new VariableView(expression, ValueInspector.FormatPrimitive(outcome.Constant, hex))
            {
                Type = outcome.Constant?.GetType().Name,
                IsReadOnly = true,
            };
    }

    /// <summary>
    /// Delve's <c>call</c>: evaluates like <see cref="Evaluate"/>, but the whole program runs
    /// while the code executes (so it may wait on other threads) and there is no time limit;
    /// <see cref="AbortEvaluation"/> stops it.
    /// </summary>
    public VariableView Call(string expression, int? frameId, bool hex)
    {
        var (timeout, allThreads) = (_funcEval.Timeout, _funcEval.RunAllThreads);
        _funcEval.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        _funcEval.RunAllThreads = true;
        try
        {
            return Evaluate(expression, frameId, hex);
        }
        finally
        {
            (_funcEval.Timeout, _funcEval.RunAllThreads) = (timeout, allThreads);
        }
    }

    /// <summary>Any thread: aborts the code a func-eval is running, e.g. a <see cref="Call"/> that hangs.</summary>
    public void AbortEvaluation() => _funcEval.RequestAbort();

    /// <summary>Assigns a new value to a child of a variables container.</summary>
    public VariableView SetVariable(int reference, string name, string value, bool hex)
    {
        RequireStopped();
        var container = _variables.Get(reference) ?? throw new InvalidOperationException("The variable is no longer valid.");
        var context = ContextOf(container);
        var child = FindChild(container, name) ?? throw new InvalidOperationException($"'{name}' was not found.");
        if (child.Value is null)
        {
            throw new InvalidOperationException($"'{name}' cannot be assigned.");
        }

        var error = _inspector.TryAssign(child.Value, value, context.Thread);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        // Re-read so the view reflects what the debuggee now holds.
        var (view, display) = ToView(FindChild(container, name) ?? child, hex, context);
        return display is null ? view : view with { Value = EvaluateDisplay(display, view.Value) };

        static NamedValue? FindChild(IVariableContainer container, string name)
        {
            foreach (var candidate in container.GetChildren(0, 0))
            {
                if (candidate.Name == name)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    // ---- Completions ------------------------------------------------------------------

    /// <summary>Debug console completions at <paramref name="cursor"/> (0-based) in <paramref name="text"/>.</summary>
    public List<CompletionView> GetCompletions(int? frameId, string text, int cursor)
    {
        RequireStopped();
        cursor = Math.Clamp(cursor, 0, text.Length);
        var start = cursor;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }

        var partial = text[start..cursor];
        var frame = frameId is { } id ? _frames.Get(id) : null;
        if (frame is null && GetFrames(_lastStoppedThreadId) is { Count: > 0 } frames)
        {
            frame = frames[0];
        }

        if (frame is null || TryGetThread(frame.ThreadId) is not { } thread)
        {
            return [];
        }

        var scope = new FrameScope(this, frame, thread);
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (start > 0 && text[start - 1] == '.')
        {
            var expression = text[ExpressionStart(text, start - 1)..(start - 1)];
            // Completing must not call methods; property getters are as far as it goes.
            if (expression.Length > 0 && !expression.Contains('(', StringComparison.Ordinal)
                && _expressions.Evaluate(expression, scope) is { Value: { IsNull: false } target })
            {
                AddMembers(target);
            }
        }
        else
        {
            foreach (var variable in GetFrameVariables(frame, includeException: true))
            {
                _ = candidates.TryAdd(variable.Name, "variable");
            }

            if (scope.Lookup("this") is { } self)
            {
                AddMembers(ValueInspector.Analyze(self));
            }

            foreach (var keyword in (ReadOnlySpan<string>)["true", "false", "null", "this"])
            {
                _ = candidates.TryAdd(keyword, "keyword");
            }
        }

        var result = new List<CompletionView>();
        foreach (var (label, type) in candidates)
        {
            if (label.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new CompletionView(label, type, start, partial.Length));
            }
        }

        result.Sort(static (a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return result;

        void AddMembers(ValueInfo value)
        {
            if (value.ElementType is CorElementType.String or CorElementType.SzArray or CorElementType.Array)
            {
                _ = candidates.TryAdd("Length", "property");
            }

            foreach (var level in _inspector.GetTypeChain(value.Type))
            {
                var metadata = level.Module.Metadata!;
                foreach (var field in metadata.GetFields(level.Token))
                {
                    if (!field.IsStatic && ValueInspector.DisplayNameForField(field.Name) is ({ } name, var kind))
                    {
                        _ = candidates.TryAdd(name, kind == VariableKind.Property ? "property" : "field");
                    }
                }

                foreach (var property in metadata.GetProperties(level.Token))
                {
                    if (property.IsPublic && !property.IsStatic)
                    {
                        _ = candidates.TryAdd(property.Name, "property");
                    }
                }

                foreach (var method in metadata.GetMethods(level.Token))
                {
                    if (method.IsPublic && !method.IsStatic && !method.IsSpecialName && !method.Name.Contains('<', StringComparison.Ordinal))
                    {
                        _ = candidates.TryAdd(method.Name, "method");
                    }
                }
            }
        }
    }

    /// <summary>Start of the member-access chain ending at <paramref name="end"/> (e.g. <c>a.b[0].c</c>).</summary>
    private static int ExpressionStart(string text, int end)
    {
        var i = end;
        var depth = 0;
        while (i > 0)
        {
            var c = text[i - 1];
            if (c is ']' or ')')
            {
                depth++;
            }
            else if (c is '[' or '(')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && !(char.IsLetterOrDigit(c) || c is '_' or '.'))
            {
                break;
            }

            i--;
        }

        return i;
    }

    private FrameScope? CreateTopFrameScope(ICorDebugThread thread)
    {
        if (thread.GetID(out var id) < 0)
        {
            return null;
        }

        var frames = WalkStack(thread, (int)id);
        return frames.Count == 0 ? null : new FrameScope(this, frames[0], thread);
    }

    /// <summary>Resolves names for the expression evaluator against a frame.</summary>
    private sealed class FrameScope(DebugSession session, FrameEntry frame, ICorDebugThread thread) : IEvaluationScope
    {
        private Dictionary<string, ICorDebugValue>? _names;
        private int _generation = -1;

        public ICorDebugThread Thread => thread;

        public StaticType? DeclaringType
        {
            get
            {
                if (frame.Module?.Metadata is not { } metadata)
                {
                    return null;
                }

                // Lambdas, async methods and iterators run in compiler-generated nested types.
                var token = metadata.GetDeclaringType(frame.MethodToken);
                while (metadata.GetTypeName(token).Contains('<', StringComparison.Ordinal) && metadata.GetEnclosingType(token) is not 0 and var outer)
                {
                    token = outer;
                }

                return session._inspector.FindType(metadata.GetMetadataName(token));
            }
        }

        public ICorDebugValue? Lookup(string name)
        {
            // Values captured before a func-eval may be stale; rebuild after each one.
            if (_names is null || _generation != session._funcEval.Generation)
            {
                _names = new Dictionary<string, ICorDebugValue>(StringComparer.Ordinal);
                foreach (var variable in session.GetFrameVariables(frame, includeException: true))
                {
                    if (variable.Value is not null)
                    {
                        _names.TryAdd(variable.Name, variable.Value);
                    }
                }

                _generation = session._funcEval.Generation;
            }

            return _names.GetValueOrDefault(name);
        }
    }
}
