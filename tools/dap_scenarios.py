#!/usr/bin/env python3
"""Focused DAP scenarios beyond the main smoke test: stop-at-entry, pause, attach,
function breakpoints, logpoints and unhandled exceptions.

Usage: tools/dap_scenarios.py [path/to/digger] [--verbose]
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dap_smoke as smoke  # noqa: E402

PROGRAM = smoke.PROGRAM
SOURCE = smoke.SOURCE
EXTRAS = os.path.join(os.path.dirname(SOURCE), "Extras.cs")
check = smoke.check
line_of = smoke.line_of


def start(launch_args, breakpoints=None, function_breakpoints=None, exceptions=None, request="launch", source=None, reverse_handler=None):
    client = smoke.Client()
    client.reverse_handler = reverse_handler
    client.request("initialize", {"adapterID": "digger", "supportsRunInTerminalRequest": reverse_handler is not None})
    client.wait_event("initialized")
    response = client.request(request, launch_args)
    check(response["success"], f"{request}: {response.get('message')}")
    if breakpoints is not None:
        client.request("setBreakpoints", {"source": {"path": source or SOURCE}, "breakpoints": breakpoints})
    if function_breakpoints is not None:
        client.request("setFunctionBreakpoints", {"breakpoints": function_breakpoints})
    client.request("setExceptionBreakpoints", {"filters": exceptions if exceptions is not None else ["unhandled"]})
    client.request("configurationDone")
    return client


def finish(client):
    client.request("disconnect", {"terminateDebuggee": True})
    client.proc.wait(timeout=10)


def stop_at_entry():
    print("stop at entry")
    client = start({"program": PROGRAM, "stopAtEntry": True})
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(stopped["body"]["reason"] == "entry", f"reason entry ({stopped['body']['reason']})")
    check(frame["line"] == line_of('Console.WriteLine("HelloDebug starting")'), f"first statement of async Main (line {frame['line']})")
    finish(client)


def function_breakpoint_and_logpoint():
    print("function breakpoint + logpoint")
    client = start(
        {"program": PROGRAM},
        breakpoints=[{"line": line_of("conditional breakpoint"), "logMessage": "i={i} total={total}"}],
        function_breakpoints=[{"name": "Program.Add"}],
    )
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(stopped["body"]["reason"] == "function breakpoint" and "Add" in frame["name"], f"function breakpoint in Add ({frame['name']})")
    client.request("continue", {"threadId": stopped["body"]["threadId"]})
    client.wait_event("terminated")
    output = "".join(client.output)
    check("i=3 total=3" in output, "logpoint interpolates locals")
    check(output.count("i=") == 10, f"logpoint logged 10 times ({output.count('i=')})")
    finish(client)


def step_over_await():
    print("step over await")
    client = start({"program": PROGRAM}, breakpoints=[{"line": line_of("await Task.Delay(10);")}])
    stopped = client.wait_event("stopped")
    thread_id = stopped["body"]["threadId"]
    client.request("next", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(stopped["body"]["reason"] == "step", f"step over await completes as a step ({stopped['body']['reason']})")
    check(frame["line"] == line_of("breakpoint inside async") and "ComputeAsync" in frame["name"],
          f"lands on the line after the await ({frame['name']}:{frame['line']})")
    names = smoke.locals_of(client, frame["id"])
    check(names.get("i", {}).get("value") == "0", "same invocation (i == 0)")
    finish(client)


def extras():
    print("DebuggerDisplay, collections, async step-out")
    client = start({"program": PROGRAM, "args": ["extras"]}, source=EXTRAS,
                   breakpoints=[{"line": line_of("inspect extras here", EXTRAS)}, {"line": line_of("step out of async from here", EXTRAS)}])
    stopped = client.wait_event("stopped")
    thread_id = stopped["body"]["threadId"]
    frame, _ = smoke.top_frame(client, thread_id)
    names = smoke.locals_of(client, frame["id"])
    value = lambda name: names.get(name, {}).get("value")
    children = lambda name: smoke.variables(client, names[name]["variablesReference"])
    if smoke.VERBOSE:
        for v in names.values():
            print(f"     {v['name']} = {v['value']}")
    check(value("order") == "Order #7 for Ada (2 lines)", f"DebuggerDisplay with nq + member chain ({value('order')})")
    check(value("point") == "3, 4", f"DebuggerDisplay on a struct ({value('point')})")
    check(value("pair") == '["answer", 42]', f"KeyValuePair ({value('pair')})")
    check(value("queue") == "Count = 3", f"Queue count ({value('queue')})")
    check([v["value"] for k, v in children("queue").items() if k.startswith("[")] == ["2", "3", "4"], "Queue items in order")
    check([v["value"] for k, v in children("linked").items() if k.startswith("[")] == ['"x"', '"y"', '"z"'], "LinkedList items")
    check(value("immutable") == "Length = 3", f"ImmutableArray ({value('immutable')})")
    check([v["value"] for k, v in children("immutable").items() if k.startswith("[")] == ["5", "6", "7"], "ImmutableArray items")
    check(value("concurrent") == "Count = 2", f"ConcurrentDictionary via its DebuggerDisplay ({value('concurrent')})")
    items = sorted(v["value"] for k, v in children("concurrent").items() if k.startswith("["))
    check(items == ['["k1", 1]', '["k2", 2]'], f"ConcurrentDictionary via Results View ({items})")
    check("Raw View" in children("concurrent"), "Raw View offered")
    check([v["value"] for k, v in children("immutableList").items() if k.startswith("[")] == ['"p"', '"q"'], "ImmutableList via Results View")
    squares = children("squares")
    check("Results View" in squares, f"iterator offers Results View ({list(squares)})")
    if "Results View" in squares:
        results = smoke.variables(client, squares["Results View"]["variablesReference"])
        check([v["value"] for v in results.values()] == ["1", "4", "9", "16"], f"Results View enumerates ({[v['value'] for v in results.values()]})")
    check(smoke.evaluate(client, frame["id"], "order") == "Order #7 for Ada (2 lines)", "DebuggerDisplay in evaluate")

    client.request("continue", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    thread_id = stopped["body"]["threadId"]
    frame, _ = smoke.top_frame(client, thread_id)
    check("InnerAsync" in frame["name"], f"stopped in InnerAsync after its await ({frame['name']})")
    _, frames = smoke.top_frame(client, thread_id)
    names = [f["name"] for f in frames]
    label = next((i for i, f in enumerate(frames) if f.get("presentationHint") == "label"), None)
    check(label is not None and frames[label]["name"] == "[Async Call Stack]", f"async call stack label ({names})")
    logical = frames[label + 1:] if label is not None else []
    check([f["name"].split(".")[-1] for f in logical][:3] == ["OuterAsync", "RunAsync", "Main"], f"logical callers ({[f['name'] for f in logical]})")
    if logical:
        check(logical[0]["line"] == line_of("awaiting caller", EXTRAS), f"caller positioned at its await (line {logical[0]['line']})")
        caller_locals = smoke.locals_of(client, logical[1]["id"])
        check("order" in caller_locals and caller_locals["order"]["value"].startswith("Order #7"), f"locals of a logical async frame ({list(caller_locals)[:6]})")
        check(smoke.evaluate(client, logical[1]["id"], "queue.Count") == "3", "evaluate in a logical async frame")
    client.request("stepOut", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(stopped["body"]["reason"] == "step" and "OuterAsync" in frame["name"], f"step out lands in the awaiting caller ({frame['name']}:{frame['line']})")
    check(frame["line"] in (line_of("awaiting caller", EXTRAS), line_of("awaiting caller", EXTRAS) + 1),
          f"on (or just after) the await (line {frame['line']})")
    finish(client)


def integrated_terminal():
    print("console: integratedTerminal (stdin)")
    terminal = {}

    def run_in_terminal(request):
        arguments = request["arguments"]
        env = dict(os.environ)
        for key, value in (arguments.get("env") or {}).items():
            if value is None:
                env.pop(key, None)
            else:
                env[key] = value
        terminal["kind"] = arguments.get("kind")
        terminal["process"] = subprocess.Popen(arguments["args"], cwd=arguments["cwd"], env=env,
                                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        return {"processId": terminal["process"].pid}

    client = start({"program": PROGRAM, "args": ["read"], "console": "integratedTerminal", "env": {"DIGGER_TERMINAL_TEST": "1"}},
                   breakpoints=[{"line": line_of("after read")}], reverse_handler=run_in_terminal)
    check(terminal.get("kind") == "integrated", f"runInTerminal requested ({terminal.get('kind')})")
    process = terminal["process"]
    process.stdin.write("Bob\n")
    process.stdin.flush()
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    names = smoke.locals_of(client, frame["id"])
    check(names.get("name", {}).get("value") == '"Bob"', f"program read stdin from the terminal ({names.get('name', {}).get('value')})")
    client.request("continue", {"threadId": stopped["body"]["threadId"]})
    exited = client.wait_event("exited")
    check(exited["body"]["exitCode"] == 3, f"exit code relayed by the launch shim ({exited['body']['exitCode']})")
    out = process.stdout.read()
    check("Hi Bob" in out, f"output went to the terminal ({out.strip()!r})")
    check(process.wait(timeout=10) == 3, "shim exits with the program's exit code")
    finish(client)


def set_next_statement_and_completions():
    print("set next statement, completions, single-thread continue")
    client = start({"program": PROGRAM}, breakpoints=[{"line": line_of("var result = a + b;")}, {"line": line_of("conditional breakpoint")}])
    stopped = client.wait_event("stopped")
    thread_id = stopped["body"]["threadId"]
    targets = client.request("gotoTargets", {"source": {"path": SOURCE}, "line": line_of("return result;")})["body"]["targets"]
    check(len(targets) == 1, f"one goto target ({targets})")
    response = client.request("goto", {"threadId": thread_id, "targetId": targets[0]["id"]})
    stopped = client.wait_event("stopped")
    frame, frames = smoke.top_frame(client, thread_id)
    check(response["success"] and stopped["body"]["reason"] == "goto", "goto stops with reason goto")
    check(frame["line"] == line_of("return result;"), f"instruction pointer moved (line {frame['line']})")
    check(smoke.locals_of(client, frame["id"]).get("result", {}).get("value") == "0", "skipped statement did not run")
    targets = client.request("gotoTargets", {"source": {"path": SOURCE}, "line": line_of("var result = a + b;")})["body"]["targets"]
    client.request("goto", {"threadId": thread_id, "targetId": targets[0]["id"]})
    client.wait_event("stopped")
    client.request("next", {"threadId": thread_id})
    client.wait_event("stopped")
    frame, frames = smoke.top_frame(client, thread_id)
    check(smoke.locals_of(client, frame["id"]).get("result", {}).get("value") == "10", "moving back re-runs the statement")
    bad = client.request("gotoTargets", {"source": {"path": SOURCE}, "line": line_of("conditional breakpoint")})
    check(bad["success"] and bad["body"]["targets"] == [], "no targets outside the current method")

    main = next(f for f in frames if "Main" in f["name"])
    labels = lambda text: [t["label"] for t in client.request(
        "completions", {"frameId": main["id"], "text": text, "column": len(text) + 1})["body"]["targets"]]
    check("person" in labels("per"), f"completes locals ({labels('per')})")
    check("Name" in labels("person.Na"), "completes members")
    member_labels = labels("person.")
    check({"Greeting", "Tags", "ToString"} <= set(member_labels), f"completes properties and methods ({member_labels[:8]})")
    check("Count" in labels("person.Tags."), "completes through a property chain")

    response = client.request("continue", {"threadId": thread_id, "singleThread": True})
    check(response["success"] and response["body"]["allThreadsContinued"] is False, "single-thread continue")
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(frame["line"] == line_of("conditional breakpoint"), "single-thread continue reaches the next breakpoint")
    finish(client)


def lazy_properties():
    print("lazy properties")
    client = start({"program": PROGRAM, "lazyProperties": True}, breakpoints=[{"line": line_of("step into here")}])
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    person = smoke.variables(client, smoke.locals_of(client, frame["id"])["person"]["variablesReference"])
    greeting = person.get("Greeting", {})
    check(greeting.get("presentationHint", {}).get("lazy") is True, f"getter offered lazily ({greeting})")
    check(person.get("Name", {}).get("value") == '"Ada"', "fields still shown directly")
    evaluated = smoke.variables(client, greeting["variablesReference"])
    check(evaluated.get("Greeting", {}).get("value") == '"Hello, Ada!"', f"lazy getter evaluates on request ({evaluated})")
    finish(client)


def symbol_server_and_source_link():
    print("symbol server + Source Link (network)")
    client = start({"program": PROGRAM, "args": ["package"], "symbolServer": True, "justMyCode": False},
                   breakpoints=[{"line": line_of("inside package callback")}])
    stopped = client.wait_event("stopped")
    frames = client.request("stackTrace", {"threadId": stopped["body"]["threadId"], "levels": 20}, timeout=120)["body"]["stackFrames"]
    package = next((f for f in frames if f["name"].startswith("Microsoft.Extensions.Primitives.")), None)
    path = (package or {}).get("source", {}).get("path")
    check(path is not None and "/.cache/digger/sources/" in path and os.path.exists(path),
          f"package frame has source via symbol server + Source Link ({(package or {}).get('name')}: {path})")
    if path:
        with open(path) as f:
            check("namespace Microsoft.Extensions.Primitives" in f.read(), "downloaded file is the real source")
    finish(client)


def source_file_map():
    print("sourceFileMap")
    import shutil
    import tempfile
    mapped_root = tempfile.mkdtemp(prefix="digger-map-")
    original_dir = os.path.dirname(SOURCE)
    mapped = os.path.join(mapped_root, "Program.cs")
    shutil.copy(SOURCE, mapped)
    client = smoke.Client()
    client.request("initialize", {"adapterID": "digger"})
    client.wait_event("initialized")
    client.request("launch", {"program": PROGRAM, "sourceFileMap": {original_dir: mapped_root}})
    response = client.request("setBreakpoints", {"source": {"path": mapped}, "breakpoints": [{"line": line_of("step into here")}]})
    client.request("configurationDone")
    stopped = client.wait_event("stopped")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(stopped["body"]["reason"] == "breakpoint", "breakpoint set on the mapped path binds")
    check(frame["source"]["path"] == mapped, f"stack frame reports the mapped path ({frame['source']['path']})")
    check(response["body"]["breakpoints"][0].get("source", {}).get("path") == mapped, "breakpoint reported with the editor's path")
    finish(client)
    shutil.rmtree(mapped_root)


def unhandled_exception():
    print("unhandled exception")
    client = start({"program": PROGRAM, "args": ["crash"]})
    stopped = client.wait_event("stopped")
    check(stopped["body"]["reason"] == "exception" and "unhandled" in (stopped["body"].get("description") or "").lower(),
          f"stopped on unhandled exception ({stopped['body'].get('description')})")
    info = client.request("exceptionInfo", {"threadId": stopped["body"]["threadId"]})["body"]
    check(info["breakMode"] == "unhandled", "breakMode unhandled")
    check("Fail" in (info.get("details", {}).get("stackTrace") or ""), "exception stack trace via func-eval")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    names = smoke.locals_of(client, frame["id"])
    check("$exception" in names, "$exception pseudo-variable")
    finish(client)


def pause_and_resume():
    print("pause")
    client = start({"program": PROGRAM, "args": ["spin"]})
    time.sleep(1.5)
    client.request("pause", {"threadId": 0})
    stopped = client.wait_event("stopped")
    check(stopped["body"]["reason"] == "pause", "reason pause")
    _, frames = smoke.top_frame(client, stopped["body"]["threadId"])
    check(any("Tick" in f["name"] or "Main" in f["name"] for f in frames), f"paused thread is in user code ({[f['name'] for f in frames[:4]]})")
    client.request("continue", {"threadId": stopped["body"]["threadId"]})
    time.sleep(0.3)
    client.request("setBreakpoints", {"source": {"path": SOURCE}, "breakpoints": [{"line": line_of("spin loop body")}]})
    stopped = client.wait_event("stopped")
    check(stopped["body"]["reason"] == "breakpoint", "breakpoint set while running is hit")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    check(smoke.evaluate(client, frame["id"], "tick >= 0") == "true", "evaluate after pause/continue")
    finish(client)


def attach():
    print("attach")
    debuggee = subprocess.Popen(["dotnet", PROGRAM, "spin"], stdout=subprocess.DEVNULL)
    time.sleep(1.5)
    client = start({"processId": debuggee.pid}, breakpoints=[{"line": line_of("spin loop body")}], request="attach")
    stopped = client.wait_event("stopped")
    check(stopped["body"]["reason"] == "breakpoint", "breakpoint hit after attach")
    frame, _ = smoke.top_frame(client, stopped["body"]["threadId"])
    names = smoke.locals_of(client, frame["id"])
    check("tick" in names, f"locals after attach ({list(names)})")
    client.request("setBreakpoints", {"source": {"path": SOURCE}, "breakpoints": []})
    client.request("disconnect", {"terminateDebuggee": False})
    client.proc.wait(timeout=10)
    time.sleep(0.5)
    check(debuggee.poll() is None, "debuggee keeps running after detach")
    debuggee.kill()


if __name__ == "__main__":
    for scenario in (stop_at_entry, function_breakpoint_and_logpoint, step_over_await, extras, integrated_terminal,
                     set_next_statement_and_completions, lazy_properties, source_file_map, symbol_server_and_source_link, unhandled_exception, pause_and_resume, attach):
        try:
            scenario()
        except SystemExit as error:
            smoke.failures.append(f"{scenario.__name__}: {error}")
            print(f"  FAIL {scenario.__name__}: {error}")
    print()
    print("PASS" if not smoke.failures else f"{len(smoke.failures)} FAILURE(S)")
    sys.exit(1 if smoke.failures else 0)
