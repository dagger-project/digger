# Digger

> [!WARNING]
> **Digger is under heavy development.** Commands, options and launch settings may change
> between releases without notice, and you will run into bugs and missing features. Please
> [report problems](https://github.com/dagger-project/digger/issues) with the output of
> `digger dap --log=/tmp/digger.log` (or `digger debug --log=...`) attached, and don't rely on
> it for anything critical yet.

A debugger for .NET (CoreCLR) applications, written in C# 14. Use it straight from the
terminal (`digger debug`, modeled on Go's [Delve](https://github.com/go-delve/delve)) or from
any editor that speaks the [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/):
VS Code and its forks (Cursor, VSCodium, Windsurf), Zed, Neovim (nvim-dap), Helix, Emacs (dape) and others.

Digger talks to the runtime through `ICorDebug` (the same API used by Visual Studio and
netcoredbg), using source-generated COM bindings, and reads portable PDBs with
`System.Reflection.Metadata`. It builds to a single ~9 MB NativeAOT binary.

## Features

| Area | Supported |
| --- | --- |
| Sessions | launch (`.dll` via `dotnet`, or an apphost executable), attach/detach by PID, run without debugging, debug tests (`digger test`) |
| Breakpoints | line, conditional (`i == 5 && name != null`), hit count (`5`, `>= 5`, `% 2`), logpoints (`x = {x}`), function (`Program.Main`), pending breakpoints that bind as modules load; in the terminal also tracepoints (`trace`) and commands run on a hit (`on`) |
| Exceptions | break on unhandled (default) and on thrown (`all`, honours Just My Code), optionally only for some types (`IOException, System.Net.*, !OperationCanceledException`, derived types included); exception info with message, inner exceptions and stack trace |
| Stepping | over / into / out, Just My Code, compiler-hidden code skipped, **step over `await`** (resumes in the same async invocation), **step out of async methods** to the awaiting caller |
| Stack | threads with names, async methods shown by their real name (not `MoveNext`), lambdas and local functions untangled, non-user frames de-emphasized, **async call stacks** (logical awaiting callers, with their locals); in the terminal, every async method in flight (`tasks`) with its logical stack |
| Control | set next statement (`gotoTargets`/`goto`), single-thread continue/step (other threads stay frozen) |
| Sources | `sourceFileMap`, sources embedded in PDBs, Source Link downloads, Microsoft/NuGet symbol servers (opt-in) |
| Variables | locals, arguments, `this`, `$exception`; closures and async state machines flattened back into plain locals; fields, auto-properties, property getters (func-eval, with timeout), static members |
| Formatting | `[DebuggerDisplay]` (with `nq`/`h`/`d` specifiers), primitives (also boxed), strings, enums and `[Flags]`, `Nullable<T>`, `decimal`, `DateTime`, `TimeSpan`, `Guid`, `KeyValuePair`; hex display |
| Collections | arrays (incl. multi-dimensional, paged); field-based views of `List`, `Dictionary`, `HashSet`, `Queue`, `Stack`, `LinkedList`, `ImmutableArray`; **Results View** for any other `IEnumerable` (`ConcurrentDictionary`, `ImmutableList`, LINQ, iterators, ...); Raw View |
| Evaluation | watch / hover / REPL: literals, locals, members, indexers (arrays, lists, dictionaries), method calls (overloads chosen by argument type), static members and type names (`Program.Add(1, 2)`, `s_counter`, `Math.Max(a, b)`, `DateTime.Now`, `string.Concat(x, y)`), arithmetic, comparison, logical, `??`, `?:`; debug console completions |
| Editing | set primitives, strings and `null` |
| Output | debuggee stdout/stderr and `Debugger.Log` forwarded as DAP output, or `"console": "integratedTerminal"` to run in the editor's terminal (stdin works); real exit codes |

Platforms: Linux and macOS, x64 and arm64 (each release is smoke-tested on all four). Windows is not supported yet.

## Quick start

Digger installs as a .NET tool (package `Digger.Debugger`, command `digger`).
Requirements: the .NET 10 SDK.

```sh
dotnet tool install -g Digger.Debugger  # NativeAOT build for linux/osx x64/arm64,
                                        # framework-dependent fallback elsewhere
scripts/install.sh                      # from this checkout: NativeAOT build (needs clang / Xcode tools)
scripts/install.sh --jit                # from this checkout: framework-dependent build, no clang needed
```

Global tools live in `~/.dotnet/tools`; make sure it is on `PATH` (fish:
`fish_add_path ~/.dotnet/tools`). `dotnet tool uninstall -g Digger.Debugger` removes it again.

Editors start the debugger as `digger dap`, which speaks the Debug Adapter Protocol over
stdin/stdout (like Delve's `dlv dap`).

### Terminal

```console
$ cd src/MyApp
$ digger debug                      # dotnet build, then debug (like `dlv debug`)
Type 'help' for list of commands.
(digger) break Program.cs:12
Breakpoint 1 set at MyApp.Program.Main() ./Program.cs:12
(digger) continue
> [Breakpoint 1] MyApp.Program.Main() ./Program.cs:12 (hits: 1)
     7:      ...
=>● 12:          var total = Sum(items);
(digger) print items
(digger) next
```

| Command | |
| --- | --- |
| `digger debug [project] [-- args]` | build the project in the current directory (or the given directory / project file) in Debug and debug it; `--framework`, `--configuration`, `--build-flags="..."`, `--no-build` |
| `digger test [project] [-- args]` | build a test project and debug its tests (like `dlv test`); arguments go to the test application, e.g. `-- --filter-method "*Parses*"` |
| `digger exec <program> [-- args]` | debug a built `.dll` or apphost executable |
| `digger attach <pid>` | attach to a running .NET process (`exit` offers to leave it running) |

All of them take `--wd=dir` (working directory, default: the current one) and `--init=file` (debugger
commands to run at startup). The program shares the terminal for output; its stdin is
`/dev/null`, so Ctrl-C reaches the debugger and pauses the program.

`digger test` supports test projects that run on
[Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
(xunit v3, MSTest, NUnit and TUnit with the testing platform runner): such a project is a program
of its own, so Digger builds and runs it like `digger debug` does. Set breakpoints in your tests,
then `continue`. Projects that only run under VSTest are not supported.

Commands follow Delve's names and aliases; `help` lists them and `help <command>` explains one.
An empty line repeats the last `next`/`step`/`continue`/`list`, and Tab completes commands,
file names and expressions.

| | |
| --- | --- |
| Running | `continue` (`c`) [location], `next` (`n`) [count], `step` (`s`), `stepout` (`so`), `call` expr, `restart` (`r`), `rebuild`, `exit` (`q`), Ctrl-C to pause |
| Breakpoints | `break` (`b`) location [`if` condition], `trace` (`t`) [-stack n] location, `on` id command, `breakpoints` (`bp`), `clear` id, `clearall`, `condition` (`cond`) id expr / `-hitcount` id op n, `toggle` id, `catch` |
| Data | `print` (`p`) [-x] expr, `locals` [-v], `vars` [regex], `whatis` expr, `set` var = value, `display` -a expr |
| Threads and stack | `threads`, `thread` (`tr`) id, `tasks` (`goroutines`) [-a], `task` (`goroutine`) id, `stack` (`bt`) [depth] [-full], `frame` n [command], `up`, `down` |
| Other | `list` (`l`) [location], `sources` / `funcs` / `types` [-a] [regex], `libraries`, `edit` (`ed`), `config`, `source` file, `transcript` file, `help` (`h`) |

Locations are `Program.cs:12` (any unique path suffix), `12` (current file), `+3`/`-3`, or a
method: `Main`, `Program.Main`, `MyApp.Program.Main`. History is kept in
`~/.config/digger/history`.

```console
(digger) catch InvalidOperationException       # stop where it is thrown (catch all, catch off)
(digger) trace -stack 1 Add                       # print each call with its arguments, keep going
> [Tracepoint 1] MyApp.Program.Add(a = 7, b = 3) ./Program.cs:118
      1  MyApp.Program.Main() ./Program.cs:78
(digger) on 2 print total                         # run a command every time breakpoint 2 stops
(digger) vars counter                             # static fields of your code
MyApp.Program.s_counter = 3
(digger) funcs Program\.                          # methods; types lists types (-a: framework too)
(digger) call cache.Clear()                       # run code; other threads run too, Ctrl-C aborts
(digger) tasks                                    # async methods in flight, like Delve's goroutines
  Task 1 - MyApp.Worker.FetchAsync() ./Worker.cs:42 [running on thread 4711] ← MyApp.Program.Main()
  Task 2 - MyApp.Program.Main() ./Program.cs:90 [awaiting]
(digger) task 2                                   # stack, locals and print now work on Main
```

`print` runs method calls with a 5 second limit, with other threads frozen. `call` lets the whole
program run during the call, with no time limit, so the method may wait on other threads; Ctrl-C
aborts it. `tasks` searches the program's memory for unfinished async methods, which can take a
moment in a large process; a task's number is valid until the program runs again.

`config` changes settings: `source-list-line-count`, `max-array-values`, `just-my-code`,
`evaluate-properties`, `symbol-server`, `source-link`, `substitute-path <from> <to>` (like
`sourceFileMap`) and `alias <command> <alias>`. `config -save` writes them to
`~/.config/digger/config`, a list of `config` commands run at startup. `edit` opens the current
line in `$DIGGER_EDITOR`, `$VISUAL` or `$EDITOR`. `transcript` copies the debugger's output to a
file (the program writes to the terminal directly, so its own output is not included).

Your programs must be built with portable PDBs (the SDK default) and, for the best
experience, in `Debug` configuration.

### VS Code, Cursor, VSCodium, Windsurf

VS Code and its forks only talk to debug adapters registered by extensions, so Digger ships a
small one in [`editors/vscode`](editors/vscode) (plain JavaScript, nothing to build). Install it with
`editors/vscode/install.sh`. The script packages a `.vsix` and installs it with every editor command
line it finds (`code`, `cursor`, `codium`, `windsurf`); pass one to choose, e.g.
`editors/vscode/install.sh cursor`. You can also install the `.vsix` from the Extensions view.

The extension finds `digger` via the `digger.path` setting, `$DIGGER_PATH`, `PATH`, or
`~/.dotnet/tools/digger`. It works next to the C# extension: use `"type": "digger"` instead of
`"coreclr"`.

```jsonc
// .vscode/launch.json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Debug MyApp",
      "type": "digger",
      "request": "launch",
      "preLaunchTask": "build",            // a tasks.json task running `dotnet build`
      "program": "${workspaceFolder}/bin/Debug/net10.0/MyApp.dll",
      "args": [],
      "cwd": "${workspaceFolder}",
      "env": { "ASPNETCORE_ENVIRONMENT": "Development" },
      "console": "internalConsole"         // or "integratedTerminal" to read stdin
    },
    {
      "name": "Attach",
      "type": "digger",
      "request": "attach",
      "processId": "${command:pickProcess}"
    },
    {
      "name": "Container",                 // digger dap --server=4711 inside the container
      "type": "digger",
      "request": "launch",
      "server": "localhost:4711",
      "program": "/app/MyApp.dll",
      "sourceFileMap": { "/src": "${workspaceFolder}" }
    }
  ]
}
```

This repository's [`.vscode/launch.json`](.vscode/launch.json) and
[`tasks.json`](.vscode/tasks.json) debug the samples. To debug tests, point `program` at the
test project's `.dll` (a Microsoft.Testing.Platform test project is a program) and pass filter
arguments in `args`.

### Zed

Zed only talks to debug adapters registered by extensions, so Digger ships a small one in
[`editors/zed`](editors/zed). Install it either way:

* `editors/zed/install.sh` (needs Rust with `rustup target add wasm32-wasip2`), then restart Zed, or
* in Zed: command palette → **zed: install dev extension** → choose `editors/zed`.

The extension finds `digger` via the `dap.Digger.binary` setting, `$DIGGER_PATH`, `PATH`,
or `~/.dotnet/tools/digger`. To point it at a specific build:

```jsonc
// ~/.config/zed/settings.json
{ "dap": { "Digger": { "binary": "/path/to/digger" } } }
```

Add a `.zed/debug.json` to your project (see [this repo's](.zed/debug.json)):

```jsonc
[
  {
    "label": "Debug MyApp",
    "adapter": "Digger",
    "request": "launch",
    "program": "$ZED_WORKTREE_ROOT/src/MyApp/bin/Debug/net10.0/MyApp.dll",
    "cwd": "$ZED_WORKTREE_ROOT/src/MyApp",
    "args": [],
    "env": { "ASPNETCORE_ENVIRONMENT": "Development" },
    "build": { "command": "dotnet", "args": ["build", "src/MyApp"], "cwd": "$ZED_WORKTREE_ROOT" }
  }
]
```

To attach, use the **Attach** tab of Zed's new debug session dialog and pick a process.

### Neovim (nvim-dap)

```lua
local dap = require("dap")
dap.adapters.digger = { type = "executable", command = "digger", args = { "dap" } }
dap.configurations.cs = {
  {
    type = "digger",
    name = "Launch",
    request = "launch",
    program = function()
      return vim.fn.input("Path to dll: ", vim.fn.getcwd() .. "/bin/Debug/", "file")
    end,
  },
  { type = "digger", name = "Attach", request = "attach", processId = require("dap.utils").pick_process },
}
```

### Helix

```toml
# languages.toml
[[language]]
name = "c-sharp"
[language.debugger]
name = "digger"
transport = "stdio"
command = "digger"
args = ["dap"]
[[language.debugger.templates]]
name = "launch"
request = "launch"
completion = [{ name = "program", completion = "filename" }]
args = { program = "{0}" }
```

### Emacs (dape)

```elisp
(add-to-list 'dape-configs
  '(digger modes (csharp-mode csharp-ts-mode) command "digger" command-args ("dap")
    :request "launch" :program "bin/Debug/net10.0/MyApp.dll"))
```

### netcoredbg drop-in

`digger dap` accepts and ignores `--interpreter=vscode`, and its launch options use the
same names as vscode-csharp/netcoredbg, so editor setups written for netcoredbg can usually
just swap the command for `digger dap`.

## Configuration

Launch:

| Field | Default | Meaning |
| --- | --- | --- |
| `program` | (required) | The built `.dll` (run with `dotnet`) or apphost executable |
| `args` | `[]` | Command line arguments |
| `cwd` | program's directory | Working directory |
| `env` | `{}` | Extra environment variables (`null` removes one) |
| `stopAtEntry` | `false` | Stop on the first statement of `Main` (async `Main` and top-level statements included) |
| `console` | `internalConsole` | `integratedTerminal` / `externalTerminal`: run in the editor's terminal so the program can read stdin (needs `runInTerminal` support, which Zed and nvim-dap have) |
| `justMyCode` | `true` | Only stop/step in modules with symbols that are not optimized |
| `enableStepFiltering` | `true` | Step through compiler-hidden code |
| `evaluateProperties` | `true` | Run property getters when expanding objects |
| `lazyProperties` | `false` | Show properties as click-to-evaluate nodes (DAP `lazy`) instead of running every getter |
| `symbolServer` | `false` | Download missing portable PDBs from the Microsoft and NuGet symbol servers |
| `sourceLink` | `true` | Download sources via Source Link when they are not on disk |
| `sourceFileMap` | `{}` | Build-time path prefix → local path prefix (containers, CI builds) |
| `dotnetPath` | auto | `dotnet` host for `.dll` programs (else `DOTNET_ROOT`, `PATH`, standard locations) |
| `noDebug` | `false` | Run without the debugger |

Attach: `processId` (number or string), `justMyCode`, `evaluateProperties`, `lazyProperties`,
`symbolServer`, `sourceLink`, `sourceFileMap`.

Downloaded symbols and sources are cached in `~/.cache/digger` (`$XDG_CACHE_HOME/digger`).

### Remote and container debugging

Run `digger dap --server=4711` inside the container (with the port published) and point the
editor at it. In Zed, add `"tcp_connection": { "port": 4711 }` to the debug scenario; the
`program` path is then a path inside the container. Alternatively run the adapter through
`docker exec -i <container> digger dap` (e.g. as nvim-dap's `command`). Use `sourceFileMap` to map
the container's source paths to your checkout.

Exception filters: `all` (thrown) and `unhandled` (on by default). `all` takes an optional
condition (an exception filter option in DAP) that limits it to some exception types:
`InvalidOperationException, System.IO.*, !OperationCanceledException`. Names can be full or
simple, `Namespace.*` matches a namespace, derived types match too and `!` excludes.

Command line (see also [Terminal](#terminal)):

```
digger debug [project] [-- args]    build and debug in the terminal
digger test [project] [-- args]     build and debug a test project in the terminal
digger exec <program> [-- args]     debug a built program in the terminal
digger attach <pid>                 attach to a process in the terminal
digger dap                          DAP over stdin/stdout (what editors use)
digger dap --server[=4711]          DAP over TCP on 127.0.0.1 (debug the adapter itself)
digger dap --log=/tmp/digger.log    diagnostic log; add --trace to log every protocol message
digger dap --dbgshim=PATH           use a specific libdbgshim
digger version                      print the version
digger help                         usage
```

`DIGGER_LOG` and `DIGGER_DBGSHIM` environment variables work like the flags. In Zed you can
also put `"logFile": "/tmp/digger.log"` in a debug scenario.

## Development

```sh
dotnet build                      # whole solution; every analyzer warning is an error
dotnet test --project tests/Digger.Tests
tools/dap_smoke.py                # end-to-end: drives digger over DAP against samples/HelloDebug
tools/dap_scenarios.py            # entry, pause, attach/detach, logpoints, async stepping, DebuggerDisplay,
                                  # collections, integratedTerminal, crashes
tools/dap_smoke.py path/to/digger # same tests against e.g. the NativeAOT build
tools/cli_smoke.py                # end-to-end: scripts the terminal debugger (digger exec / debug / test)
```

The end-to-end scripts need Python 3 and built `samples/HelloDebug` and `samples/HelloTests`
(`dotnet build` builds them).

Code quality settings live in [`Directory.Build.props`](Directory.Build.props) and
[`.editorconfig`](.editorconfig): `AnalysisLevel=latest-all`, Meziantou.Analyzer, nullable
reference types, trimming/AOT analyzers, `TreatWarningsAsErrors`. Rules that are switched off
are listed with the reason next to them.

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing the engine: it explains
the threading model and the rules ICorDebug imposes (values go stale after a func-eval,
every callback must be continued exactly once, and so on).

### Releasing

Pushing a `v*` tag (for example `git tag v0.2.0 && git push origin v0.2.0`) runs
[`.github/workflows/release.yml`](.github/workflows/release.yml): it tests, packs the NativeAOT
packages on Linux and macOS runners plus the framework-dependent fallback, pushes them to
nuget.org (RID packages first, the `Digger.Debugger` pointer package last) and creates a
GitHub release. The tag sets the version; a tag with a suffix (`v0.2.0-preview.1`) makes a
prerelease. Running the workflow by hand builds and tests everything without publishing.

Publishing uses [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so there is no API key secret. It needs a trusted publishing policy on nuget.org for this
repository, workflow `release.yml` and environment `nuget`, and a repository variable
`NUGET_USER` with the nuget.org user name.

### Layout

```
src/Digger.Protocol DAP message types, framed JSON transport (System.Text.Json source generation)
src/Digger.Interop  [GeneratedComInterface] ICorDebug bindings, dbgshim and libc P/Invoke
src/Digger.Engine   the debugger: session, callbacks, breakpoints, stepping, symbols, inspection, evaluator
src/Digger          the digger executable and .NET tool package: DAP request handlers, stdio redirection,
                    terminal debugger (Cli/)
tests/Digger.Tests  unit tests (xunit v3, Microsoft.Testing.Platform)
samples/HelloDebug  debuggee used by the tests
samples/HelloTests  test project for `digger test`
tools/              end-to-end DAP test clients
editors/vscode      VS Code / Cursor / VSCodium / Windsurf extension (plain JavaScript)
editors/zed         Zed extension (Rust → wasm)
scripts/install.sh  pack + install as a global tool from this checkout
scripts/pack.sh     build the tool packages locally (CI publishes them)
.github/workflows   release.yml: test, pack and publish to nuget.org on a v* tag
```

## Limitations and roadmap

Good next steps, roughly in order of value:

* **Windows**: dbgshim works there, but stdio redirection and exit-code handling are Unix-only.
* **`DebuggerTypeProxy`** attributes (`DebuggerDisplay` is supported).
* **Debugging tests from editors** (a Zed debug locator for test projects; `digger test` covers the terminal).
* **Evaluator**: casts, `typeof`, generic methods, lambdas, argument conversions (an `int` passed to a `long` parameter).
* Decompilation for frames with no source at all.
* Data breakpoints (CoreCLR implements them on Windows only) and Hot Reload (needs a Roslyn workspace).
* Edit and Continue / hot reload.

## License

[Apache License 2.0](LICENSE).
