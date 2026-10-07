using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Digger.Engine.Breakpoints;
using Digger.Engine.Evaluation;
using Digger.Engine.Infrastructure;
using Digger.Engine.Inspection;
using Digger.Engine.Runtime;
using Digger.Engine.Symbols;
using Digger.Interop.CorDebug;
using Digger.Interop.Native;

namespace Digger.Engine;

/// <summary>
/// One debugging session for one debuggee process.
///
/// Threading model: every public method must be called on the <see cref="EngineDispatcher"/>
/// thread, and ICorDebug callbacks are re-posted there too, so session state is
/// single-threaded. The only exception is while a func-eval runs (the engine thread is then
/// blocked waiting for it): callbacks are handled inline, see <see cref="HandleDuringEvaluation"/>.
/// </summary>
public sealed partial class DebugSession : ICallbackSink, IDisposable
{
    private readonly EngineDispatcher _dispatcher;
    private readonly IDebuggerEvents _events;
    private readonly ModuleRegistry _modules = new();
    private readonly FuncEvaluator _funcEval = new(TimeSpan.FromSeconds(5));
    private readonly ValueInspector _inspector;
    private readonly ExpressionEvaluator _expressions;
    private readonly BreakpointManager _breakpoints;
    private readonly VariableStore _variables = new();
    private readonly FrameStore _frames = new();
    private readonly Dictionary<int, string> _threadNames = [];
    private readonly SortedSet<int> _knownThreads = [];

    private SessionOptions _options = new();
    private SourceResolver _sources = new(null, sourceLink: true);
    private readonly HashSet<ulong> _symbolLookups = [];
    private LaunchOptions? _launchOptions;
    private ICorDebug? _corDebug;
    private ICorDebugProcess? _process;
    private RuntimeLauncher? _launcher;
    private LaunchShimChannel? _terminal;
    private ManagedCallback? _callback;
    private int _processId;
    private bool _attached;
    private bool _stopped;
    private bool _exited;
    private bool _configurationDone;
    private bool _breakOnAllExceptions;
    private bool _breakOnUnhandledExceptions = true;
    private ExceptionFilter _thrownFilter = ExceptionFilter.All;
    private bool _entryBreakpointSet;
    private UserBreakpoint? _entryBreakpoint;
    private int _lastStoppedThreadId;
    private bool _othersFrozen;
    private readonly List<GotoTarget> _gotoTargets = [];

    private sealed record GotoTarget(int ThreadId, uint MethodToken, uint Offset);
    private string _lastExceptionBreakMode = "always";
    private readonly ManualResetEventSlim _exitCodeKnown = new();
    private int _exitCode;

    public DebugSession(EngineDispatcher dispatcher, IDebuggerEvents events)
    {
        _dispatcher = dispatcher;
        _events = events;
        _inspector = new ValueInspector(_modules, _funcEval);
        _expressions = new ExpressionEvaluator(_inspector);
        _breakpoints = new BreakpointManager(_modules);
    }

    public bool HasProcess => _process is not null && !_exited;

    public bool IsStopped => _stopped;

    public bool IsAttached => _attached;

    // ---- Lifecycle --------------------------------------------------------------------

