using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Digger.Interop.CorDebug;

/// <summary>Helpers for the raw-pointer corners of the ICorDebug surface.</summary>
public static unsafe class ComInterop
{
    /// <summary>Signature of the <c>Next</c> method shared by all ICorDebug enumerators.</summary>
    public delegate int EnumNext(uint celt, nint* values, out uint fetched);

    /// <summary>Signature of the two-call "query length, then fill buffer" string getters.</summary>
    public delegate int StringGetter(uint capacity, out uint length, char* buffer);

    /// <summary>
    /// Wraps an owned (already AddRef'd) interface pointer in a managed RCW and releases the
    /// raw reference: the RCW holds its own.
    /// </summary>
    public static T? TakeOwnership<T>(nint pointer)
        where T : class
    {
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer);
        }
        finally
        {
            _ = Marshal.Release(pointer);
        }
    }

    /// <summary>Returns an AddRef'd native pointer for <paramref name="value"/>; free with <see cref="Release"/>.</summary>
    public static nint GetPointer<T>(T value)
        where T : class => (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(value);

    public static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = Marshal.Release(pointer);
        }
    }

    /// <summary>Drains an ICorDebug enumerator in batches.</summary>
    public static List<T> Drain<T>(EnumNext next)
        where T : class
    {
        const int Batch = 16;
        var result = new List<T>();
        var buffer = stackalloc nint[Batch];
        while (true)
        {
            var hr = next(Batch, buffer, out var fetched);
            for (var i = 0; i < fetched; i++)
            {
                if (TakeOwnership<T>(buffer[i]) is { } item)
                {
                    result.Add(item);
                }
            }

            if (hr != HResults.S_OK || fetched < Batch)
            {
                return result;
            }
        }
    }

    /// <summary>Reads a string from a two-call API; returns null on failure.</summary>
    public static string? ReadString(StringGetter getter)
    {
        if (HResults.Failed(getter(0, out var length, null)) || length == 0)
        {
            return null;
        }

        // Lengths include the terminating NUL.
        if (length <= 256)
        {
            var stack = stackalloc char[(int)length];
            return Fill(getter, stack, length);
        }

        var heap = new char[length];
        fixed (char* p = heap)
        {
            return Fill(getter, p, length);
        }

        static string? Fill(StringGetter getter, char* buffer, uint capacity)
        {
            if (HResults.Failed(getter(capacity, out var written, buffer)))
            {
                return null;
            }

            var span = new ReadOnlySpan<char>(buffer, (int)Math.Min(written, capacity));
            var nul = span.IndexOf('\0');
            return new string(nul >= 0 ? span[..nul] : span);
        }
    }
}
