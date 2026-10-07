// Source-generated COM bindings for the subset of cordebug.idl the engine uses.
//
// Rules for editing this file:
//  * Every interface lists ALL methods of its IDL definition, in IDL order, up to the
//    last method we call. The vtable is positional; skipping a slot silently corrupts calls.
//  * All methods are [PreserveSig] and return the raw HRESULT so S_FALSE and expected
//    failures (e.g. CORDBG_E_IL_VAR_NOT_AVAILABLE) do not cost an exception.
//  * Types we never touch are declared as nint so their slots keep the right shape.
//  * BOOL is a 4-byte int; it is marshalled with UnmanagedType.Bool.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Digger.Interop.CorDebug;

[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
public partial interface ICorDebug
{
    [PreserveSig] int Initialize();
    [PreserveSig] int Terminate();
    [PreserveSig] int SetManagedHandler(ICorDebugManagedCallback pCallback);
    [PreserveSig] int SetUnmanagedHandler(nint pCallback);
    [PreserveSig] unsafe int CreateProcess(char* lpApplicationName, char* lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes, int bInheritHandles, uint dwCreationFlags, nint lpEnvironment, char* lpCurrentDirectory, nint lpStartupInfo, nint lpProcessInformation, int debuggingFlags, out nint ppProcess);
    [PreserveSig] int DebugActiveProcess(uint id, [MarshalAs(UnmanagedType.Bool)] bool win32Attach, out ICorDebugProcess ppProcess);
    [PreserveSig] int EnumerateProcesses(out nint ppProcess);
    [PreserveSig] int GetProcess(uint dwProcessId, out ICorDebugProcess ppProcess);
    [PreserveSig] int CanLaunchOrAttach(uint dwProcessId, [MarshalAs(UnmanagedType.Bool)] bool win32DebuggingEnabled);
}

[GeneratedComInterface]
[Guid("3d6f5f62-7538-11d3-8d5b-00104b35e7ef")]
public partial interface ICorDebugController
{
    [PreserveSig] int Stop(uint dwTimeoutIgnored);
    [PreserveSig] int Continue([MarshalAs(UnmanagedType.Bool)] bool fIsOutOfBand);
    [PreserveSig] int IsRunning([MarshalAs(UnmanagedType.Bool)] out bool pbRunning);
    [PreserveSig] int HasQueuedCallbacks(ICorDebugThread? pThread, [MarshalAs(UnmanagedType.Bool)] out bool pbQueued);
    [PreserveSig] int EnumerateThreads(out ICorDebugThreadEnum ppThreads);
    [PreserveSig] int SetAllThreadsDebugState(CorDebugThreadState state, ICorDebugThread? pExceptThisThread);
    [PreserveSig] int Detach();
    [PreserveSig] int Terminate(uint exitCode);
    [PreserveSig] int CanCommitChanges(uint cSnapshots, nint pSnapshots, out nint pError);
    [PreserveSig] int CommitChanges(uint cSnapshots, nint pSnapshots, out nint pError);
}

[GeneratedComInterface]
[Guid("3d6f5f64-7538-11d3-8d5b-00104b35e7ef")]
public partial interface ICorDebugProcess : ICorDebugController
{
    [PreserveSig] int GetID(out uint pdwProcessId);
    [PreserveSig] int GetHandle(out nint phProcessHandle);
    [PreserveSig] int GetThread(uint dwThreadId, out ICorDebugThread ppThread);
    [PreserveSig] int EnumerateObjects(out nint ppObjects);
    [PreserveSig] int IsTransitionStub(ulong address, [MarshalAs(UnmanagedType.Bool)] out bool pbTransitionStub);
    [PreserveSig] int IsOSSuspended(uint threadID, [MarshalAs(UnmanagedType.Bool)] out bool pbSuspended);
    [PreserveSig] int GetThreadContext(uint threadID, uint contextSize, nint context);
    [PreserveSig] int SetThreadContext(uint threadID, uint contextSize, nint context);
    [PreserveSig] int ReadMemory(ulong address, uint size, nint buffer, out nuint read);
    [PreserveSig] int WriteMemory(ulong address, uint size, nint buffer, out nuint written);
    [PreserveSig] int ClearCurrentException(uint threadID);
    [PreserveSig] int EnableLogMessages([MarshalAs(UnmanagedType.Bool)] bool fOnOff);
    [PreserveSig] unsafe int ModifyLogSwitch(char* pLogSwitchName, int lLevel);
    [PreserveSig] int EnumerateAppDomains(out nint ppAppDomains);
    [PreserveSig] int GetObject(out ICorDebugValue ppObject);
    [PreserveSig] int ThreadForFiberCookie(uint fiberCookie, out ICorDebugThread ppThread);
    [PreserveSig] int GetHelperThreadID(out uint pThreadID);
}

/// <summary>Heap inspection (implemented by the same object as <see cref="ICorDebugProcess"/>).</summary>
[GeneratedComInterface]
[Guid("21e9d9c0-fcb8-11df-8cff-0800200c9a66")]
public partial interface ICorDebugProcess5
{
    [PreserveSig] int GetGCHeapInformation(nint pHeapInfo);
    [PreserveSig] int EnumerateHeap(out ICorDebugHeapEnum ppObjects);
    [PreserveSig] int EnumerateHeapRegions(out nint ppRegions);
    [PreserveSig] int GetObject(ulong addr, out ICorDebugObjectValue pObject);
    [PreserveSig] int EnumerateGCReferences([MarshalAs(UnmanagedType.Bool)] bool enumerateWeakReferences, out nint ppEnum);
    [PreserveSig] int EnumerateHandles(uint types, out nint ppEnum);
    [PreserveSig] int GetTypeID(ulong obj, out CorTypeId pId);
    [PreserveSig] int GetTypeForTypeID(CorTypeId id, out ICorDebugType ppType);
}

[GeneratedComInterface]
[Guid("76D7DAB8-D044-11DF-9A15-7E29DFD72085")]
public partial interface ICorDebugHeapEnum : ICorDebugEnum
{
    [PreserveSig] unsafe int Next(uint celt, CorHeapObject* objects, out uint pceltFetched);
}

[GeneratedComInterface]
[Guid("3d6f5f63-7538-11d3-8d5b-00104b35e7ef")]
public partial interface ICorDebugAppDomain : ICorDebugController
{
    [PreserveSig] int GetProcess(out ICorDebugProcess ppProcess);
    [PreserveSig] int EnumerateAssemblies(out nint ppAssemblies);
    [PreserveSig] int GetModuleFromMetaDataInterface(nint pIMetaData, out ICorDebugModule ppModule);
    [PreserveSig] int EnumerateBreakpoints(out nint ppBreakpoints);
    [PreserveSig] int EnumerateSteppers(out nint ppSteppers);
    [PreserveSig] int IsAttached([MarshalAs(UnmanagedType.Bool)] out bool pbAttached);
    [PreserveSig] unsafe int GetName(uint cchName, out uint pcchName, char* szName);
    [PreserveSig] int GetObject(out ICorDebugValue ppObject);
    [PreserveSig] int Attach();
    [PreserveSig] int GetID(out uint pId);
}

[GeneratedComInterface]
[Guid("df59507c-d47a-459e-bce2-6427eac8fd06")]
public partial interface ICorDebugAssembly
{
    [PreserveSig] int GetProcess(out ICorDebugProcess ppProcess);
    [PreserveSig] int GetAppDomain(out ICorDebugAppDomain ppAppDomain);
    [PreserveSig] int EnumerateModules(out nint ppModules);
    [PreserveSig] unsafe int GetCodeBase(uint cchName, out uint pcchName, char* szName);
    [PreserveSig] unsafe int GetName(uint cchName, out uint pcchName, char* szName);
}

[GeneratedComInterface]
[Guid("dba2d8c1-e5c5-4069-8c13-10a7c6abf43d")]
public partial interface ICorDebugModule
{
    [PreserveSig] int GetProcess(out ICorDebugProcess ppProcess);
    [PreserveSig] int GetBaseAddress(out ulong pAddress);
    [PreserveSig] int GetAssembly(out ICorDebugAssembly ppAssembly);
    [PreserveSig] unsafe int GetName(uint cchName, out uint pcchName, char* szName);
    [PreserveSig] int EnableJITDebugging([MarshalAs(UnmanagedType.Bool)] bool bTrackJITInfo, [MarshalAs(UnmanagedType.Bool)] bool bAllowJitOpts);
    [PreserveSig] int EnableClassLoadCallbacks([MarshalAs(UnmanagedType.Bool)] bool bClassLoadCallbacks);
    [PreserveSig] int GetFunctionFromToken(uint methodDef, out ICorDebugFunction ppFunction);
    [PreserveSig] int GetFunctionFromRVA(ulong rva, out ICorDebugFunction ppFunction);
    [PreserveSig] int GetClassFromToken(uint typeDef, out ICorDebugClass ppClass);
    [PreserveSig] int CreateBreakpoint(out nint ppBreakpoint);
    [PreserveSig] int GetEditAndContinueSnapshot(out nint ppEditAndContinueSnapshot);
    [PreserveSig] int GetMetaDataInterface(nint riid, out nint ppObj);
    [PreserveSig] int GetToken(out uint pToken);
    [PreserveSig] int IsDynamic([MarshalAs(UnmanagedType.Bool)] out bool pDynamic);
    [PreserveSig] int GetGlobalVariableValue(uint fieldDef, out ICorDebugValue ppValue);
    [PreserveSig] int GetSize(out uint pcBytes);
    [PreserveSig] int IsInMemory([MarshalAs(UnmanagedType.Bool)] out bool pInMemory);
}

[GeneratedComInterface]
[Guid("7FCC5FB5-49C0-41de-9938-3B88B5B9ADD7")]
public partial interface ICorDebugModule2
{
    [PreserveSig] unsafe int SetJMCStatus([MarshalAs(UnmanagedType.Bool)] bool bIsJustMyCode, uint cTokens, uint* pTokens);
    [PreserveSig] int ApplyChanges(uint cbMetadata, nint pbMetadata, uint cbIL, nint pbIL);
    [PreserveSig] int SetJITCompilerFlags(CorDebugJitCompilerFlags dwFlags);
    [PreserveSig] int GetJITCompilerFlags(out CorDebugJitCompilerFlags pdwFlags);
    [PreserveSig] int ResolveAssembly(uint tkAssemblyRef, out ICorDebugAssembly ppAssembly);
}

[GeneratedComInterface]
[Guid("CC7BCAF3-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugFunction
{
    [PreserveSig] int GetModule(out ICorDebugModule ppModule);
    [PreserveSig] int GetClass(out ICorDebugClass ppClass);
    [PreserveSig] int GetToken(out uint pMethodDef);
    [PreserveSig] int GetILCode(out ICorDebugCode ppCode);
    [PreserveSig] int GetNativeCode(out ICorDebugCode ppCode);
    [PreserveSig] int CreateBreakpoint(out ICorDebugFunctionBreakpoint ppBreakpoint);
    [PreserveSig] int GetLocalVarSigToken(out uint pmdSig);
    [PreserveSig] int GetCurrentVersionNumber(out uint pnCurrentVersion);
}

[GeneratedComInterface]
[Guid("EF0C490B-94C3-4e4d-B629-DDC134C532D8")]
public partial interface ICorDebugFunction2
{
    [PreserveSig] int SetJMCStatus([MarshalAs(UnmanagedType.Bool)] bool bIsJustMyCode);
    [PreserveSig] int GetJMCStatus([MarshalAs(UnmanagedType.Bool)] out bool pbIsJustMyCode);
}

[GeneratedComInterface]
[Guid("CC7BCAF4-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugCode
{
    [PreserveSig] int IsIL([MarshalAs(UnmanagedType.Bool)] out bool pbIL);
    [PreserveSig] int GetFunction(out ICorDebugFunction ppFunction);
    [PreserveSig] int GetAddress(out ulong pStart);
    [PreserveSig] int GetSize(out uint pcBytes);
    [PreserveSig] int CreateBreakpoint(uint offset, out ICorDebugFunctionBreakpoint ppBreakpoint);
}

[GeneratedComInterface]
[Guid("CC7BCAE8-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugBreakpoint
{
    [PreserveSig] int Activate([MarshalAs(UnmanagedType.Bool)] bool bActive);
    [PreserveSig] int IsActive([MarshalAs(UnmanagedType.Bool)] out bool pbActive);
}

[GeneratedComInterface]
[Guid("CC7BCAE9-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugFunctionBreakpoint : ICorDebugBreakpoint
{
    [PreserveSig] int GetFunction(out ICorDebugFunction ppFunction);
    [PreserveSig] int GetOffset(out uint pnOffset);
}

[GeneratedComInterface]
[Guid("938c6d66-7fb6-4f69-b389-425b8987329b")]
public partial interface ICorDebugThread
{
    [PreserveSig] int GetProcess(out ICorDebugProcess ppProcess);
    [PreserveSig] int GetID(out uint pdwThreadId);
    [PreserveSig] int GetHandle(out nint phThreadHandle);
    [PreserveSig] int GetAppDomain(out ICorDebugAppDomain ppAppDomain);
    [PreserveSig] int SetDebugState(CorDebugThreadState state);
    [PreserveSig] int GetDebugState(out CorDebugThreadState pState);
    [PreserveSig] int GetUserState(out CorDebugUserState pState);
    [PreserveSig] int GetCurrentException(out ICorDebugValue? ppExceptionObject);
    [PreserveSig] int ClearCurrentException();
    [PreserveSig] int CreateStepper(out ICorDebugStepper ppStepper);
    [PreserveSig] int EnumerateChains(out nint ppChains);
    [PreserveSig] int GetActiveChain(out nint ppChain);
    [PreserveSig] int GetActiveFrame(out ICorDebugFrame? ppFrame);
    [PreserveSig] int GetRegisterSet(out nint ppRegisters);
    [PreserveSig] int CreateEval(out ICorDebugEval ppEval);
    [PreserveSig] int GetObject(out ICorDebugValue? ppObject);
}

[GeneratedComInterface]
[Guid("F8544EC3-5E4E-46c7-8D3E-A52B8405B1F5")]
public partial interface ICorDebugThread3
{
    [PreserveSig] int CreateStackWalk(out ICorDebugStackWalk ppStackWalk);
}

[GeneratedComInterface]
[Guid("A0647DE9-55DE-4816-929C-385271C64CF7")]
public partial interface ICorDebugStackWalk
{
    [PreserveSig] int GetContext(uint contextFlags, uint contextBufSize, out uint contextSize, nint contextBuf);
    [PreserveSig] int SetContext(int flag, uint contextSize, nint context);
    [PreserveSig] int Next();
    [PreserveSig] int GetFrame(out ICorDebugFrame? pFrame);
}

[GeneratedComInterface]
[Guid("CC7BCAEF-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugFrame
{
    [PreserveSig] int GetChain(out nint ppChain);
    [PreserveSig] int GetCode(out ICorDebugCode ppCode);
    [PreserveSig] int GetFunction(out ICorDebugFunction ppFunction);
    [PreserveSig] int GetFunctionToken(out uint pToken);
    [PreserveSig] int GetStackRange(out ulong pStart, out ulong pEnd);
    [PreserveSig] int GetCaller(out ICorDebugFrame? ppFrame);
    [PreserveSig] int GetCallee(out ICorDebugFrame? ppFrame);
    [PreserveSig] int CreateStepper(out ICorDebugStepper ppStepper);
}

[GeneratedComInterface]
[Guid("03E26311-4F76-11d3-88C6-006097945418")]
public partial interface ICorDebugILFrame : ICorDebugFrame
{
    [PreserveSig] int GetIP(out uint pnOffset, out CorDebugMappingResult pMappingResult);
    [PreserveSig] int SetIP(uint nOffset);
    [PreserveSig] int EnumerateLocalVariables(out ICorDebugValueEnum ppValueEnum);
    [PreserveSig] int GetLocalVariable(uint dwIndex, out ICorDebugValue ppValue);
    [PreserveSig] int EnumerateArguments(out ICorDebugValueEnum ppValueEnum);
    [PreserveSig] int GetArgument(uint dwIndex, out ICorDebugValue ppValue);
    [PreserveSig] int GetStackDepth(out uint pDepth);
    [PreserveSig] int GetStackValue(uint dwIndex, out ICorDebugValue ppValue);
    [PreserveSig] int CanSetIP(uint nOffset);
}

[GeneratedComInterface]
[Guid("5D88A994-6C30-479b-890F-BCEF88B129A5")]
public partial interface ICorDebugILFrame2
{
    [PreserveSig] int RemapFunction(uint newILOffset);
    [PreserveSig] int EnumerateTypeParameters(out ICorDebugTypeEnum ppTyParEnum);
}

[GeneratedComInterface]
[Guid("CC7BCAEC-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugStepper
{
    [PreserveSig] int IsActive([MarshalAs(UnmanagedType.Bool)] out bool pbActive);
    [PreserveSig] int Deactivate();
    [PreserveSig] int SetInterceptMask(CorDebugIntercept mask);
    [PreserveSig] int SetUnmappedStopMask(CorDebugUnmappedStop mask);
    [PreserveSig] int Step([MarshalAs(UnmanagedType.Bool)] bool bStepIn);
    [PreserveSig] unsafe int StepRange([MarshalAs(UnmanagedType.Bool)] bool bStepIn, CorDebugStepRange* ranges, uint cRangeCount);
    [PreserveSig] int StepOut();
    [PreserveSig] int SetRangeIL([MarshalAs(UnmanagedType.Bool)] bool bIL);
}

[GeneratedComInterface]
[Guid("C5B6E9C3-E7D1-4a8e-873B-7F047F0706F7")]
public partial interface ICorDebugStepper2
{
    [PreserveSig] int SetJMC([MarshalAs(UnmanagedType.Bool)] bool fIsJMCStepper);
}

[GeneratedComInterface]
[Guid("CC7BCAF7-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugValue
{
    [PreserveSig] int GetType(out CorElementType pType);
    [PreserveSig] int GetSize(out uint pSize);
    [PreserveSig] int GetAddress(out ulong pAddress);
    [PreserveSig] int CreateBreakpoint(out nint ppBreakpoint);
}

[GeneratedComInterface]
[Guid("5E0B54E7-D88A-4626-9420-A691E0A78B49")]
public partial interface ICorDebugValue2
{
    [PreserveSig] int GetExactType(out ICorDebugType ppType);
}

[GeneratedComInterface]
[Guid("CC7BCAF8-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugGenericValue : ICorDebugValue
{
    [PreserveSig] unsafe int GetValue(void* pTo);
    [PreserveSig] unsafe int SetValue(void* pFrom);
}

[GeneratedComInterface]
[Guid("CC7BCAF9-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugReferenceValue : ICorDebugValue
{
    [PreserveSig] int IsNull([MarshalAs(UnmanagedType.Bool)] out bool pbNull);
    [PreserveSig] int GetValue(out ulong pValue);
    [PreserveSig] int SetValue(ulong value);
    [PreserveSig] int Dereference(out ICorDebugValue ppValue);
    [PreserveSig] int DereferenceStrong(out ICorDebugValue ppValue);
}

[GeneratedComInterface]
[Guid("CC7BCAFA-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugHeapValue : ICorDebugValue
{
    [PreserveSig] int IsValid([MarshalAs(UnmanagedType.Bool)] out bool pbValid);
    [PreserveSig] int CreateRelocBreakpoint(out nint ppBreakpoint);
}

/// <summary>Heap objects can be pinned in place by a GC handle that survives Continue.</summary>
[GeneratedComInterface]
[Guid("E3AC4D6C-9CB7-43e6-96CC-B21540E5083C")]
public partial interface ICorDebugHeapValue2
{
    [PreserveSig] int CreateHandle(CorDebugHandleType type, out ICorDebugHandleValue ppHandle);
}

[GeneratedComInterface]
[Guid("029596E8-276B-46a1-9821-732E96BBB00B")]
public partial interface ICorDebugHandleValue : ICorDebugReferenceValue
{
    [PreserveSig] int GetHandleType(out CorDebugHandleType pType);
    [PreserveSig] int Dispose();
}

[GeneratedComInterface]
[Guid("CC7BCAFC-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugBoxValue : ICorDebugHeapValue
{
    [PreserveSig] int GetObject(out ICorDebugObjectValue ppObject);
}

[GeneratedComInterface]
[Guid("CC7BCAFD-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugStringValue : ICorDebugHeapValue
{
    [PreserveSig] int GetLength(out uint pcchString);
    [PreserveSig] unsafe int GetString(uint cchString, out uint pcchString, char* szString);
}

[GeneratedComInterface]
[Guid("0405B0DF-A660-11d2-BD02-0000F80849BD")]
public partial interface ICorDebugArrayValue : ICorDebugHeapValue
{
    [PreserveSig] int GetElementType(out CorElementType pType);
    [PreserveSig] int GetRank(out uint pnRank);
    [PreserveSig] int GetCount(out uint pnCount);
    [PreserveSig] unsafe int GetDimensions(uint cdim, uint* dims);
    [PreserveSig] int HasBaseIndicies([MarshalAs(UnmanagedType.Bool)] out bool pbHasBaseIndicies);
    [PreserveSig] unsafe int GetBaseIndicies(uint cdim, uint* indices);
    [PreserveSig] unsafe int GetElement(uint cdim, uint* indices, out ICorDebugValue ppValue);
    [PreserveSig] int GetElementAtPosition(uint nPosition, out ICorDebugValue ppValue);
}

[GeneratedComInterface]
[Guid("18AD3D6E-B7D2-11d2-BD04-0000F80849BD")]
public partial interface ICorDebugObjectValue : ICorDebugValue
{
    [PreserveSig] int GetClass(out ICorDebugClass ppClass);
    [PreserveSig] int GetFieldValue(ICorDebugClass pClass, uint fieldDef, out ICorDebugValue ppValue);
    [PreserveSig] int GetVirtualMethod(uint memberRef, out ICorDebugFunction ppFunction);
    [PreserveSig] int GetContext(out nint ppContext);
    [PreserveSig] int IsValueClass([MarshalAs(UnmanagedType.Bool)] out bool pbIsValueClass);
    [PreserveSig] int GetManagedCopy(out nint ppObject);
    [PreserveSig] int SetFromManagedCopy(nint pObject);
}

[GeneratedComInterface]
[Guid("CC7BCAF5-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugClass
{
    [PreserveSig] int GetModule(out ICorDebugModule pModule);
    [PreserveSig] int GetToken(out uint pTypeDef);
    [PreserveSig] int GetStaticFieldValue(uint fieldDef, ICorDebugFrame? pFrame, out ICorDebugValue ppValue);
}

[GeneratedComInterface]
[Guid("D613F0BB-ACE1-4c19-BD72-E4C08D5DA7F5")]
public partial interface ICorDebugType
{
    [PreserveSig] int GetType(out CorElementType ty);
    [PreserveSig] int GetClass(out ICorDebugClass ppClass);
    [PreserveSig] int EnumerateTypeParameters(out ICorDebugTypeEnum ppTyParEnum);
    [PreserveSig] int GetFirstTypeParameter(out ICorDebugType value);
    [PreserveSig] int GetBase(out ICorDebugType? pBase);
    [PreserveSig] int GetStaticFieldValue(uint fieldDef, ICorDebugFrame? pFrame, out ICorDebugValue ppValue);
    [PreserveSig] int GetRank(out uint pnRank);
}

[GeneratedComInterface]
[Guid("CC7BCAF6-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugEval
{
    [PreserveSig] unsafe int CallFunction(ICorDebugFunction pFunction, uint nArgs, nint* ppArgs);
    [PreserveSig] unsafe int NewObject(ICorDebugFunction pConstructor, uint nArgs, nint* ppArgs);
    [PreserveSig] int NewObjectNoConstructor(ICorDebugClass pClass);
    [PreserveSig] int NewString([MarshalAs(UnmanagedType.LPWStr)] string @string);
    [PreserveSig] unsafe int NewArray(CorElementType elementType, ICorDebugClass? pElementClass, uint rank, uint* dims, uint* lowBounds);
    [PreserveSig] int IsActive([MarshalAs(UnmanagedType.Bool)] out bool pbActive);
    [PreserveSig] int Abort();
    [PreserveSig] int GetResult(out ICorDebugValue? ppResult);
    [PreserveSig] int GetThread(out ICorDebugThread ppThread);
    [PreserveSig] int CreateValue(CorElementType elementType, ICorDebugClass? pElementClass, out ICorDebugValue ppValue);
}

[GeneratedComInterface]
[Guid("FB0D9CE7-BE66-4683-9D32-A42A04E2FD91")]
public partial interface ICorDebugEval2
{
    [PreserveSig] unsafe int CallParameterizedFunction(ICorDebugFunction pFunction, uint nTypeArgs, nint* ppTypeArgs, uint nArgs, nint* ppArgs);
    [PreserveSig] int CreateValueForType(ICorDebugType pType, out ICorDebugValue ppValue);
    [PreserveSig] unsafe int NewParameterizedObject(ICorDebugFunction pConstructor, uint nTypeArgs, nint* ppTypeArgs, uint nArgs, nint* ppArgs);
    [PreserveSig] unsafe int NewParameterizedObjectNoConstructor(ICorDebugClass pClass, uint nTypeArgs, nint* ppTypeArgs);
    [PreserveSig] unsafe int NewParameterizedArray(ICorDebugType pElementType, uint rank, uint* dims, uint* lowBounds);
    [PreserveSig] unsafe int NewStringWithLength(char* @string, uint uiLength);
    [PreserveSig] int RudeAbort();
}

// ---- Enumerators ----------------------------------------------------------------------
// Next() takes a raw array of interface pointers; ComEnumerator converts them.

[GeneratedComInterface]
[Guid("CC7BCB01-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugEnum
{
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out nint ppEnum);
    [PreserveSig] int GetCount(out uint pcelt);
}

[GeneratedComInterface]
[Guid("CC7BCB06-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugThreadEnum : ICorDebugEnum
{
    [PreserveSig] unsafe int Next(uint celt, nint* values, out uint pceltFetched);
}

[GeneratedComInterface]
[Guid("CC7BCB0A-8A68-11d2-983C-0000F808342D")]
public partial interface ICorDebugValueEnum : ICorDebugEnum
{
    [PreserveSig] unsafe int Next(uint celt, nint* values, out uint pceltFetched);
}

[GeneratedComInterface]
[Guid("10F27499-9DF2-43ce-8333-A321D7C99CB4")]
public partial interface ICorDebugTypeEnum : ICorDebugEnum
{
    [PreserveSig] unsafe int Next(uint celt, nint* values, out uint pceltFetched);
}

// ---- Callbacks ------------------------------------------------------------------------

[GeneratedComInterface]
[Guid("3d6f5f60-7538-11d3-8d5b-00104b35e7ef")]
public partial interface ICorDebugManagedCallback
{
    [PreserveSig] int Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint);
    [PreserveSig] int StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugStepper pStepper, CorDebugStepReason reason);
    [PreserveSig] int Break(ICorDebugAppDomain pAppDomain, ICorDebugThread thread);
    [PreserveSig] int Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, [MarshalAs(UnmanagedType.Bool)] bool unhandled);
    [PreserveSig] int EvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval);
    [PreserveSig] int EvalException(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval);
    [PreserveSig] int CreateProcess(ICorDebugProcess pProcess);
    [PreserveSig] int ExitProcess(ICorDebugProcess pProcess);
    [PreserveSig] int CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread thread);
    [PreserveSig] int ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread thread);
    [PreserveSig] int LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule);
    [PreserveSig] int UnloadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule);
    [PreserveSig] int LoadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c);
    [PreserveSig] int UnloadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c);
    [PreserveSig] int DebuggerError(ICorDebugProcess pProcess, int errorHR, uint errorCode);
    [PreserveSig] unsafe int LogMessage(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, char* pLogSwitchName, char* pMessage);
    [PreserveSig] unsafe int LogSwitch(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, uint ulReason, char* pLogSwitchName, char* pParentName);
    [PreserveSig] int CreateAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain);
    [PreserveSig] int ExitAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain);
    [PreserveSig] int LoadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly);
    [PreserveSig] int UnloadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly);
    [PreserveSig] int ControlCTrap(ICorDebugProcess pProcess);
    [PreserveSig] int NameChange(ICorDebugAppDomain? pAppDomain, ICorDebugThread? pThread);
    [PreserveSig] int UpdateModuleSymbols(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule, nint pSymbolStream);
    [PreserveSig] int EditAndContinueRemap(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction, [MarshalAs(UnmanagedType.Bool)] bool fAccurate);
    [PreserveSig] int BreakpointSetError(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint, uint dwError);
}

[GeneratedComInterface]
[Guid("250E5EEA-DB5C-4C76-B6F3-8C46F12E3203")]
public partial interface ICorDebugManagedCallback2
{
    [PreserveSig] int FunctionRemapOpportunity(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pOldFunction, ICorDebugFunction pNewFunction, uint oldILOffset);
    [PreserveSig] unsafe int CreateConnection(ICorDebugProcess pProcess, uint dwConnectionId, char* pConnName);
    [PreserveSig] int ChangeConnection(ICorDebugProcess pProcess, uint dwConnectionId);
    [PreserveSig] int DestroyConnection(ICorDebugProcess pProcess, uint dwConnectionId);
    [PreserveSig] int Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFrame? pFrame, uint nOffset, CorDebugExceptionCallbackType dwEventType, uint dwFlags);
    [PreserveSig] int ExceptionUnwind(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, CorDebugExceptionUnwindCallbackType dwEventType, uint dwFlags);
    [PreserveSig] int FunctionRemapComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction);
    [PreserveSig] int MDANotification(ICorDebugController pController, ICorDebugThread pThread, nint pMDA);
}
