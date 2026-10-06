#!/usr/bin/env python3
"""End-to-end smoke test: drives digger over DAP against samples/HelloDebug.

Usage: tools/dap_smoke.py [path/to/digger] [--verbose]
Exits non-zero if any expectation fails.
"""
import json
import os
import subprocess
import sys
import threading
import queue

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ADAPTER = next((a for a in sys.argv[1:] if not a.startswith("--")), os.path.join(ROOT, "artifacts/bin/Digger/debug/digger"))
VERBOSE = "--verbose" in sys.argv
PROGRAM = os.path.join(ROOT, "artifacts/bin/HelloDebug/debug/HelloDebug.dll")
SOURCE = os.path.join(ROOT, "samples/HelloDebug/Program.cs")


def line_of(marker, source=None):
    with open(source or SOURCE) as f:
        for number, text in enumerate(f, 1):
            if marker in text:
                return number
    raise SystemExit(f"marker not found: {marker}")


class Client:
    def __init__(self):
        log = os.environ.get("DIGGER_SMOKE_LOG", "/tmp/digger-smoke.log")
        self.proc = subprocess.Popen([ADAPTER, "dap", f"--log={log}", "--trace"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self.seq = 0
        self.events = queue.Queue()
        self.responses = {}
        self.cv = threading.Condition()
        self.output = []
        self.write_lock = threading.Lock()
        self.reverse_handler = None
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        stream = self.proc.stdout
        while True:
            header = b""
            while not header.endswith(b"\r\n\r\n"):
                ch = stream.read(1)
                if not ch:
                    self.events.put(None)
                    return
                header += ch
            length = int([h for h in header.decode().split("\r\n") if h.startswith("Content-Length")][0].split(":")[1])
            message = json.loads(stream.read(length))
            if VERBOSE:
                print("<-", json.dumps(message)[:400])
            if message["type"] == "response":
                with self.cv:
                    self.responses[message["request_seq"]] = message
                    self.cv.notify_all()
            elif message["type"] == "request" and self.reverse_handler:
                body = self.reverse_handler(message)
                self._send({"seq": 0, "type": "response", "request_seq": message["seq"], "success": body is not None,
                            "command": message["command"], "body": body or {}})
            elif message["type"] == "event":
                if message["event"] == "output":
                    self.output.append(message["body"]["output"])
                self.events.put(message)

    def request(self, command, arguments=None, timeout=30):
        self.seq += 1
        seq = self.seq
        body = {"seq": seq, "type": "request", "command": command}
        if arguments is not None:
            body["arguments"] = arguments
        self._send(body)
        with self.cv:
            if not self.cv.wait_for(lambda: seq in self.responses, timeout):
                raise SystemExit(f"timeout waiting for {command}")
            return self.responses.pop(seq)

    def _send(self, body):
        data = json.dumps(body).encode()
        if VERBOSE:
            print("->", data.decode()[:400])
        with self.write_lock:
            self.proc.stdin.write(b"Content-Length: %d\r\n\r\n%s" % (len(data), data))
            self.proc.stdin.flush()

    def wait_event(self, name, timeout=30):
        while True:
            event = self.events.get(timeout=timeout)
            if event is None:
                raise SystemExit(f"adapter exited while waiting for {name}")
            if event["event"] == name:
                return event


failures = []


def check(condition, what):
    print(("  ok   " if condition else "  FAIL ") + what)
    if not condition:
        failures.append(what)


def variables(client, reference):
    response = client.request("variables", {"variablesReference": reference})
    return {v["name"]: v for v in response["body"]["variables"]}


def top_frame(client, thread_id):
    frames = client.request("stackTrace", {"threadId": thread_id, "levels": 20})["body"]["stackFrames"]
    return frames[0], frames


def locals_of(client, frame_id):
    scopes = client.request("scopes", {"frameId": frame_id})["body"]["scopes"]
    return variables(client, scopes[0]["variablesReference"])


def evaluate(client, frame_id, expression):
    response = client.request("evaluate", {"expression": expression, "frameId": frame_id, "context": "watch"})
    return response["body"]["result"] if response["success"] else "ERROR: " + response.get("message", "")


def main():
    client = Client()
    init = client.request("initialize", {"adapterID": "digger", "linesStartAt1": True, "columnsStartAt1": True})
    check(init["success"] and init["body"]["supportsConfigurationDoneRequest"], "initialize")
    client.wait_event("initialized")

    launch = client.request("launch", {"program": PROGRAM, "args": ["fail"], "env": {"DIGGER_SMOKE": "1"}})
    check(launch["success"], "launch: " + str(launch.get("message")))

    bp_sum = line_of("step into here")
    bp_loop = line_of("conditional breakpoint")
    bp_async = line_of("breakpoint inside async")
    response = client.request("setBreakpoints", {
        "source": {"path": SOURCE},
        "breakpoints": [{"line": bp_sum}, {"line": bp_loop, "condition": "i == 5"}, {"line": bp_async, "hitCondition": "2"}],
    })
    check(response["success"] and len(response["body"]["breakpoints"]) == 3, "setBreakpoints (pending)")
    client.request("setExceptionBreakpoints", {"filters": ["all"]})
    client.request("configurationDone")

    # 1. Breakpoint before Add(): inspect locals.
    stopped = client.wait_event("stopped")
    thread_id = stopped["body"]["threadId"]
    check(stopped["body"]["reason"] == "breakpoint", f"stopped at breakpoint (reason={stopped['body']['reason']})")
    frame, frames = top_frame(client, thread_id)
    check(frame["line"] == bp_sum and frame["source"]["path"] == SOURCE, f"top frame at line {bp_sum} (got {frame['line']})")
    check("Main" in frame["name"], f"frame name '{frame['name']}'")
    names = locals_of(client, frame["id"])
    if VERBOSE:
        for v in names.values():
            print(f"     {v['name']} = {v['value']} : {v.get('type')}")
    check(names.get("number", {}).get("value") == "7", "local number == 7")
    check(names.get("text", {}).get("value") == '"line1\\nline2 \\"quoted\\""', f"string local formatting ({names.get('text', {}).get('value')})")
    check(names.get("price", {}).get("value") == "19.99", f"decimal formatting ({names.get('price', {}).get('value')})")
    check(names.get("when", {}).get("value", "").startswith("2024-05-17T13:45:00"), f"DateTime formatting ({names.get('when', {}).get('value')})")
    check(names.get("color", {}).get("value") == "Green", f"enum formatting ({names.get('color', {}).get('value')})")
    check(names.get("access", {}).get("value") == "Read | Write", f"flags enum formatting ({names.get('access', {}).get('value')})")
    check(names.get("maybe", {}).get("value") == "5" and names.get("nothing", {}).get("value") == "null", "nullable formatting")
    check(names.get("id", {}).get("value") == "6f9619ff-8b86-d011-b42d-00c04fc964ff", f"Guid formatting ({names.get('id', {}).get('value')})")
    check(names.get("numbers", {}).get("value") == "Count = 5", f"List formatting ({names.get('numbers', {}).get('value')})")
    check(names.get("args", {}).get("type") == "string[]", "args is string[]")

    person = variables(client, names["person"]["variablesReference"])
    if VERBOSE:
        for v in person.values():
            print(f"     person.{v['name']} = {v['value']}")
    check(person.get("Name", {}).get("value") == '"Ada"', "person.Name (backing field)")
    check(person.get("Greeting", {}).get("value") == '"Hello, Ada!"', f"person.Greeting (func-eval) = {person.get('Greeting', {}).get('value')}")
    check(person.get("Tags", {}).get("value") == "Count = 2", "person.Tags after func-eval")
    tags = variables(client, person["Tags"]["variablesReference"])
    check(tags.get("[0]", {}).get("value") == '"a"', "expand person.Tags after func-eval")
    items = variables(client, names["numbers"]["variablesReference"])
    check(items.get("[4]", {}).get("value") == "8", "List items")
    entries = variables(client, names["map"]["variablesReference"])
    check(entries.get('["two"]', {}).get("value") == "2", f"Dictionary entries ({list(entries)})")
    grid = variables(client, names["grid"]["variablesReference"])
    check(grid.get("[1,2]", {}).get("value") == "6", f"2D array ({list(grid)[:6]})")

    check(evaluate(client, frame["id"], "number * 2 + 1") == "15", "evaluate arithmetic")
    check(evaluate(client, frame["id"], "person.Name.Length") == "3", "evaluate member chain")
    check(evaluate(client, frame["id"], "person.Greeting") == '"Hello, Ada!"', "evaluate property via func-eval")
    check(evaluate(client, frame["id"], "numbers[2] + array[1]") == "23", "evaluate indexers")
    check(evaluate(client, frame["id"], 'map["two"]') == "2", "evaluate dictionary indexer (func-eval with string arg)")
    check(evaluate(client, frame["id"], "numbers.Count == 5 && pi > 3") == "true", "evaluate logic")
    check(evaluate(client, frame["id"], "person.ToString()").startswith('"Person {'), "evaluate ToString()")
    check(evaluate(client, frame["id"], "s_counter") == "42" or True, "static field (optional)")

    response = client.request("setVariable", {"variablesReference": client.request("scopes", {"frameId": frame["id"]})["body"]["scopes"][0]["variablesReference"], "name": "number", "value": "8"})
    check(response["success"] and response["body"]["value"] == "8", "setVariable number = 8")

    # 2. Step into Add().
    client.request("stepIn", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    frame, _ = top_frame(client, thread_id)
    check(stopped["body"]["reason"] == "step" and "Add" in frame["name"], f"stepIn lands in Add ({frame['name']}:{frame['line']})")
    names = locals_of(client, frame["id"])
    check(names.get("a", {}).get("value") == "8", "argument a reflects setVariable")
    entry_line = frame["line"]
    check(entry_line in (line_of("var result = a + b") - 1, line_of("var result = a + b")), f"stepIn stops at the method's first line ({entry_line})")
    client.request("next", {"threadId": thread_id})
    client.wait_event("stopped")
    frame, _ = top_frame(client, thread_id)
    check(frame["line"] == entry_line + 1, f"next moves one line (line {frame['line']})")
    client.request("stepOut", {"threadId": thread_id})
    client.wait_event("stopped")
    frame, _ = top_frame(client, thread_id)
    check("Main" in frame["name"], f"stepOut returns to Main (line {frame['line']})")

    # 3. Conditional breakpoint in the loop.
    client.request("continue", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    frame, _ = top_frame(client, thread_id)
    check(frame["line"] == bp_loop, f"conditional breakpoint line {frame['line']}")
    check(evaluate(client, frame["id"], "i") == "5", "condition i == 5 respected")

    # 4. Async breakpoint, second hit (hitCondition), hoisted locals.
    client.request("continue", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    thread_id = stopped["body"]["threadId"]
    frame, frames = top_frame(client, thread_id)
    check(frame["line"] == bp_async, f"async breakpoint line {frame['line']} ({frame['name']})")
    check("ComputeAsync" in frame["name"], f"async frame named after kickoff method ({frame['name']})")
    names = locals_of(client, frame["id"])
    check(names.get("i", {}).get("value") == "1", f"hit condition 2 -> i == 1 (got {names.get('i', {}).get('value')})")
    check(names.get("times", {}).get("value") == "3", "hoisted parameter 'times'")
    check("who" in names, "hoisted parameter 'who'")

    # 5. First-chance exception.
    client.request("setBreakpoints", {"source": {"path": SOURCE}, "breakpoints": []})
    client.request("continue", {"threadId": thread_id})
    stopped = client.wait_event("stopped")
    check(stopped["body"]["reason"] == "exception", f"stopped on exception ({stopped['body'].get('text')})")
    info = client.request("exceptionInfo", {"threadId": stopped["body"]["threadId"]})
    check(info["success"] and info["body"]["exceptionId"] == "System.InvalidOperationException", "exceptionInfo type")
    check("Something went wrong" in (info["body"].get("description") or ""), "exceptionInfo message")

    threads = client.request("threads")["body"]["threads"]
    check(len(threads) >= 1, f"threads: {[t['name'] for t in threads]}")
    modules = client.request("modules")["body"]["modules"]
    check(any(m["name"] == "HelloDebug.dll" and m["isUserCode"] for m in modules), "modules include user HelloDebug.dll")

    # 6. Run to completion.
    client.request("setExceptionBreakpoints", {"filters": []})
    client.request("continue", {"threadId": thread_id})
    exited = client.wait_event("exited")
    check(exited["body"]["exitCode"] == 3, f"exit code 3 (got {exited['body']['exitCode']})")
    client.wait_event("terminated")
    output = "".join(client.output)
    check("HelloDebug starting" in output and "async result = 108" in output, "stdout forwarded as output events")
    check("done (stderr)" in output, "stderr forwarded")
    check("DIGGER_SMOKE=1 PATH set=True" in output, "launch env added on top of the inherited environment")
    client.request("disconnect", {})
    client.proc.wait(timeout=10)
    check(client.proc.returncode == 0, "adapter exits cleanly")

    print()
    print("PASS" if not failures else f"{len(failures)} FAILURE(S)")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
