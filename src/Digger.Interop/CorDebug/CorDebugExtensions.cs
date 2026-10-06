using System;
using System.Collections.Generic;

namespace Digger.Interop.CorDebug;

/// <summary>Convenience wrappers over the raw-pointer ICorDebug methods.</summary>
public static unsafe class CorDebugExtensions
{
    public static string? GetNameString(this ICorDebugModule module) =>
        ComInterop.ReadString(module.GetName);

    /// <summary>Reads a managed string, truncated to <paramref name="maxLength"/> characters.</summary>
    public static string? GetStringValue(this ICorDebugStringValue value, int maxLength = int.MaxValue)
    {
        if (value.GetLength(out var length) < 0)
        {
            return null;
        }

        var count = (int)Math.Min(length, (uint)maxLength);
        if (count == 0)
        {
            return string.Empty;
        }

        // GetString wants room for the terminating NUL.
        var buffer = new char[count + 1];
        fixed (char* p = buffer)
        {
            if (value.GetString((uint)count + 1, out var fetched, p) < 0)
            {
                return null;
            }

            return new string(p, 0, (int)Math.Min(fetched, (uint)count));
        }
    }

    public static List<ICorDebugThread> GetThreads(this ICorDebugController controller) =>
        controller.EnumerateThreads(out var threads) < 0 ? [] : ComInterop.Drain<ICorDebugThread>(threads.Next);

    public static List<ICorDebugValue> GetValues(this ICorDebugValueEnum values) =>
        ComInterop.Drain<ICorDebugValue>(values.Next);

    public static List<ICorDebugType> GetTypes(this ICorDebugTypeEnum types) =>
        ComInterop.Drain<ICorDebugType>(types.Next);

    /// <summary>Type arguments of a constructed type (empty for non-generic types).</summary>
    public static List<ICorDebugType> GetTypeParameters(this ICorDebugType type) =>
        type.EnumerateTypeParameters(out var types) < 0 ? [] : types.GetTypes();

    /// <summary>Calls a (possibly generic) function through ICorDebugEval2.</summary>
    public static int CallFunction(this ICorDebugEval eval, ICorDebugFunction function, IReadOnlyList<ICorDebugType> typeArgs, IReadOnlyList<ICorDebugValue> args)
    {
        if (eval is not ICorDebugEval2 eval2)
        {
            return HResults.E_NOINTERFACE;
        }

        var typePointers = stackalloc nint[Math.Max(typeArgs.Count, 1)];
        var argPointers = stackalloc nint[Math.Max(args.Count, 1)];
        try
        {
            for (var i = 0; i < typeArgs.Count; i++)
            {
                typePointers[i] = ComInterop.GetPointer(typeArgs[i]);
            }

            for (var i = 0; i < args.Count; i++)
            {
                argPointers[i] = ComInterop.GetPointer(args[i]);
            }

            return eval2.CallParameterizedFunction(function, (uint)typeArgs.Count, typePointers, (uint)args.Count, argPointers);
        }
        finally
        {
            for (var i = 0; i < typeArgs.Count; i++)
            {
                ComInterop.Release(typePointers[i]);
            }

            for (var i = 0; i < args.Count; i++)
            {
                ComInterop.Release(argPointers[i]);
            }
        }
    }

    /// <summary>Starts a step over/into the IL range [start, end).</summary>
    public static int StepRange(this ICorDebugStepper stepper, bool stepIn, uint start, uint end)
    {
        var range = new CorDebugStepRange(start, end);
        return stepper.StepRange(stepIn, &range, 1);
    }
}
