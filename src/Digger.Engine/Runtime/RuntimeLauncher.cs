using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading;
using Digger.Engine.Infrastructure;
using Digger.Interop.CorDebug;
using Digger.Interop.Native;

namespace Digger.Engine.Runtime;

/// <summary>
/// Starts a debuggee through dbgshim: the process is created suspended, a runtime-startup
/// callback is registered, and the process is resumed once the client finished configuring
/// breakpoints. When CoreCLR initializes in the debuggee, dbgshim hands us an ICorDebug.
/// </summary>
internal sealed unsafe class RuntimeLauncher : IDisposable
{
    private readonly Action<ICorDebug?, int> _onStartup;
    private Action? _externalResume;
    private GCHandle _self;
    private nint _resumeHandle;
    private nint _unregisterToken;

    private RuntimeLauncher(Action<ICorDebug?, int> onStartup) => _onStartup = onStartup;

    public uint ProcessId { get; private set; }

    /// <summary>Creates the process suspended and waits (asynchronously) for its runtime.</summary>
    /// <param name="commandLine">Full command line, already quoted.</param>
    /// <param name="workingDirectory">Working directory, or null to inherit.</param>
    /// <param name="environment">Variables to add (or remove, when null) on top of ours.</param>
    /// <param name="onStartup">Invoked on a dbgshim thread with the ICorDebug (or null and a failure HRESULT).
    /// The debuggee runtime is blocked until it returns, so attach synchronously inside it.</param>
    public static RuntimeLauncher Launch(
        string commandLine,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        Action<ICorDebug?, int> onStartup)
    {
        var launcher = new RuntimeLauncher(onStartup);
        try
        {
            launcher.Start(commandLine, workingDirectory, environment);
            return launcher;
        }
        catch
        {
            launcher.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits for the runtime in a process that someone else created suspended (the terminal
    /// launch shim); <paramref name="resume"/> lets it run.
    /// </summary>
    public static RuntimeLauncher ForSuspendedProcess(uint processId, Action resume, Action<ICorDebug?, int> onStartup)
    {
        var launcher = new RuntimeLauncher(onStartup) { ProcessId = processId, _externalResume = resume };
        try
        {
            launcher.Register();
            return launcher;
        }
        catch
        {
            launcher.Dispose();
            throw;
        }
    }

    private void Start(string commandLine, string? workingDirectory, IReadOnlyDictionary<string, string?>? environment)
    {
        // CreateProcessW may write to the command line buffer, so it must be a private copy.
        var commandBuffer = (commandLine + '\0').ToCharArray();
        var environmentBlock = environment is null ? null : BuildEnvironmentBlock(environment);

        fixed (char* command = commandBuffer)
        fixed (char* cwd = workingDirectory)
        fixed (byte* env = environmentBlock)
        {
            var hr = DbgShim.CreateProcessForLaunch(command, bSuspendProcess: true, env, cwd, out var pid, out _resumeHandle);
            if (hr < 0)
            {
                throw new CorDebugException(hr, $"Failed to start '{commandLine}'");
            }

            ProcessId = pid;
        }

        Register();
        Log.Info($"Created process {ProcessId}: {commandLine}");
    }

    private void Register()
    {
        _self = GCHandle.Alloc(this);
        var registerHr = DbgShim.RegisterForRuntimeStartup(ProcessId, &OnRuntimeStartup, GCHandle.ToIntPtr(_self), out _unregisterToken);
        if (registerHr < 0)
        {
            throw new CorDebugException(registerHr, "RegisterForRuntimeStartup failed");
        }
    }

    /// <summary>Lets the suspended process run.</summary>
    public void Resume()
    {
        if (Interlocked.Exchange(ref _externalResume, null) is { } externalResume)
        {
            externalResume();
            return;
        }

        if (_resumeHandle == 0)
        {
            return;
        }

        var hr = DbgShim.ResumeProcess(_resumeHandle);
        _ = DbgShim.CloseResumeHandle(_resumeHandle);
        _resumeHandle = 0;
        HResults.Check(hr);
    }

    [UnmanagedCallersOnly]
    private static void OnRuntimeStartup(nint corDebugUnknown, nint parameter, int hr)
    {
        try
        {
            if (GCHandle.FromIntPtr(parameter).Target is not RuntimeLauncher launcher)
            {
                return;
            }

            // dbgshim keeps its own reference and releases it after we return; the RCW AddRefs.
            var corDebug = hr >= 0 && corDebugUnknown != 0
                ? ComInterfaceMarshaller<ICorDebug>.ConvertToManaged((void*)corDebugUnknown)
                : null;
            launcher._onStartup(corDebug, hr);
        }
        catch (Exception ex)
        {
            // Exceptions must never cross an UnmanagedCallersOnly boundary.
            Log.Error("Runtime startup callback failed", ex);
        }
    }

    /// <summary>Creates an ICorDebug for the CoreCLR already running in <paramref name="processId"/>.</summary>
    public static ICorDebug CreateForAttach(uint processId)
    {
        HResults.Check(DbgShim.EnumerateCLRs(processId, out var handles, out var paths, out var count));
        try
        {
            if (count == 0)
            {
                throw new InvalidOperationException($"No .NET runtime found in process {processId}.");
            }

            var runtimePath = paths[0];

            // Size query: reports ERROR_INSUFFICIENT_BUFFER by design.
            var sizeHr = DbgShim.CreateVersionStringFromModule(processId, runtimePath, null, 0, out var length);
            if (sizeHr < 0 && sizeHr != HResults.ERROR_INSUFFICIENT_BUFFER)
            {
                HResults.Check(sizeHr);
            }

            var version = stackalloc char[(int)length + 1];
            HResults.Check(DbgShim.CreateVersionStringFromModule(processId, runtimePath, version, length + 1, out _));
            HResults.Check(DbgShim.CreateDebuggingInterfaceFromVersionEx((int)CorDebugInterfaceVersion.Version_4_0, version, out var unknown));
            return ComInterop.TakeOwnership<ICorDebug>(unknown)
                ?? throw new InvalidOperationException("dbgshim returned no ICorDebug instance.");
        }
        finally
        {
            _ = DbgShim.CloseCLREnumeration(handles, paths, count);
        }
    }

    /// <summary>
    /// Builds an environment block ("K=V\0K=V\0\0") over the current environment, in UTF-8:
    /// dbgshim calls CreateProcessW without CREATE_UNICODE_ENVIRONMENT, so its PAL reads the
    /// block as narrow strings (a UTF-16 block arrives as one-letter variables and nothing else).
    /// </summary>
    internal static byte[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                merged[key] = value;
            }
        }

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                _ = merged.Remove(key);
            }
            else
            {
                merged[key] = value;
            }
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in merged)
        {
            _ = builder.Append(key).Append('=').Append(value).Append('\0');
        }

        _ = builder.Append('\0');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public void Dispose()
    {
        if (_unregisterToken != 0)
        {
            _ = DbgShim.UnregisterForRuntimeStartup(_unregisterToken);
            _unregisterToken = 0;
        }

        if (_resumeHandle != 0)
        {
            _ = DbgShim.CloseResumeHandle(_resumeHandle);
            _resumeHandle = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
