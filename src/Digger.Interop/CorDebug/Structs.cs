using System.Runtime.InteropServices;

namespace Digger.Interop.CorDebug;

/// <summary><c>COR_DEBUG_STEP_RANGE</c>: a half-open IL offset range [Start, End).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CorDebugStepRange(uint StartOffset, uint EndOffset);
