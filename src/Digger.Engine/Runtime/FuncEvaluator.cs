using System;
using System.Collections.Generic;
using System.Threading;
using Digger.Engine.Infrastructure;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Runtime;

/// <summary>Outcome of a function evaluation.</summary>
public readonly record struct EvalResult(ICorDebugValue? Value, bool IsException, string? Error)
{
    public bool Succeeded => Error is null && !IsException;

    public static EvalResult Failure(string error) => new(null, false, error);
}

/// <summary>
/// Runs code in the debuggee (property getters, ToString, indexers) via ICorDebugEval.
///
/// An evaluation resumes the stopped process with every other thread suspended, then waits
/// for EvalComplete/EvalException. While it waits, the engine thread is blocked, so the
/// session handles any other callback inline on the callback thread (see
/// <see cref="IsRunning"/>).
/// </summary>
public sealed class FuncEvaluator : IDisposable
{
    private readonly ManualResetEventSlim _completed = new();
    private readonly ManualResetEventSlim _abortRequested = new();
    private volatile bool _isRunning;
    private volatile bool _completedWithException;
    private volatile bool _abandoned;

    public FuncEvaluator(TimeSpan timeout) => Timeout = timeout;

    /// <summary>Time allowed for one evaluation before it is aborted.</summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>Let every thread run during evaluations (Delve's <c>call</c>), not only the evaluating one.</summary>
    public bool RunAllThreads { get; set; }

    /// <summary>Set by the session while the process is alive and stopped at a safe point.</summary>
    public ICorDebugProcess? Process { get; set; }

    /// <summary>
    /// Incremented by every evaluation. Frames (and possibly values) obtained before an
    /// evaluation must be re-acquired afterwards, because it continues the process.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>True while an evaluation is executing in the debuggee.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Called on the callback thread for EvalComplete/EvalException.</summary>
    internal void OnCompleted(bool exception)
    {
        _completedWithException = exception;
        _completed.Set();
    }

    /// <summary>Any thread: aborts the running evaluation (Ctrl-C during <c>call</c>).</summary>
    public void RequestAbort()
    {
        if (_isRunning)
        {
            _abortRequested.Set();
        }
    }

    /// <summary>The process exited mid-evaluation: wake the waiter.</summary>
    internal void Abandon()
    {
        _abandoned = true;
        _completed.Set();
    }

    public EvalResult Call(ICorDebugThread thread, ICorDebugFunction function, IReadOnlyList<ICorDebugType> typeArgs, IReadOnlyList<ICorDebugValue> args)
    {
        if (CreateEval(thread, out var eval) is { } error)
        {
            return EvalResult.Failure(error);
        }

        var hr = eval.CallFunction(function, typeArgs, args);
        return hr < 0 ? EvalResult.Failure(Describe(hr)) : Run(thread, eval);
    }

    public EvalResult NewString(ICorDebugThread thread, string value)
    {
        if (CreateEval(thread, out var eval) is { } error)
        {
            return EvalResult.Failure(error);
        }

        var hr = eval.NewString(value);
        return hr < 0 ? EvalResult.Failure(Describe(hr)) : Run(thread, eval);
    }

    /// <summary>Allocates an unboxed primitive in the debuggee (no code runs).</summary>
    public static ICorDebugValue? CreatePrimitive(ICorDebugThread thread, CorElementType type)
    {
        if (thread.CreateEval(out var eval) < 0 || eval.CreateValue(type, null, out var value) < 0)
        {
            return null;
        }

        return value;
    }

    private string? CreateEval(ICorDebugThread thread, out ICorDebugEval eval)
    {
        eval = null!;
        if (_isRunning)
        {
            return "Nested evaluations are not supported.";
        }

        if (Process is null)
        {
            return "The process is not stopped.";
        }

        var hr = thread.CreateEval(out eval);
        return hr < 0 ? Describe(hr) : null;
    }

    private EvalResult Run(ICorDebugThread thread, ICorDebugEval eval)
    {
        var process = Process!;
        _completed.Reset();
        _abortRequested.Reset();
        _abandoned = false;
        _completedWithException = false;
        _isRunning = true;
        Generation++;
        try
        {
            // Normally only the evaluating thread runs; others stay frozen so the user's view
            // of the program does not change underneath them.
            if (!RunAllThreads)
            {
                _ = process.SetAllThreadsDebugState(CorDebugThreadState.Suspend, thread);
                _ = thread.SetDebugState(CorDebugThreadState.Run);
            }

            var hr = process.Continue(false);
            if (hr < 0)
            {
                return EvalResult.Failure(Describe(hr));
            }

            var signaled = WaitHandle.WaitAny([_completed.WaitHandle, _abortRequested.WaitHandle], Timeout);
            if (signaled != 0)
            {
                var reason = signaled == 1 ? "aborted" : "timed out";
                Log.Warn($"Evaluation {reason}; aborting");
                _ = eval.Abort();
                if (!_completed.Wait(TimeSpan.FromSeconds(2)))
                {
                    if (eval is ICorDebugEval2 eval2)
                    {
                        _ = eval2.RudeAbort();
                    }

                    if (!_completed.Wait(TimeSpan.FromSeconds(2)))
                    {
                        _abandoned = true;
                        return EvalResult.Failure($"Evaluation {reason} and could not be stopped.");
                    }
                }

                return EvalResult.Failure($"Evaluation {reason}.");
            }

            if (_abandoned)
            {
                return EvalResult.Failure("The process exited during evaluation.");
            }

            _ = eval.GetResult(out var result);
            return new EvalResult(result, _completedWithException, null);
        }
        finally
        {
            _isRunning = false;
            if (!_abandoned)
            {
                _ = process.SetAllThreadsDebugState(CorDebugThreadState.Run, null);
            }
        }
    }

    internal static string Describe(int hr) => hr switch
    {
        HResults.CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT => "Cannot evaluate: the thread is not at a GC-safe point.",
        HResults.CORDBG_E_FUNC_EVAL_BAD_START_POINT => "Cannot evaluate: the thread is in a state that does not allow evaluation.",
        HResults.CORDBG_E_PROCESS_NOT_SYNCHRONIZED => "Cannot evaluate: the process is running.",
        _ => $"Evaluation failed (0x{hr:X8}).",
    };

    public void Dispose()
    {
        _completed.Dispose();
        _abortRequested.Dispose();
    }
}
