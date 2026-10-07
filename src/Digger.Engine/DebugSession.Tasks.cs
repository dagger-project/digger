using System;
using System.Collections.Generic;
using Digger.Engine.Inspection;
using Digger.Interop.CorDebug;

namespace Digger.Engine;

/// <summary>A suspended (or running) async method: Digger's take on a goroutine.</summary>
/// <param name="Id">Number for <c>task &lt;id&gt;</c>; its frames use thread id <c>-Id</c>.</param>
/// <param name="Name">The async method.</param>
public sealed record TaskView(int Id, string Name)
{
    public string? SourcePath { get; init; }

    public int Line { get; init; }

    /// <summary>Executing on a thread right now, rather than waiting at an await.</summary>
    public bool IsRunning { get; init; }

    /// <summary>The thread a running task executes on, when found.</summary>
    public int ThreadId { get; init; }

    public bool IsUserCode { get; init; }

    /// <summary>The innermost async method awaiting this one, if any.</summary>
    public string? AwaitedBy { get; init; }
}

/// <content>Async methods in flight, found by walking the GC heap for state machine boxes.</content>
public sealed partial class DebugSession
{
    private const int TaskCompletedMask = 0x1600000; // RanToCompletion | Faulted | Canceled

    /// <summary>
    /// Async methods that have started and not finished, outermost callers excluded (they show
    /// up in each task's logical stack). Each task's frames are available as thread <c>-Id</c>,
    /// so stack traces, scopes and evaluation work on them like on a thread.
    /// </summary>
    public List<TaskView> GetAsyncTasks(bool userCodeOnly)
    {
        RequireStopped();
        _frames.Tasks ??= FindAsyncTasks();
        return userCodeOnly ? _frames.Tasks.FindAll(static t => t.IsUserCode) : _frames.Tasks;
    }

    private List<TaskView> FindAsyncTasks()
    {
        if (_process is not ICorDebugProcess5 heap || heap.EnumerateHeap(out var objects) < 0)
        {
            throw new NotSupportedException("This runtime does not support walking the heap.");
        }

        var boxes = new List<(ValueInfo Box, ValueInfo StateMachine, FrameEntry Frame, bool Running)>();
        var isBoxType = new Dictionary<CorTypeId, bool>();
        var batch = new CorHeapObject[1024];
        while (true)
        {
            uint fetched;
            unsafe
            {
                fixed (CorHeapObject* buffer = batch)
                {
                    if (objects.Next((uint)batch.Length, buffer, out fetched) < 0)
                    {
                        break;
                    }
                }
            }

            for (var i = 0; i < fetched; i++)
            {
                var item = batch[i];
                if (!isBoxType.TryGetValue(item.Type, out var box))
                {
                    box = heap.GetTypeForTypeID(item.Type, out var type) >= 0
                        && _inspector.Resolve(type)?.Name.EndsWith("AsyncStateMachineBox", StringComparison.Ordinal) == true;
                    isBoxType[item.Type] = box;
                }

                if (box && heap.GetObject(item.Address, out var value) >= 0 && DescribeTask(ValueInspector.Analyze(value)) is { } task)
                {
                    boxes.Add(task);
                }
            }

            if (fetched < batch.Length)
            {
                break;
            }
        }

        // Stable order for the numbering: user code first, then by method and location.
        boxes.Sort(static (a, b) =>
            a.Frame.IsUserCode != b.Frame.IsUserCode ? (a.Frame.IsUserCode ? -1 : 1)
            : string.CompareOrdinal(a.Frame.Name, b.Frame.Name) is not 0 and var byName ? byName
            : (a.Frame.Location?.StartLine ?? 0).CompareTo(b.Frame.Location?.StartLine ?? 0));

        var result = new List<TaskView>(boxes.Count);
        for (var i = 0; i < boxes.Count; i++)
        {
            var (box, stateMachine, first, running) = boxes[i];
            var id = i + 1;

            // A running method is somewhere in its body, not at an await: ask its thread.
            var (threadId, current) = running ? FindRunningFrame(first) : (0, null);
            var frames = new List<FrameEntry> { DescribeAsyncFrame(stateMachine, -id, current?.Location) ?? first };
            frames.AddRange(FollowContinuations(box, -id));
            _frames.AddThread(-id, frames);
            result.Add(new TaskView(id, first.Name)
            {
                SourcePath = frames[0].Location is { } location ? _sources.Resolve(first.Module, location.Document) : null,
                Line = frames[0].Location?.StartLine ?? 0,
                ThreadId = threadId,
                IsRunning = running,
                IsUserCode = first.IsUserCode,
                AwaitedBy = frames.Count > 1 ? frames[1].Name : null,
            });
        }

        return result;
    }

    /// <summary>The thread whose stack is executing the async method <paramref name="task"/>.</summary>
    private (int ThreadId, FrameEntry? Frame) FindRunningFrame(FrameEntry task)
    {
        foreach (var thread in GetThreads())
        {
            foreach (var frame in GetFrames(thread.Id))
            {
                if (frame.Frame is not null && frame.MethodToken == task.MethodToken && ReferenceEquals(frame.Module, task.Module))
                {
                    return (thread.Id, frame);
                }
            }
        }

        return (0, null);
    }

    /// <summary>A box whose task has not completed, with the frame where its method is.</summary>
    private (ValueInfo Box, ValueInfo StateMachine, FrameEntry Frame, bool Running)? DescribeTask(ValueInfo box)
    {
        if (box.IsNull
            || _inspector.FindField(box, "m_stateFlags") is not { } flagsValue
            || ValueInspector.ReadPrimitive(flagsValue, CorElementType.I4) is not int flags
            || (flags & TaskCompletedMask) != 0
            || _inspector.FindField(box, "StateMachine") is not { } stateMachineValue)
        {
            return null;
        }

        var stateMachine = ValueInspector.Analyze(stateMachineValue);
        if (_inspector.FindField(stateMachine, "<>1__state") is not { } stateValue
            || ValueInspector.ReadPrimitive(stateValue, CorElementType.I4) is not int state
            || state < -1
            || DescribeAsyncFrame(stateMachine, 0) is not { } frame)
        {
            return null;
        }

        return (box, stateMachine, frame, state == -1);
    }
}
