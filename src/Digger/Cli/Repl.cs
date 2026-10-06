using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Digger.Engine;
using Digger.Engine.Infrastructure;
using Digger.Engine.Symbols;
using Digger.Interop.CorDebug;
using Digger.Interop.Native;

namespace Digger.Cli;

/// <summary>What the terminal debugger runs: a program (debug/exec) or a process to attach to.</summary>
internal sealed record DebugTarget
{
    public string? Program { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public int ProcessId { get; init; }

    /// <summary>Set for <c>digger debug</c>: lets <c>rebuild</c> build the project again.</summary>
    public BuildSettings? Build { get; init; }
}

/// <summary>
/// The interactive, Delve-style terminal debugger behind <c>digger debug</c>, <c>exec</c> and
/// <c>attach</c>. It runs on the main thread and drives <see cref="DebugSession"/> on the
/// engine thread; the debuggee shares the terminal for output.
/// </summary>
internal sealed partial class Repl : IDisposable
{
    private enum State
    {
        NotStarted,
        Running,
        Stopped,
        Exited,
    }

    private readonly EngineDispatcher _dispatcher = new();
    private readonly LineEditor _editor;
    private readonly BlockingCollection<RunEvent> _runEvents = [];
    private readonly Lock _gate = new();
    private DebugTarget _target;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Disposed on the engine thread by EndSession.")]
    private DebugSession? _session;
    private int _generation;
    private volatile State _state;
    private int _processId;
    private int _exitCode;
    private bool _quit;
    private ModuleMetadata? _program;
    private PosixSignalRegistration? _interrupt;

    // Where the user is looking, valid while stopped.
    private int _threadId;
    private int _frameIndex;
    private List<FrameView>? _frames;
    private StopInfo? _lastStop;

    /// <summary>A stop (or, with a null stop, the end of the process).</summary>
    private sealed record RunEvent(StopInfo? Stop);

    public Repl(DebugTarget target)
    {
        _target = target;
        _editor = new LineEditor(HistoryPath()) { Completer = Complete };
        _commands = CreateCommands();
        OpenProgram();
    }

    private bool IsAttach => _target.ProcessId > 0;

    /// <summary>Runs the command loop until the user quits; returns the process exit code.</summary>
    public int Run(IReadOnlyList<string> initialCommands)
    {
        if (!OperatingSystem.IsWindows())
        {
            _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnInterrupt);
        }

        Console.WriteLine("Type 'help' for list of commands.");
        if (IsAttach && !TryAttach())
        {
            return 1;
        }

        foreach (var command in initialCommands)
        {
            Console.WriteLine(Ansi.Dim("(digger) " + command));
            Execute(command);
            if (_quit)
            {
                return 0;
            }
        }

        while (!_quit)
        {
            var line = _editor.ReadLine(Ansi.Bold("(digger) "));
            if (line is null)
            {
                Quit(force: false);
                break;
            }

            Execute(line);
        }

        return 0;
    }

    // ---- Session lifecycle --------------------------------------------------------------

    private void OpenProgram()
    {
        _program?.Dispose();
        _program = null;
        if (_target.Program is not { } program)
        {
            return;
        }

        // For an apphost executable the managed code lives in the .dll next to it.
        var assembly = program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? program : Path.ChangeExtension(program, ".dll");
        _program = ModuleMetadata.TryOpen(assembly, loadSymbols: true);
        if (_program is not null && _program.Symbols is null)
        {
            Console.WriteLine(Ansi.Yellow($"warning: no symbols (.pdb) found for {Path.GetFileName(assembly)}; build in Debug configuration with portable PDBs"));
        }
    }

    private DebugSession NewSession()
    {
        var generation = Interlocked.Increment(ref _generation);
        var session = new DebugSession(_dispatcher, new Events(this, generation));
        _session = session;
        return session;
    }

