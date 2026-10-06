# Digger architecture

```
 editor ──DAP/JSON──▶ digger ──ICorDebug (COM)──▶ mscordbi ──IPC──▶ CoreCLR in the debuggee
                        │                              ▲
                        └── dbgshim: launch / attach ──┘
```

## Projects

* **Digger.Protocol**: wire types (`Messages.cs`) and `DapConnection` (Content-Length
  framing over a `PipeReader`, serialized writes via a reusable `Utf8JsonWriter`). Request
  arguments are kept as raw UTF-8 and deserialized lazily with source-generated
  `JsonTypeInfo`, so there is no reflection anywhere.
* **Digger.Interop**: `ICorDebug*` interfaces as `[GeneratedComInterface]` (cross-platform,
  AOT-safe, no built-in COM needed). Every method is `[PreserveSig]` and returns the raw
  HRESULT: S_FALSE and "expected" failures (variable not available, end of stack) are
  common and must not cost an exception. When adding methods, **declare every vtable slot
  in IDL order** (see the header of `Interfaces.cs`); unused parameter types become `nint`.
* **Digger.Engine**: everything else, DAP-agnostic. `DebugSession` is the facade.
* **Digger** (exe): `DapServer` maps requests to `DebugSession` calls and engine events to
  DAP events; `StdioRedirector` sets up file descriptors (below).

## Threading model

There is one **engine thread** (`EngineDispatcher`). All session state is owned by it and
needs no locks:

* The protocol reader (a background task) posts each request to the engine thread.
* ICorDebug delivers callbacks on its own thread (the RCET). `ManagedCallback` flattens each
  callback into a `DebugEvent` and `DebugSession.OnCallback` re-posts it to the engine thread,
  then returns **without** calling `Continue`. The process stays stopped until the engine
  either calls `Continue` (uninteresting event) or reports a stop to the client.

Why not handle callbacks inline? Because breakpoint conditions and property display need
**func-eval**, which resumes the process and waits for `EvalComplete`, a callback that
cannot arrive while the RCET is still inside the `Breakpoint` callback.

The one exception: while a func-eval runs, the engine thread is blocked waiting for it. Any
other callback that arrives then (a module load triggered by the evaluated code, a thread
start, ...) is handled **inline on the RCET** (`HandleDuringEvaluation`): the minimum
bookkeeping is done (modules must be configured before their code runs) and the process is
continued. `ModuleRegistry` is the only engine structure touched from both threads; it locks.

## ICorDebug rules the engine relies on

1. **Every callback is continued exactly once.** `Dispatch` continues unless the handler
   returned "stay stopped". If an event wants to stop while the session is already stopped
   (e.g. a queued breakpoint after a pause), it is continued instead (`StopFor`).
2. **Values and frames go stale when the process runs**, and a func-eval runs the process.
   Therefore:
   * `FuncEvaluator.Generation` increments per evaluation; `FrameEntry` re-walks the stack
     by depth when its generation is old; `FrameScope` rebuilds its name table.
   * Variable containers never hold raw values. They hold an `IValueSource`: heap objects
     are pinned with a strong GC handle (`ICorDebugHeapValue2.CreateHandle`) at the moment
     the value is displayed; value types are re-evaluated from their expression. Handles
     are released on resume (`VariableStore.Clear`).
   * Children are produced lazily (`IEnumerable`) and formatted immediately, so a property
     getter cannot invalidate a sibling read before it.
   * The evaluator reads each operand into a local constant before evaluating the next,
     and evaluates call arguments and indexes before the target.
3. **SetJITCompilerFlags only works inside the real LoadModule callback**: done before
   continuing it (`ConfigureModule`), so user modules run unoptimized.
4. **Func-eval only at GC-safe points** (breakpoint, step, exception stops). Other threads
   are suspended for the duration; a 5 s timeout aborts (then rude-aborts) the evaluation.

## Launch and attach

Launch uses dbgshim: `CreateProcessForLaunch` (suspended) → `RegisterForRuntimeStartup` →
`ResumeProcess` at `configurationDone`, so breakpoints are in place first. When CoreCLR starts
in the debuggee, dbgshim calls back with an `ICorDebug`; the debuggee runtime is blocked
until that callback returns, so the session attaches synchronously inside it
(`EngineDispatcher.Invoke`). Attach uses `EnumerateCLRs` → `CreateVersionStringFromModule` →
`CreateDebuggingInterfaceFromVersionEx`; the runtime then replays "fake" load events.

