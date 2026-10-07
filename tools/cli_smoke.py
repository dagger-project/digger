#!/usr/bin/env python3
"""End-to-end test of the terminal debugger: scripts `digger exec` / `digger debug`
against samples/HelloDebug (and `digger test` against samples/HelloTests) through stdin and checks the transcript.

Usage: tools/cli_smoke.py [path/to/digger] [--verbose]
Exits non-zero if any expectation fails.
"""
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIGGER = next((a for a in sys.argv[1:] if not a.startswith("--")), os.path.join(ROOT, "artifacts/bin/Digger/debug/digger"))
VERBOSE = "--verbose" in sys.argv
PROGRAM = "artifacts/bin/HelloDebug/debug/HelloDebug.dll"
SOURCE = os.path.join(ROOT, "samples/HelloDebug/Program.cs")
failures = []
# A private config directory: the user's saved config and history must not change the results.
CONFIG_HOME = tempfile.mkdtemp(prefix="digger-cli-smoke-")


def line_of(marker):
    with open(SOURCE) as f:
        for number, text in enumerate(f, 1):
            if marker in text:
                return number
    raise SystemExit(f"marker not found: {marker}")


def run(args, commands):
    env = dict(os.environ, NO_COLOR="1", XDG_CONFIG_HOME=CONFIG_HOME)
    result = subprocess.run([DIGGER, *args], input="\n".join(commands) + "\n", capture_output=True,
                            text=True, cwd=ROOT, env=env, timeout=120)
    if VERBOSE:
        print(result.stdout, result.stderr)
    return result.stdout + result.stderr


def expect(name, transcript, *needles):
    missing = [n for n in needles if n not in transcript]
    if missing:
        failures.append(name)
        print(f"  FAIL {name}: missing {missing!r}")
        if not VERBOSE:
            print("\n".join("       | " + line for line in transcript.splitlines()[-40:]))
    else:
        print(f"  ok   {name}")


step_into = line_of("// step into here")
conditional = line_of("// conditional breakpoint")
in_async = line_of("// breakpoint inside async")

out = run(["exec", PROGRAM], [
    f"break Program.cs:{step_into}",
    f"b {conditional} if i == 5",
    "b Add",
    "breakpoints",
    "continue",
    "print person",
    "locals",
    "whatis map",
    "continue",
    "stepout",
    "continue",
    "print i",
    "print total",
    "set total = 100",
    "print total",
    "stack",
    "clearall",
    f"continue Program.cs:{in_async}",
    "print accumulator",
    "next",
    "continue",
    "print 1",
    "exit",
])
expect("breakpoint set before start", out, f"Breakpoint 1 set at HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:{step_into}")
expect("function breakpoint is qualified", out, "Breakpoint 3 set at HelloDebug.Program.Add()")
expect("stop at line breakpoint", out, f"> [Breakpoint 1] HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:{step_into} (hits: 1)")
expect("source listing", out, f"=>● {step_into}:")
expect("print expands objects", out, '  Name: "Ada"', "  Age: 36")
expect("locals", out, "number = 7")
expect("whatis", out, "System.Collections.Generic.Dictionary<string, int>")
expect("function breakpoint hit", out, "> [Breakpoint 3] HelloDebug.Program.Add()")
expect("conditional breakpoint", out, f"> [Breakpoint 2] HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:{conditional}", "(digger) print i\n5")
expect("set variable", out, "total = 100")
expect("stack", out, "HelloDebug.Program.Main()\n       at ./samples/HelloDebug/Program.cs")
expect("continue to location", out, f"HelloDebug.Program.ComputeAsync() ./samples/HelloDebug/Program.cs:{in_async}")
expect("program exit", out, "has exited with status 0")
expect("commands after exit", out, "use 'restart' to run it again")

out = run(["exec", PROGRAM, "--", "crash"], ["continue", "print $exception.Message", "exit"])
expect("unhandled exception stop", out, "[exception]", "System.InvalidOperationException: Something went wrong for crash")

out = run(["exec", PROGRAM], ["trace -stack 1 Add", f"b {conditional} if i == 2", "on 2 print total", "breakpoints", "c",
                               "on 2 -clear", "on 2 trace", "cond 2 i >= 8", "c", "exit"])
expect("tracepoint prints arguments and continues", out, "Tracepoint 1 set at HelloDebug.Program.Add()",
       "> [Tracepoint 1] HelloDebug.Program.Add(a = 7, b = 3) ./samples/HelloDebug/Program.cs:",
       "     1  HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:", "sum = 10")
expect("on runs commands at a stop", out, "\tprint total", "[on 2] print total\n1\n")
expect("on trace turns a breakpoint into a tracepoint", out,
       f"> [Tracepoint 2] HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:{conditional}\n" * 2, "has exited with status 0")

out = run(["exec", PROGRAM, "--", "spin"], ["funcs Program\\.", "types Point", "b Tick", "c", "c", "c", "vars counter",
                                           "funcs -a ^System.Console.WriteLine$", "exit"])
expect("funcs before start", out, "HelloDebug.Program.Add\nHelloDebug.Program.ComputeAsync\n")
expect("types", out, "(digger) types Point\nHelloDebug.Point\n")
expect("vars reads statics", out, "HelloDebug.Program.s_counter = 1")
expect("funcs -a searches the framework", out, "(digger) funcs -a ^System.Console.WriteLine$\nSystem.Console.WriteLine\n")

