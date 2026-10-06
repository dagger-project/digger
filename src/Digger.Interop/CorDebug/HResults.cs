namespace Digger.Interop.CorDebug;

/// <summary>HRESULT values that the debugger needs to tell apart.</summary>
public static class HResults
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int ERROR_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);
    public const int CORDBG_S_FUNC_EVAL_ABORTED = 0x00131319;
    public const int CORDBG_S_AT_END_OF_STACK = 0x00131324;
    public const int CORDBG_E_PROCESS_TERMINATED = unchecked((int)0x80131301);
    public const int CORDBG_E_PROCESS_NOT_SYNCHRONIZED = unchecked((int)0x80131302);
    public const int CORDBG_E_IL_VAR_NOT_AVAILABLE = unchecked((int)0x80131304);
    public const int CORDBG_E_FUNC_EVAL_BAD_START_POINT = unchecked((int)0x80131313);
    public const int CORDBG_E_OBJECT_NEUTERED = unchecked((int)0x8013134F);
    public const int CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT = unchecked((int)0x80131C23);

    public static bool Succeeded(int hr) => hr >= 0;

    public static bool Failed(int hr) => hr < 0;

    /// <summary>Throws a <see cref="CorDebugException"/> when <paramref name="hr"/> is a failure code.</summary>
    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Throw(hr);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Throw(int hr) =>
        throw new CorDebugException(hr);
}

/// <summary>A failed ICorDebug/dbgshim call.</summary>
public sealed class CorDebugException : System.Exception
{
    public CorDebugException(int hresult)
        : base($"ICorDebug call failed: 0x{hresult:X8}") => HResult = hresult;

    public CorDebugException(int hresult, string message)
        : base($"{message} (0x{hresult:X8})") => HResult = hresult;
}