**Stdio**: the debuggee inherits digger's fds 0/1/2, which carry the protocol. At startup the
protocol is moved to private close-on-exec descriptors, fd 0 becomes `/dev/null`, and fds
1/2 become pipes that are pumped into DAP `output` events.

**Terminal launch** (`"console": "integratedTerminal"`, `Runtime/TerminalLaunch.cs`): the
adapter asks the editor (DAP `runInTerminal`) to run `digger --launch-shim=<socket> -- <program>`.
The shim creates the program suspended with dbgshim, so it inherits the terminal, and sends
its pid over a Unix socket. The adapter registers for runtime startup on that pid (dbgshim
does not require the debuggee to be its child), tells the shim to resume at
`configurationDone`, and the shim relays the exit code when the program ends.

**Exit codes**: the PAL inside mscordbi reaps the child to notice its exit, so `waitpid`
after `ExitProcess` finds nothing. A watcher thread blocks in `waitid(WEXITED | WNOWAIT)`,
which sees the status without consuming it.

## Symbols

`ModuleMetadata` opens each module from disk with `PEReader` (no `IMetaDataImport`) and its
portable PDB (`SymbolReader`, embedded or side-by-side):

* **Line → IL** (`ResolveBreakpoint`): a multi-line statement containing the line, else the
  next line with code; the left-most statement on that line wins over lambdas on it.
* **Path matching** (`SourcePathMatcher`): exact first; suffix matching only for PDB paths
  that do not exist locally (`/_/` deterministic builds), never between two real files.
* **Stepping** uses the IL range of the current statement up to the next *visible* sequence
  point; landing on hidden code or code without symbols re-steps (bounded).
* **Async**: kickoff ↔ MoveNext mapping names frames and places breakpoints; the
  `AsyncMethodSteppingInformation` blob provides yield/resume offsets for step over `await`
  (a breakpoint at the resume offset, matched to the same state machine object).
  Step out of an async method that already yielded uses the runtime's debugger hook:
  `Task.SetNotificationForWaitCompletion(true)` on the method's task (read from the
  builder's `m_task`), then a breakpoint in `Task.NotifyDebuggerOfWaitCompletion` (called
  from the awaiter's `GetResult` in the caller) followed by a Just My Code step out.

## Stack and sources

* **Async call stacks** (`GetAsyncCallers`): from the outermost user frame, if it is an async
  MoveNext resumed by the thread pool, follow `builder.m_task.m_continuationObject` to the
  awaiting method's `AsyncStateMachineBox` (directly, via a delegate's `_target`, an
  `AwaitTaskContinuation.m_action`, or the first entry of a continuation list). Its
  `<>1__state` indexes the PDB await list to position the frame. Logical frames carry the
  pinned state machine; their "locals" are its hoisted fields.
* **Source resolution** (`SourceResolver`): `sourceFileMap` → file on disk → embedded source
  → Source Link download, cached under `~/.cache/digger/sources`. The reverse map lets
  breakpoints in mapped or downloaded files bind to the PDB's document path.
* **Symbol servers** (`SymbolServer`, opt-in): the PE CodeView record gives the SSQP key
  (`{guid}FFFFFFFF` for portable PDBs). Lookups happen lazily, the first time a frame of a
  module without symbols is described, and attach the PDB to the module after load (it stays
  non-user code for Just My Code).
* **Set next statement**: `gotoTargets` offers the first sequence point of a line within the
  top frame's method; `goto` checks `CanSetIP` then calls `SetIP`.

## Variables view

* `[DebuggerDisplay]` runs code, so it is evaluated after all children of a container have
  been read and formatted (`PendingDisplay`), from each child's pinned source. Built-in
  formats (collections, enums, `DateTime`, ...) take precedence and cost no func-eval.
* Results View enumerates through `IEnumerable.GetEnumerator`, `IEnumerator.MoveNext` and
  `Current` (methods resolved in System.Private.CoreLib; the eval does virtual/interface
  dispatch), pinning the enumerator with a GC handle between calls.

## Extending

* **A new DAP request**: add argument/body records to `Messages.cs` *and* to `DapJsonContext`,
  a case in `DapServer.Handle`, and an engine method on `DebugSession` (engine thread).
* **A new collection view**: `CollectionView.cs` (read private fields, no func-eval).
* **New value formatting**: `ValueInspector.TryFormatWellKnown`.
* **Evaluator syntax**: `ExpressionParser` (AST) + `ExpressionEvaluator`.
* **More ICorDebug**: add the interface to `Interfaces.cs` following the slot rules.