script = os.path.join(CONFIG_HOME, "commands.txt")
transcript = os.path.join(CONFIG_HOME, "transcript.txt")
with open(script, "w") as f:
    f.write("# a comment\nb Add\n")
out = run(["exec", PROGRAM], ["config source-list-line-count 1", "config alias print pp", "config -save", f"source {script}",
                              f"transcript -t {transcript}", "c", "pp a + b", "transcript -off", "exit"])
expect("config and alias", out, "Configuration saved to", "(digger) pp a + b\n10")
expect("source runs a command file", out, "(digger) b Add\nBreakpoint 1 set at")
expect("source-list-line-count", out, "    117:      private static int Add(int a, int b)\n=>  118:")
with open(transcript) as f:
    written = f.read()
expect("transcript", written, "(digger) c\n> [Breakpoint 1] HelloDebug.Program.Add()", "(digger) pp a + b\n10\n")
out = run(["exec", PROGRAM], ["config -list", "exit"])
expect("config is loaded at startup", out, "source-list-line-count   1", "alias                    pp → print")
os.remove(os.path.join(CONFIG_HOME, "digger", "config"))

out = run(["exec", PROGRAM], [f"b {conditional}", "c", "call numbers.Add(13)", "p numbers.Count", "call Add(2, 3)", "p Program.Add(4, 5)",
                              "p s_counter", "p Math.Max(2.5, 1.0)", "p text.Contains(\"line\")", "p numbers.Clear()", "p Program", "exit"])
expect("call runs methods", out, "(digger) call numbers.Add(13)\n(digger) p numbers.Count\n6", "(digger) call Add(2, 3)\n5")
expect("statics and type names", out, "(digger) p Program.Add(4, 5)\n9", "(digger) p s_counter\n42", "(digger) p Math.Max(2.5, 1.0)\n2.5",
       "(digger) p text.Contains(\"line\")\ntrue", "(digger) p numbers.Clear()\nvoid", "is a type, not a value")

out = run(["exec", PROGRAM], [f"b {in_async}", "c", "tasks", "task 2", "stack", "p number + 1", "next", "exit"])
expect("tasks lists async methods", out,
       f"Task 1 - HelloDebug.Program.ComputeAsync() ./samples/HelloDebug/Program.cs:{in_async} [running on thread",
       f"Task 2 - HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:{line_of('await ComputeAsync(person, 3)')} [awaiting]")
expect("task switches to a logical stack", out, "Switched to task 2", "(digger) p number + 1\n8", "cannot be stepped")

out = run(["exec", PROGRAM], ["catch InvalidOperationException", "continue", "print $exception.Message", "exit"])
expect("catch thrown exception by type", out, "Stop on thrown exceptions: InvalidOperationException", "[exception]",
       "HelloDebug.Program.Fail(", "Something went wrong for")

out = run(["exec", PROGRAM], ["catch all !System.InvalidOperationException", "catch ArgumentException", "continue", "exit"])
expect("catch filters skip other types", out, "Stop on thrown exceptions: all types except System.InvalidOperationException",
       "caught: Something went wrong", "has exited with status 0")

out = run(["exec", PROGRAM, "--", "fail"], ["continue", "restart", "continue", "exit"])
expect("exit status and restart", out, "has exited with status 3", "Process restarted", "HelloDebug starting\n")

out = run(["exec", PROGRAM], ["next", "next", "step", "exit"])
expect("next before start stops in Main", out, "> HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:")

out = run(["exec", PROGRAM], ["b Nope.cs:3", "b Program.cs: 9", "frobnicate", "print x", "exit"])
expect("errors are reported", out, "No source file matches 'Nope.cs'", "is not a location", "command not available: frobnicate",
       "The program is not running yet")

out = run(["debug", "samples/HelloDebug"], [f"b Program.cs:{step_into}", "c", "p number", "exit"])
expect("debug builds and runs", out, "Build succeeded", f"[Breakpoint 1] HelloDebug.Program.Main()", "(digger) p number\n7")

tests_source = os.path.join(ROOT, "samples/HelloTests/CalculatorTests.cs")
with open(tests_source) as f:
    in_divides = next(n for n, text in enumerate(f, 1) if "breakpoint in Divides" in text)
out = run(["test", "samples/HelloTests", "--no-build", "--", "--filter-method", "*Divides"],
          ["next", f"b CalculatorTests.cs:{in_divides}", "c", "p dividend", "c", "p dividend", "clearall", "c", "exit"])
expect("test stops in a test", out, "set a breakpoint in a test and use 'continue'",
       f"[Breakpoint 1] HelloTests.CalculatorTests.Divides() ./samples/HelloTests/CalculatorTests.cs:{in_divides} (hits: 1)",
       "(digger) p dividend\n4", "(digger) p dividend\n9", "succeeded: 2", "has exited with status 0")

out = run(["test", "samples/HelloDebug", "--no-build"], [])
expect("test rejects a non-test project", out, "HelloDebug.dll is not a test project")

print("FAIL" if failures else "PASS")
sys.exit(1 if failures else 0)
