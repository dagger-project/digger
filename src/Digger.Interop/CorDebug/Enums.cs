namespace Digger.Interop.CorDebug;

/// <summary>ECMA-335 element type codes (<c>CorElementType</c>).</summary>
public enum CorElementType : uint
{
    End = 0x00,
    Void = 0x01,
    Boolean = 0x02,
    Char = 0x03,
    I1 = 0x04,
    U1 = 0x05,
    I2 = 0x06,
    U2 = 0x07,
    I4 = 0x08,
    U4 = 0x09,
    I8 = 0x0A,
    U8 = 0x0B,
    R4 = 0x0C,
    R8 = 0x0D,
    String = 0x0E,
    Ptr = 0x0F,
    ByRef = 0x10,
    ValueType = 0x11,
    Class = 0x12,
    Var = 0x13,
    Array = 0x14,
    GenericInst = 0x15,
    TypedByRef = 0x16,
    I = 0x18,
    U = 0x19,
    FnPtr = 0x1B,
    Object = 0x1C,
    SzArray = 0x1D,
    MVar = 0x1E,
    CModReqd = 0x1F,
    CModOpt = 0x20,
    Internal = 0x21,
    Max = 0x22,
    Modifier = 0x40,
    Sentinel = 0x41,
    Pinned = 0x45,
}

public enum CorDebugStepReason
{
    Normal,
    Return,
    Call,
    ExceptionFilter,
    ExceptionHandler,
    Intercept,
    Exit,
}

[System.Flags]
public enum CorDebugIntercept
{
    None = 0x0,
    ClassInit = 0x01,
    ExceptionFilter = 0x02,
    Security = 0x04,
    ContextPolicy = 0x08,
    Interception = 0x10,
}

[System.Flags]
public enum CorDebugUnmappedStop
{
    None = 0x0,
    Prolog = 0x01,
    Epilog = 0x02,
    NoMappingInfo = 0x04,
    OtherUnmapped = 0x08,
    Unmanaged = 0x10,
}

public enum CorDebugThreadState
{
    Run,
    Suspend,
}

[System.Flags]
public enum CorDebugUserState
{
    None = 0,
    StopRequested = 0x01,
    SuspendRequested = 0x02,
    Background = 0x04,
    Unstarted = 0x08,
    Stopped = 0x10,
    WaitSleepJoin = 0x20,
    Suspended = 0x40,
    UnsafePoint = 0x80,
    ThreadPool = 0x100,
}

[System.Flags]
public enum CorDebugMappingResult
{
    None = 0,
    Prolog = 0x1,
    Epilog = 0x2,
    NoInfo = 0x4,
    UnmappedAddress = 0x8,
    Exact = 0x10,
    Approximate = 0x20,
}

public enum CorDebugExceptionCallbackType
{
    FirstChance = 1,
    UserFirstChance = 2,
    CatchHandlerFound = 3,
    Unhandled = 4,
}

public enum CorDebugExceptionUnwindCallbackType
{
    UnwindBegin = 1,
    Intercepted = 2,
}

public enum CorDebugJitCompilerFlags : uint
{
    Default = 0x1,
    DisableOptimization = 0x3,
    EnableEnc = 0x7,
}

/// <summary>ICorDebug interface versions understood by <c>CreateDebuggingInterfaceFromVersionEx</c>.</summary>
public enum CorDebugInterfaceVersion
{
    Version_4_0 = 4,
}

public enum CorDebugHandleType
{
    Strong = 1,
    WeakTrackResurrection = 2,
    Pinned = 3,
}
