using Digger.Engine.Runtime;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Inspection;

/// <summary>
/// An ICorDebugValue with references followed and boxes opened. Built once per value by
/// <see cref="ValueInspector.Analyze"/> so formatting and expansion share the work.
/// </summary>
public sealed class ValueInfo
{
    internal ValueInfo(ICorDebugValue raw, ICorDebugValue? heap, ICorDebugValue? target, bool isNull, CorElementType elementType, ICorDebugType? type)
    {
        Raw = raw;
        Heap = heap;
        Target = target;
        IsNull = isNull;
        ElementType = elementType;
        Type = type;
    }

    /// <summary>The value as obtained (often an ICorDebugReferenceValue).</summary>
    public ICorDebugValue Raw { get; }

    /// <summary>The heap object a reference pointed at (before unboxing), if any.</summary>
    public ICorDebugValue? Heap { get; }

    /// <summary>The dereferenced, unboxed value; null when <see cref="IsNull"/>.</summary>
    public ICorDebugValue? Target { get; }

    public bool IsNull { get; }

    /// <summary>Element type of <see cref="Target"/> (of <see cref="Raw"/> for null references).</summary>
    public CorElementType ElementType { get; }

    /// <summary>Exact runtime type, when the runtime can provide it.</summary>
    public ICorDebugType? Type { get; }

    /// <summary>The <c>this</c> argument to pass when calling an instance method on this value.</summary>
    internal ICorDebugValue ThisArgument => Raw is ICorDebugReferenceValue ? Raw : Target ?? Raw;

    /// <summary>Resolved TypeDef behind <see cref="Type"/> (class/value types only), cached.</summary>
    internal TypeHandle? Handle { get; set; }

    internal bool HandleResolved { get; set; }

    public bool IsPrimitive => ElementType is >= CorElementType.Boolean and <= CorElementType.R8
        or CorElementType.I or CorElementType.U;
}

/// <summary>A TypeDef in a loaded module, plus the constructed type that refers to it.</summary>
internal sealed record TypeHandle(ICorDebugType Type, ICorDebugClass Class, LoadedModule Module, uint Token, string Name);
