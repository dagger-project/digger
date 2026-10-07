using System.Runtime.InteropServices;

namespace Digger.Interop.CorDebug;

/// <summary><c>COR_TYPEID</c>: identifies a type in the GC heap.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CorTypeId(ulong Token1, ulong Token2);

/// <summary><c>COR_HEAPOBJECT</c>: one object found by a heap walk.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CorHeapObject(ulong Address, ulong Size, CorTypeId Type);

/// <summary><c>COR_DEBUG_STEP_RANGE</c>: a half-open IL offset range [Start, End).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CorDebugStepRange(uint StartOffset, uint EndOffset);
