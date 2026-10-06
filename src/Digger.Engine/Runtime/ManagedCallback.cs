using System.Runtime.InteropServices.Marshalling;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Runtime;

/// <summary>Receives ICorDebug events on the runtime's callback thread.</summary>
internal interface ICallbackSink
{
    /// <summary>
    /// Handles one callback. <paramref name="controller"/> must be continued exactly once,
    /// either by the sink or later by the user.
    /// </summary>
    void OnCallback(ICorDebugController controller, DebugEvent debugEvent);
}

internal enum DebugEventKind
{
    Breakpoint,
    StepComplete,
    Break,
    Exception,
    ExceptionV2,
    EvalComplete,
    EvalException,
    CreateProcess,
    ExitProcess,
    CreateThread,
    ExitThread,
    LoadModule,
    UnloadModule,
    NameChange,
    CreateAppDomain,
    BreakpointSetError,
    LogMessage,
    Other,
}

/// <summary>A single callback, flattened. Only the fields relevant to <see cref="Kind"/> are set.</summary>
internal sealed class DebugEvent(DebugEventKind kind)
{
    public DebugEventKind Kind { get; } = kind;

    public ICorDebugAppDomain? AppDomain { get; init; }

    public ICorDebugProcess? Process { get; init; }

    public ICorDebugThread? Thread { get; init; }

    public ICorDebugBreakpoint? Breakpoint { get; init; }

    public ICorDebugStepper? Stepper { get; init; }

    public CorDebugStepReason StepReason { get; init; }

    public ICorDebugEval? Eval { get; init; }

    public ICorDebugModule? Module { get; init; }

    public CorDebugExceptionCallbackType ExceptionType { get; init; }

    public string? Message { get; init; }

    public int ErrorCode { get; init; }
}

/// <summary>
/// The ICorDebugManagedCallback implementation. It does no work itself: every event is
/// flattened into a <see cref="DebugEvent"/> and handed to the sink.
/// </summary>
[GeneratedComClass]
internal sealed partial class ManagedCallback(ICallbackSink sink) : ICorDebugManagedCallback, ICorDebugManagedCallback2
{
    private int Raise(ICorDebugController controller, DebugEvent debugEvent)
    {
        sink.OnCallback(controller, debugEvent);
        return HResults.S_OK;
    }

    // ---- ICorDebugManagedCallback -----------------------------------------------------