    /// <summary>Creates the debuggee suspended; it starts running at <see cref="ConfigurationDone"/>.</summary>
    public void Launch(LaunchOptions launch, SessionOptions options)
    {
        EnsureNoProcess();
        _options = options;
        _launchOptions = launch;
        ApplyOptions(options);

        var program = Path.GetFullPath(launch.Program);
        if (!File.Exists(program))
        {
            throw new FileNotFoundException($"Program '{program}' does not exist. Did you build it?", program);
        }

        string[] command;
        if (program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var host = DotnetLocator.Find(launch.DotnetPath)
                ?? throw new FileNotFoundException("Could not find the 'dotnet' host. Set 'dotnetPath' or DOTNET_ROOT.");
            command = [host, program, .. launch.Arguments];
        }
        else
        {
            command = [program, .. launch.Arguments];
        }

        var workingDirectory = launch.WorkingDirectory is { Length: > 0 } cwd
            ? Path.GetFullPath(cwd)
            : Path.GetDirectoryName(program) ?? Environment.CurrentDirectory;

        if (launch.Terminal is { } terminal)
        {
            LaunchInTerminal(terminal, Path.GetFileName(program), workingDirectory, command, launch.Environment);
        }
        else if (launch.IsolateFromTerminal && !OperatingSystem.IsWindows())
        {
            LaunchIsolated(CommandLine.Build(command[0], command[1..]), workingDirectory, launch.Environment);
        }
        else
        {
            _launcher = RuntimeLauncher.Launch(CommandLine.Build(command[0], command[1..]), workingDirectory, launch.Environment, OnRuntimeStarted);
            _processId = (int)_launcher.ProcessId;
            StartExitWatcher(_processId);
        }

        _events.ProcessStarted(Path.GetFileName(program), _processId, attached: false);

        if (_configurationDone)
        {
            _launcher?.Resume();
        }
    }

    /// <summary>
    /// Launches with fd 0 pointing at /dev/null (the child inherits our descriptors at fork)
    /// and moves the still-suspended child into its own process group, so the terminal's
    /// Ctrl-C and keystrokes go to digger. The PAL holds a suspended child before exec,
    /// which is what lets setpgid succeed.
    /// </summary>
    private void LaunchIsolated(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string?>? environment)
    {
        var savedStdin = Libc.dup(0);
        var devNull = Libc.open("/dev/null", Libc.O_RDONLY);
        try
        {
            if (savedStdin >= 0 && devNull >= 0)
            {
                _ = Libc.dup2(devNull, 0);
            }

            _launcher = RuntimeLauncher.Launch(commandLine, workingDirectory, environment, OnRuntimeStarted);
        }
        finally
        {
            if (savedStdin >= 0)
            {
                _ = Libc.dup2(savedStdin, 0);
                _ = Libc.close(savedStdin);
            }

            if (devNull >= 0)
            {
                _ = Libc.close(devNull);
            }
        }

        _processId = (int)_launcher.ProcessId;
        if (Libc.setpgid(_processId, _processId) != 0)
        {
            Log.Warn($"setpgid({_processId}) failed (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}); Ctrl-C will reach the program too");
        }

        StartExitWatcher(_processId);
    }

