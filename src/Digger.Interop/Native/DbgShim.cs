using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Digger.Interop.Native;

/// <summary>
/// Bindings for <c>libdbgshim</c>, the shim that launches a process suspended, waits for
/// the CoreCLR runtime to start in it, and hands back an <c>ICorDebug</c> instance.
/// </summary>
public static unsafe partial class DbgShim
{
    private const string Library = "dbgshim";
    private static string? s_overridePath;

    /// <summary>Must be called before the first P/Invoke to use a specific libdbgshim.</summary>
    public static void UseLibrary(string? path)
    {
        s_overridePath = path;
        NativeLibrary.SetDllImportResolver(typeof(DbgShim).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, Library, StringComparison.Ordinal))
        {
            return 0;
        }

        if (s_overridePath is { Length: > 0 } explicitPath)
        {
            return NativeLibrary.Load(explicitPath);
        }

        // Next to the executable first (AOT/single-file layout), then the default probe.
        var fileName = OperatingSystem.IsWindows() ? "dbgshim.dll"
            : OperatingSystem.IsMacOS() ? "libdbgshim.dylib"
            : "libdbgshim.so";
        var local = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(local) && NativeLibrary.TryLoad(local, out var handle))
        {
            return handle;
        }

        return NativeLibrary.TryLoad(name, assembly, searchPath, out handle) ? handle : 0;
    }

    [LibraryImport(Library)]
    public static partial int CreateProcessForLaunch(
        char* lpCommandLine,
        [MarshalAs(UnmanagedType.Bool)] bool bSuspendProcess,
        void* lpEnvironment,
        char* lpCurrentDirectory,
        out uint pProcessId,
        out nint pResumeHandle);

    [LibraryImport(Library)]
    public static partial int ResumeProcess(nint hResumeHandle);

    [LibraryImport(Library)]
    public static partial int CloseResumeHandle(nint hResumeHandle);

    [LibraryImport(Library)]
    public static partial int RegisterForRuntimeStartup(
        uint dwProcessId,
        delegate* unmanaged<nint, nint, int, void> pfnCallback,
        nint parameter,
        out nint ppUnregisterToken);

    [LibraryImport(Library)]
    public static partial int UnregisterForRuntimeStartup(nint pUnregisterToken);

    [LibraryImport(Library)]
    public static partial int EnumerateCLRs(
        uint debuggeePID,
        out nint* ppHandleArrayOut,
        out char** ppStringArrayOut,
        out uint pdwArrayLengthOut);

    [LibraryImport(Library)]
    public static partial int CloseCLREnumeration(nint* pHandleArray, char** pStringArray, uint dwArrayLength);

    [LibraryImport(Library)]
    public static partial int CreateVersionStringFromModule(
        uint pidDebuggee,
        char* szModuleName,
        char* pBuffer,
        uint cchBuffer,
        out uint pdwLength);

    [LibraryImport(Library)]
    public static partial int CreateDebuggingInterfaceFromVersionEx(
        int iDebuggerVersion,
        char* szDebuggeeVersion,
        out nint ppCordb);
}