    public int Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint) =>
        Raise(pAppDomain, new(DebugEventKind.Breakpoint) { AppDomain = pAppDomain, Thread = pThread, Breakpoint = pBreakpoint });

    public int StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugStepper pStepper, CorDebugStepReason reason) =>
        Raise(pAppDomain, new(DebugEventKind.StepComplete) { AppDomain = pAppDomain, Thread = pThread, Stepper = pStepper, StepReason = reason });

    public int Break(ICorDebugAppDomain pAppDomain, ICorDebugThread thread) =>
        Raise(pAppDomain, new(DebugEventKind.Break) { AppDomain = pAppDomain, Thread = thread });

    public int Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, bool unhandled) =>
        Raise(pAppDomain, new(DebugEventKind.Exception) { AppDomain = pAppDomain, Thread = pThread });

    public int EvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval) =>
        Raise(pAppDomain, new(DebugEventKind.EvalComplete) { AppDomain = pAppDomain, Thread = pThread, Eval = pEval });

    public int EvalException(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval) =>
        Raise(pAppDomain, new(DebugEventKind.EvalException) { AppDomain = pAppDomain, Thread = pThread, Eval = pEval });

    public int CreateProcess(ICorDebugProcess pProcess) =>
        Raise(pProcess, new(DebugEventKind.CreateProcess) { Process = pProcess });

    public int ExitProcess(ICorDebugProcess pProcess) =>
        Raise(pProcess, new(DebugEventKind.ExitProcess) { Process = pProcess });

    public int CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread thread) =>
        Raise(pAppDomain, new(DebugEventKind.CreateThread) { AppDomain = pAppDomain, Thread = thread });

    public int ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread thread) =>
        Raise(pAppDomain, new(DebugEventKind.ExitThread) { AppDomain = pAppDomain, Thread = thread });

    public int LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule) =>
        Raise(pAppDomain, new(DebugEventKind.LoadModule) { AppDomain = pAppDomain, Module = pModule });

    public int UnloadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule) =>
        Raise(pAppDomain, new(DebugEventKind.UnloadModule) { AppDomain = pAppDomain, Module = pModule });

    public int LoadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int UnloadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int DebuggerError(ICorDebugProcess pProcess, int errorHR, uint errorCode) =>
        Raise(pProcess, new(DebugEventKind.Other) { ErrorCode = errorHR, Message = "DebuggerError" });

    public unsafe int LogMessage(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, char* pLogSwitchName, char* pMessage) =>
        Raise(pAppDomain, new(DebugEventKind.LogMessage) { AppDomain = pAppDomain, Thread = pThread, Message = pMessage is null ? null : new string(pMessage) });

    public unsafe int LogSwitch(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, uint ulReason, char* pLogSwitchName, char* pParentName) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int CreateAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain) =>
        Raise(pProcess, new(DebugEventKind.CreateAppDomain) { Process = pProcess, AppDomain = pAppDomain });

    public int ExitAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain) =>
        Raise(pProcess, new(DebugEventKind.Other));

    public int LoadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int UnloadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int ControlCTrap(ICorDebugProcess pProcess) =>
        Raise(pProcess, new(DebugEventKind.Other));

    public int NameChange(ICorDebugAppDomain? pAppDomain, ICorDebugThread? pThread)
    {
        // Either argument may be null; the thread's own domain is the controller.
        ICorDebugController? controller = pAppDomain;
        if (controller is null && pThread is not null && pThread.GetAppDomain(out var domain) >= 0)
        {
            controller = domain;
        }

        return controller is null
            ? HResults.S_OK
            : Raise(controller, new(DebugEventKind.NameChange) { AppDomain = pAppDomain, Thread = pThread });
    }

    public int UpdateModuleSymbols(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule, nint pSymbolStream) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int EditAndContinueRemap(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction, bool fAccurate) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int BreakpointSetError(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint, uint dwError) =>
        Raise(pAppDomain, new(DebugEventKind.BreakpointSetError) { AppDomain = pAppDomain, Breakpoint = pBreakpoint, ErrorCode = (int)dwError });

    // ---- ICorDebugManagedCallback2 ----------------------------------------------------

    public int FunctionRemapOpportunity(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pOldFunction, ICorDebugFunction pNewFunction, uint oldILOffset) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public unsafe int CreateConnection(ICorDebugProcess pProcess, uint dwConnectionId, char* pConnName) =>
        Raise(pProcess, new(DebugEventKind.Other));

    public int ChangeConnection(ICorDebugProcess pProcess, uint dwConnectionId) =>
        Raise(pProcess, new(DebugEventKind.Other));

    public int DestroyConnection(ICorDebugProcess pProcess, uint dwConnectionId) =>
        Raise(pProcess, new(DebugEventKind.Other));

    public int Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFrame? pFrame, uint nOffset, CorDebugExceptionCallbackType dwEventType, uint dwFlags) =>
        Raise(pAppDomain, new(DebugEventKind.ExceptionV2) { AppDomain = pAppDomain, Thread = pThread, ExceptionType = dwEventType });

    public int ExceptionUnwind(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, CorDebugExceptionUnwindCallbackType dwEventType, uint dwFlags) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int FunctionRemapComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction) =>
        Raise(pAppDomain, new(DebugEventKind.Other));

    public int MDANotification(ICorDebugController pController, ICorDebugThread pThread, nint pMDA) =>
        Raise(pController, new(DebugEventKind.Other));
}