    /// <summary>
    /// Runs the program in the editor's terminal through the launch shim (see
    /// <see cref="LaunchShimChannel"/>). Blocks until the shim reports the process id.
    /// </summary>
    private void LaunchInTerminal(ITerminalHost terminal, string name, string workingDirectory, string[] command, IReadOnlyDictionary<string, string?>? environment)
    {
        var channel = LaunchShimChannel.Create();
        try
        {
            string[] arguments = [.. terminal.ShimCommand, "--launch-shim=" + channel.SocketPath, "--", .. command];
            terminal.RunInTerminal($"Digger: {name}", workingDirectory, arguments, environment, channel.Fail);
            var processId = channel.WaitForProcess(TimeSpan.FromSeconds(30));
            _launcher = RuntimeLauncher.ForSuspendedProcess((uint)processId, channel.Resume, OnRuntimeStarted);
            _processId = processId;
            channel.WatchExit(code =>
            {
                _exitCode = code;
                _exitCodeKnown.Set();
            });
            _terminal = channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>Attaches to a running .NET process.</summary>
    public void Attach(int processId, SessionOptions options)
    {
        EnsureNoProcess();
        _options = options;
        ApplyOptions(options);
        _attached = true;
        _processId = processId;
        var corDebug = RuntimeLauncher.CreateForAttach((uint)processId);
        ConnectToRuntime(corDebug);
        _events.ProcessStarted($"process {processId}", processId, attached: true);
    }

    private void ApplyOptions(SessionOptions options)
    {
        _inspector.EvaluateProperties = options.EvaluateProperties;
        _inspector.LazyProperties = options.LazyProperties;
        _sources = new SourceResolver(options.SourceFileMap, options.SourceLink);
    }

    /// <summary>The client finished sending breakpoints: let a launched process run.</summary>
    public void ConfigurationDone()
    {
        _configurationDone = true;
        _launcher?.Resume();
    }

    private void EnsureNoProcess()
    {
        if (_process is not null || _launcher is not null)
        {
            throw new InvalidOperationException("A debuggee is already running in this session.");
        }
    }

    /// <summary>dbgshim thread: the runtime in the debuggee is blocked until this returns.</summary>
    private void OnRuntimeStarted(ICorDebug? corDebug, int hr)
    {
        _dispatcher.Invoke(() =>
        {
            if (corDebug is null)
            {
                _events.Output("stderr", $"Failed to attach to the .NET runtime in the debuggee (0x{hr:X8}).\n");
                return;
            }

            try
            {
                ConnectToRuntime(corDebug);
            }
            catch (Exception ex)
            {
                Log.Error("Attaching to the runtime failed", ex);
                _events.Output("stderr", $"Failed to attach to the .NET runtime: {ex.Message}\n");
            }
        });
    }

    private void ConnectToRuntime(ICorDebug corDebug)
    {
        _corDebug = corDebug;
        HResults.Check(corDebug.Initialize());
        _callback = new ManagedCallback(this);
        HResults.Check(corDebug.SetManagedHandler(_callback));
        HResults.Check(corDebug.DebugActiveProcess((uint)_processId, win32Attach: false, out var process));
        _process = process;
        _funcEval.Process = process;
        Log.Info($"Attached ICorDebug to process {_processId}");
    }

    /// <summary>Ends the session: kills a launched debuggee or detaches from an attached one.</summary>
    public void Disconnect(bool? terminateDebuggee)
    {
        var terminate = terminateDebuggee ?? !_attached;
        if (_process is not null && !_exited)
        {
            if (terminate)
            {
                Terminate();
            }
            else
            {
                Detach();
            }
        }

        ShutdownCorDebug();
    }

    /// <summary>Kills the debuggee.</summary>
    public void Terminate()
    {
        if (_process is null || _exited)
        {
            if (_launcher is not null && _processId > 0 && _process is null)
            {
                // The runtime never started; kill the suspended process directly.
                _ = Libc.kill(_processId, Libc.SIGKILL);
            }

            return;
        }

        var hr = _process.Terminate(1);
        if (hr < 0)
        {
            Log.Warn($"ICorDebugProcess.Terminate failed (0x{hr:X8}); sending SIGKILL");
            _ = Libc.kill(_processId, Libc.SIGKILL);
        }
    }

    private void Detach()
    {
        if (_process is null)
        {
            return;
        }

        if (!_stopped)
        {
            _ = _process.Stop(0);
            _stopped = true;
        }

        CancelStepper();
        _breakpoints.DeactivateAll();
        var hr = _process.Detach();
        if (hr < 0)
        {
            Log.Warn($"Detach failed (0x{hr:X8})");
        }

        _process = null;
        _funcEval.Process = null;
    }

    private void ShutdownCorDebug()
    {
        if (_corDebug is not null)
        {
            _ = _corDebug.Terminate();
            _corDebug = null;
        }

        _launcher?.Dispose();
        _launcher = null;
        _terminal?.Dispose();
        _terminal = null;
    }

    // ---- Execution control ------------------------------------------------------------

    /// <param name="threadId">The thread to resume.</param>
    /// <param name="singleThread">Resume only <paramref name="threadId"/>; the others stay frozen until the next stop.</param>
    public void Continue(int threadId = 0, bool singleThread = false)
    {
        if (_process is null || !_stopped)
        {
            return;
        }

        CancelStepper();
        if (singleThread && threadId != 0)
        {
            FreezeOtherThreads(GetThread(threadId));
        }

        Resume();
    }

    private void FreezeOtherThreads(ICorDebugThread thread)
    {
        _ = _process!.SetAllThreadsDebugState(CorDebugThreadState.Suspend, thread);
        _ = thread.SetDebugState(CorDebugThreadState.Run);
        _othersFrozen = true;
    }

    public void Pause()
    {
        if (_process is null || _stopped || _exited)
        {
            return;
        }

        HResults.Check(_process.Stop(0));
        var threadId = PickThreadForPause();
        EnterStopped(new StopInfo(StopReason.Pause, threadId) { Description = "Paused" });
    }

    public void Step(int threadId, StepKind kind, bool singleThread = false)
    {
        RequireStopped();
        var thread = GetThread(threadId);
        CancelStepper();
        _stepAttempts = 0;
        if (singleThread)
        {
            FreezeOtherThreads(thread);
        }

        if (kind == StepKind.Out && TryStartAsyncStepOut(thread, threadId))
        {
            Resume();
            return;
        }

        if (!StartStep(thread, kind))
        {
            throw new InvalidOperationException("Cannot step from the current location.");
        }

        Resume();
    }

    private void Resume()
    {
        _frames.Clear();
        _variables.Clear();
        _gotoTargets.Clear();
        _stopped = false;
        var hr = _process!.Continue(false);
        if (hr < 0)
        {
            Log.Warn($"Continue failed (0x{hr:X8})");
        }
    }

    private void EnterStopped(StopInfo info)
    {
        _stopped = true;
        if (_othersFrozen)
        {
            // Threads frozen by a single-thread continue/step thaw at the next stop.
            _ = _process?.SetAllThreadsDebugState(CorDebugThreadState.Run, null);
            _othersFrozen = false;
        }

        _lastStoppedThreadId = info.ThreadId;
        _events.Stopped(info);
    }

    /// <summary>Converts a stop-worthy event into a stop; returns false if already stopped.</summary>
    private bool StopFor(StopReason reason, ICorDebugThread? thread, string? description = null, string? text = null, IReadOnlyList<int>? hitIds = null)
    {
        if (_stopped)
        {
            return false;
        }

        CancelStepper();
        var threadId = 0;
        if (thread is not null && thread.GetID(out var id) >= 0)
        {
            threadId = (int)id;
        }

        EnterStopped(new StopInfo(reason, threadId)
        {
            Description = description,
            Text = text,
            HitBreakpointIds = hitIds ?? [],
        });
        return true;
    }

    private void RequireStopped()
    {
        if (_process is null || !_stopped)
        {
            throw new InvalidOperationException("The debuggee is not stopped.");
        }
    }

    private ICorDebugThread GetThread(int threadId)
    {
        // Task frames (see GetAsyncTasks) use negative ids; their code runs on the stopped thread.
        threadId = threadId < 0 ? _lastStoppedThreadId : threadId;
        if (_process is null || _process.GetThread((uint)threadId, out var thread) < 0)
        {
            throw new InvalidOperationException($"Thread {threadId} does not exist.");
        }

        return thread;
    }

    private int PickThreadForPause()
    {
        if (_process is null)
        {
            return 0;
        }

        var threads = _process.GetThreads();
        foreach (var thread in threads)
        {
            if (thread.GetActiveFrame(out var frame) >= 0 && frame is ICorDebugILFrame
                && thread.GetID(out var id) >= 0)
            {
                return (int)id;
            }
        }

        return threads.Count > 0 && threads[0].GetID(out var first) >= 0 ? (int)first : 0;
    }

    // ---- Set next statement -----------------------------------------------------------

    /// <summary>Lines of the current method (top frame) that execution can be moved to.</summary>
    public List<GotoTargetView> GetGotoTargets(string path, int line)
    {
        RequireStopped();
        var frames = GetFrames(_lastStoppedThreadId);
        if (frames.Count == 0 || frames[0].Module?.Symbols is not { } symbols)
        {
            return [];
        }

        var top = frames[0];
        var documentPath = _sources.ToDocumentPath(path);
        var points = symbols.GetVisiblePoints(top.MethodToken).FindAll(p => SourcePathMatcher.Score(documentPath, p.Location.Document) > 0);
        // Only lines inside the current method qualify (SetIP cannot leave the frame).
        var firstLine = int.MaxValue;
        var lastLine = 0;
        foreach (var (_, location) in points)
        {
            firstLine = Math.Min(firstLine, location.StartLine);
            lastLine = Math.Max(lastLine, location.EndLine);
        }

        if (line < firstLine || line > lastLine)
        {
            return [];
        }

        var targetLine = int.MaxValue;
        foreach (var (_, location) in points)
        {
            if (location.StartLine >= line && location.StartLine < targetLine)
            {
                targetLine = location.StartLine;
            }
        }

        (uint Offset, SourceLocation Location)? best = null;
        foreach (var point in points)
        {
            if (point.Location.StartLine == targetLine && (best is null || point.Offset < best.Value.Offset))
            {
                best = point;
            }
        }

        if (best is not { } target)
        {
            return [];
        }

        _gotoTargets.Add(new GotoTarget(top.ThreadId, top.MethodToken, target.Offset));
        var where = target.Location;
        return [new GotoTargetView(_gotoTargets.Count, where.StartLine, where.StartColumn, where.EndLine, where.EndColumn)];
    }

    /// <summary>
    /// Moves the instruction pointer of the thread's top frame ("set next statement"). Returns
    /// the thread id; the caller reports the new position with a <see cref="StopReason.Goto"/> stop.
    /// </summary>
    public int Goto(int threadId, int targetId)
    {
        RequireStopped();
        if (targetId <= 0 || targetId > _gotoTargets.Count)
        {
            throw new InvalidOperationException("Unknown goto target.");
        }

        var target = _gotoTargets[targetId - 1];
        var thread = GetThread(threadId == 0 ? target.ThreadId : threadId);
        if (thread.GetActiveFrame(out var frame) < 0 || frame is not ICorDebugILFrame il
            || il.GetFunctionToken(out var token) < 0 || token != target.MethodToken)
        {
            throw new InvalidOperationException("The next statement can only be set within the current method.");
        }

        var hr = il.CanSetIP(target.Offset);
        if (hr != HResults.S_OK)
        {
            throw new InvalidOperationException($"The next statement cannot be set to that line (0x{hr:X8}).");
        }

        HResults.Check(il.SetIP(target.Offset));
        _frames.Clear();
        _variables.Clear();
        _gotoTargets.Clear();
        return thread.GetID(out var id) >= 0 ? (int)id : threadId;
    }

    // ---- Exceptions -------------------------------------------------------------------

    /// <summary>
    /// <paramref name="thrownFilter"/> narrows <paramref name="breakOnAll"/> to some exception
    /// types (see <see cref="ExceptionFilter"/>); unhandled exceptions are never filtered.
    /// </summary>
    public void SetExceptionFilters(bool breakOnAll, bool breakOnUnhandled, ExceptionFilter? thrownFilter = null)
    {
        _breakOnAllExceptions = breakOnAll;
        _breakOnUnhandledExceptions = breakOnUnhandled;
        _thrownFilter = thrownFilter ?? ExceptionFilter.All;
    }

    // ---- Threads ----------------------------------------------------------------------

    public List<ThreadView> GetThreads()
    {
        var result = new List<ThreadView>();
        if (_process is null || _exited)
        {
            return result;
        }

        // Enumerating threads needs a synchronized process; while running, report the
        // threads seen through CreateThread/ExitThread callbacks.
        if (!_stopped)
        {
            foreach (var id in _knownThreads)
            {
                result.Add(new ThreadView(id, _threadNames.GetValueOrDefault(id) ?? $"Thread #{id}"));
            }

            return result;
        }

        var threads = _process.GetThreads();
        for (var i = 0; i < threads.Count; i++)
        {
            if (threads[i].GetID(out var id) < 0)
            {
                continue;
            }

            if (!_threadNames.TryGetValue((int)id, out var name))
            {
                name = ReadThreadName(threads[i]) ?? (i == 0 ? "Main Thread" : $"Thread #{id}");
                _threadNames[(int)id] = name;
            }

            result.Add(new ThreadView((int)id, name));
        }

        return result;
    }

    private string? ReadThreadName(ICorDebugThread thread)
    {
        // Reading a field is safe while running only if stopped; skip otherwise.
        if (!_stopped || thread.GetObject(out var threadObject) < 0 || threadObject is null)
        {
            return null;
        }

        var info = ValueInspector.Analyze(threadObject);
        if (_inspector.FindField(info, "_name") is not { } nameValue)
        {
            return null;
        }

        var nameInfo = ValueInspector.Analyze(nameValue);
        return (nameInfo.Target as ICorDebugStringValue)?.GetStringValue(256);
    }

    // ---- Modules ----------------------------------------------------------------------

    public List<ModuleView> GetModules()
    {
        var result = new List<ModuleView>();
        foreach (var module in _modules.Snapshot())
        {
            result.Add(ToView(module));
        }

        return result;
    }

    private static ModuleView ToView(LoadedModule module) => new(module.Id, module.Name, module.Path)
    {
        IsOptimized = module.Metadata?.IsOptimized ?? true,
        IsUserCode = module.IsUserCode,
        HasSymbols = module.Symbols is not null,
        SymbolPath = module.Symbols?.PdbPath,
    };

    // ---- Breakpoints ------------------------------------------------------------------

    public List<BreakpointView> SetSourceBreakpoints(string path, IReadOnlyList<SourceBreakpointSpec> specs)
    {
        // Breakpoints in mapped or downloaded files bind against the path recorded in the PDB,
        // but are reported back with the editor's path.
        var result = new List<BreakpointView>();
        foreach (var breakpoint in _breakpoints.SetSourceBreakpoints(_sources.ToDocumentPath(path), specs))
        {
            result.Add(ToView(breakpoint) with { Path = path });
        }

        return result;
    }

    public List<BreakpointView> SetFunctionBreakpoints(IReadOnlyList<FunctionBreakpointSpec> specs)
    {
        var result = new List<BreakpointView>();
        foreach (var breakpoint in _breakpoints.SetFunctionBreakpoints(specs))
        {
            result.Add(ToView(breakpoint));
        }

        return result;
    }

    private BreakpointView ToView(UserBreakpoint breakpoint)
    {
        var location = breakpoint.Location;
        return new BreakpointView(breakpoint.Id, breakpoint.IsVerified)
        {
            Message = breakpoint.Message,
            Path = location is null ? breakpoint.Path : _sources.Resolve(null, location.Value.Document),
            Line = location?.StartLine ?? (breakpoint.Line > 0 ? breakpoint.Line : null),
            Column = location?.StartColumn,
            EndLine = location?.EndLine,
            EndColumn = location?.EndColumn,
        };
    }

    // ---- Exit -------------------------------------------------------------------------

    private bool OnExitProcess()
    {
        _exited = true;
        _stopped = false;
        _frames.Clear();
        _variables.Clear();
        _funcEval.Process = null;
        var exitCode = ReapExitCode();
        Log.Info($"Process {_processId} exited with code {exitCode}");
        _events.Exited(exitCode);
        _events.Terminated();
        return true;
    }

    /// <summary>
    /// The debuggee is our child (dbgshim forks it), but the PAL inside mscordbi reaps it
    /// to detect the exit, so a plain waitpid after ExitProcess usually finds nothing. A
    /// thread blocked in waitid(WNOWAIT) sees the status first without consuming it.
    /// </summary>
    private void StartExitWatcher(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var watcher = new Thread(() =>
        {
            if (Libc.TryPeekExitCode(processId, out var code))
            {
                _exitCode = code;
                _exitCodeKnown.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Digger exit watcher",
        };
        watcher.Start();
    }

    private int ReapExitCode()
    {
        if (_attached || _processId <= 0)
        {
            return 0;
        }

        if (_exitCodeKnown.Wait(TimeSpan.FromSeconds(2)))
        {
            return _exitCode;
        }

        // Fallback: maybe nobody reaped it yet.
        if (!OperatingSystem.IsWindows() && Libc.waitpid(_processId, out var status, Libc.WNOHANG) == _processId)
        {
            return Libc.DecodeExitStatus(status);
        }

        Log.Warn("Exit code of the debuggee is unknown");
        return 0;
    }

    public void Dispose()
    {
        _exitCodeKnown.Dispose();
        _modules.Dispose();
        _funcEval.Dispose();
        _launcher?.Dispose();
        _terminal?.Dispose();
    }
}
