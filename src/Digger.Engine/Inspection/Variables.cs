using System.Collections.Generic;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Inspection;

/// <summary>How a child should be presented.</summary>
public enum VariableKind
{
    Data,
    Property,
    Method,
    Class,
    Virtual,
    Error,
}

/// <summary>A named child before formatting: either a debuggee value, a synthetic group, or text.</summary>
public sealed record NamedValue(string Name)
{
    public ICorDebugValue? Value { get; init; }

    /// <summary>Synthetic node (e.g. "Static members", "Raw View") expanded by its own container.</summary>
    public IVariableContainer? Group { get; init; }

    /// <summary>Fixed display text: used for groups and errors.</summary>
    public string? Text { get; init; }

    /// <summary>Expression that re-evaluates this child (for watches / "copy as expression").</summary>
    public string? EvaluateName { get; init; }

    public VariableKind Kind { get; init; }

    /// <summary>Evaluated only on request (DAP "lazy" presentation hint).</summary>
    public bool IsLazy { get; init; }
}

/// <summary>Something that can be expanded in the variables view.</summary>
public interface IVariableContainer
{
    /// <summary>Number of indexed children (arrays and collections), or 0.</summary>
    int IndexedCount { get; }

    /// <summary>Whether the presenter should sort children by name (groups stay last).</summary>
    bool SortByName => false;

    /// <summary>
    /// Children in [start, start+count); unpaged containers ignore the range. The sequence is
    /// consumed lazily and each child is formatted before the next is produced.
    /// </summary>
    IEnumerable<NamedValue> GetChildren(int start, int count);
}

/// <summary>
/// Re-obtains a value on demand. ICorDebugValue objects can be invalidated when the process
/// runs (including for func-evals), so containers keep a source rather than the value.
/// </summary>
public interface IValueSource
{
    ICorDebugValue? Resolve();
}

/// <summary>A value that is only valid until the debuggee next runs.</summary>
public sealed class FixedValueSource(ICorDebugValue value) : IValueSource
{
    public ICorDebugValue? Resolve() => value;
}

/// <summary>A heap object held through a strong GC handle; valid until the handle is disposed.</summary>
public sealed class HandleValueSource(ICorDebugHandleValue handle) : IValueSource
{
    public ICorDebugValue? Resolve() => handle;
}

/// <summary>A formatted variable, ready for the protocol layer.</summary>
public sealed record VariableView(string Name, string Value)
{
    public string? Type { get; init; }

    public int Reference { get; init; }

    public int IndexedCount { get; init; }

    public string? EvaluateName { get; init; }

    public VariableKind Kind { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsLazy { get; init; }
}

/// <summary>
/// Handle table behind DAP's <c>variablesReference</c>, plus the GC handles that keep the
/// referenced objects reachable. Everything is released on every resume.
/// </summary>
public sealed class VariableStore
{
    private readonly List<IVariableContainer> _containers = [];
    private readonly List<ICorDebugHandleValue> _handles = [];

    public int Add(IVariableContainer container)
    {
        _containers.Add(container);
        return _containers.Count;
    }

    public IVariableContainer? Get(int reference) =>
        reference > 0 && reference <= _containers.Count ? _containers[reference - 1] : null;

    /// <summary>Pins a heap object for the duration of the current stop.</summary>
    public IValueSource Pin(ValueInfo value)
    {
        if (value.Heap is ICorDebugHeapValue2 heap && heap.CreateHandle(CorDebugHandleType.Strong, out var handle) >= 0)
        {
            _handles.Add(handle);
            return new HandleValueSource(handle);
        }

        return new FixedValueSource(value.Raw);
    }

    public void Clear()
    {
        _containers.Clear();
        foreach (var handle in _handles)
        {
            _ = handle.Dispose();
        }

        _handles.Clear();
    }
}