    /// <summary>Creates the debuggee, applies breakpoints and lets it run.</summary>
    private void Start(bool stopAtEntry)
    {
        var program = _target.Program ?? throw new InvalidOperationException("Nothing to run.");
        DrainEvents();
        ResetHitCounts();
        var session = NewSession();
        Engine(() =>
        {
            ApplyAllBreakpoints(session);
            session.Launch(
                new LaunchOptions(program)
                {
                    Arguments = _target.Arguments,
                    WorkingDirectory = _target.WorkingDirectory,
                    StopAtEntry = stopAtEntry,
                    IsolateFromTerminal = true,
                },
                new SessionOptions());
            session.ConfigurationDone();
        });
        _state = State.Running;
        WaitForStop();
    }

    private bool TryAttach()
    {
        var session = NewSession();
        try
        {
            Engine(() =>
            {
                ApplyAllBreakpoints(session);
                session.Attach(_target.ProcessId, new SessionOptions());
                session.ConfigurationDone();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or CorDebugException)
        {
            Console.Error.WriteLine($"digger: could not attach to process {_target.ProcessId}: {ex.Message}");
            return false;
        }

        _processId = _target.ProcessId;
        _state = State.Running;

        // The runtime replays module loads after attaching, with no "done" signal: wait for
        // the module list to settle so the pause lands with symbols available.
        var stableSince = Environment.TickCount64;
        var deadline = stableSince + 5000;
        var count = -1;
        while (Environment.TickCount64 < deadline && Environment.TickCount64 - stableSince < 300)
        {
            var current = Engine(() => session.GetModules().Count);
            if (current != count || current == 0)
            {
                count = current;
                stableSince = Environment.TickCount64;
            }

            Thread.Sleep(50);
        }

        // Like Delve, attaching stops the process so breakpoints can be set.
        Engine(session.Pause);
        WaitForStop();
        ReportStop();
        return true;
    }

    /// <summary>Kills (or detaches from) the debuggee and forgets the session.</summary>
    private void EndSession(bool terminate)
    {
        if (_session is not { } session)
        {
            return;
        }

        var processId = _processId;
        _session = null;
        _ = Interlocked.Increment(ref _generation); // events from the old session are ignored from now on
        Engine(() =>
        {
            session.Disconnect(terminate);
            session.Dispose();
        });

        if (terminate && processId > 0 && !OperatingSystem.IsWindows())
        {
            // Let the old process go away before a new one starts writing to the terminal.
            for (var i = 0; i < 50 && Libc.kill(processId, 0) == 0; i++)
            {
                Thread.Sleep(20);
            }
        }

        DrainEvents();
        ClearStop();
        _state = State.NotStarted;
    }

    private void Quit(bool force)
    {
        if (IsAttach && _session is not null && _state is State.Stopped or State.Running)
        {
            var kill = force || Confirm("Would you like to kill the process? [Y/n] ", defaultYes: true);
            EndSession(terminate: kill);
        }
        else
        {
            EndSession(terminate: true);
        }

        _quit = true;
    }

    private bool Confirm(string question, bool defaultYes)
    {
        var answer = _editor.ReadLine(question)?.Trim().ToUpperInvariant();
        return answer switch
        {
            null or "" => defaultYes,
            _ => answer.StartsWith('Y'),
        };
    }

    // ---- Running and stopping -----------------------------------------------------------

    /// <summary>Resumes the debuggee with <paramref name="resume"/> and waits for it to stop or exit.</summary>
    private StopInfo? Resume(Action<DebugSession> resume)
    {
        var session = RequireStopped();
        DrainEvents();
        ClearStop();
        _state = State.Running;
        try
        {
            Engine(() => resume(session));
        }
        catch
        {
            _state = State.Stopped;
            throw;
        }

        return WaitForStop();
    }

    private StopInfo? WaitForStop()
    {
        var next = _runEvents.Take();
        if (next.Stop is not { } stop)
        {
            _state = State.Exited;
            Console.WriteLine($"Process {_processId} has exited with status {_exitCode}");
            return null;
        }

        _state = State.Stopped;
        _lastStop = stop;
        _threadId = stop.ThreadId;
        _frameIndex = 0;
        _frames = null;
        CountHits(stop);
        return stop;
    }

    private void DrainEvents()
    {
        while (_runEvents.TryTake(out _))
        {
        }
    }

    private void ClearStop()
    {
        _frames = null;
        _frameIndex = 0;
    }

    /// <summary>Ctrl-C pauses a running debuggee; at the prompt the line editor handles it.</summary>
    private void OnInterrupt(PosixSignalContext context)
    {
        context.Cancel = true;
        if (_state == State.Running && _session is { } session)
        {
            Console.WriteLine();
            _dispatcher.Post(session.Pause);
        }
    }

    // ---- Engine access ------------------------------------------------------------------

    private DebugSession RequireSession() => _state switch
    {
        State.NotStarted => throw new InvalidOperationException("The program is not running yet; use 'continue', 'next' or 'step' to start it."),
        State.Exited => throw new InvalidOperationException($"Process {_processId} has exited with status {_exitCode}; use 'restart' to run it again."),
        _ => _session ?? throw new InvalidOperationException("No debug session."),
    };

    private DebugSession RequireStopped()
    {
        var session = RequireSession();
        return _state == State.Stopped ? session : throw new InvalidOperationException("The program is running.");
    }

    private void Engine(Action work) => _dispatcher.Invoke(work);

    private T Engine<T>(Func<T> work)
    {
        T result = default!;
        _dispatcher.Invoke(() => result = work());
        return result;
    }

    /// <summary>Frames of the selected thread (without async call stack labels), cached per stop.</summary>
    private List<FrameView> Frames()
    {
        if (_frames is null)
        {
            var session = RequireStopped();
            var (frames, _) = Engine(() => session.GetStackTrace(_threadId, 0, 0));
            _frames = frames.FindAll(static frame => !frame.IsLabel);
        }

        return _frames;
    }

    private FrameView? CurrentFrame()
    {
        var frames = Frames();
        return _frameIndex < frames.Count ? frames[_frameIndex] : null;
    }

    // ---- Engine events ------------------------------------------------------------------

    /// <summary>Receives one session's events; anything from an ended session is dropped.</summary>
    private sealed class Events(Repl repl, int generation) : IDebuggerEvents
    {
        private bool Current => Volatile.Read(ref repl._generation) == generation;

        public void ProcessStarted(string name, int processId, bool attached)
        {
            if (Current)
            {
                repl._processId = processId;
            }
        }

        public void Stopped(StopInfo info)
        {
            if (Current)
            {
                repl._runEvents.Add(new RunEvent(info));
            }
        }

        public void Exited(int exitCode)
        {
            if (Current)
            {
                repl._exitCode = exitCode;
            }
        }

        public void Terminated()
        {
            if (Current)
            {
                repl._runEvents.Add(new RunEvent(null));
            }
        }

        public void BreakpointChanged(BreakpointView breakpoint)
        {
            if (Current)
            {
                repl.OnBreakpointChanged(breakpoint);
            }
        }

        public void Output(string category, string text)
        {
            if (!Current)
            {
                return;
            }

            var writer = category == "stderr" ? Console.Error : Console.Out;
            writer.Write(category == "stderr" ? Ansi.Red(text) : text);
            writer.Flush();
        }

        public void Continued(int threadId)
        {
        }

        public void ThreadStarted(int threadId)
        {
        }

        public void ThreadExited(int threadId)
        {
        }

        public void ModuleLoaded(ModuleView loadedModule)
        {
        }

        public void ModuleUnloaded(ModuleView unloadedModule)
        {
        }
    }

    private static string? HistoryPath()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                return null;
            }

            config = Path.Combine(home, ".config");
        }

        return Path.Combine(config, "digger", "history");
    }

    public void Dispose()
    {
        _interrupt?.Dispose();
        EndSession(terminate: !IsAttach); // disposes the session

        _program?.Dispose();
        _dispatcher.Dispose();
        _runEvents.Dispose();
    }
}
