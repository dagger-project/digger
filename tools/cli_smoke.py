#!/usr/bin/env python3
"""End-to-end test of the terminal debugger: scripts `digger exec` / `digger debug`
against samples/HelloDebug through stdin and checks the transcript.

Usage: tools/cli_smoke.py [path/to/digger] [--verbose]
Exits non-zero if any expectation fails.
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIGGER = next((a for a in sys.argv[1:] if not a.startswith("--")), os.path.join(ROOT, "artifacts/bin/Digger/debug/digger"))
VERBOSE = "--verbose" in sys.argv
PROGRAM = "artifacts/bin/HelloDebug/debug/HelloDebug.dll"
SOURCE = os.path.join(ROOT, "samples/HelloDebug/Program.cs")
failures = []


def line_of(marker):
    with open(SOURCE) as f:
        for number, text in enumerate(f, 1):
            if marker in text:
                return number
    raise SystemExit(f"marker not found: {marker}")


def run(args, commands):
    env = dict(os.environ, NO_COLOR="1")
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

out = run(["exec", PROGRAM, "--", "fail"], ["continue", "restart", "continue", "exit"])
expect("exit status and restart", out, "has exited with status 3", "Process restarted", "HelloDebug starting\n")

out = run(["exec", PROGRAM], ["next", "next", "step", "exit"])
expect("next before start stops in Main", out, "> HelloDebug.Program.Main() ./samples/HelloDebug/Program.cs:")

out = run(["exec", PROGRAM], ["b Nope.cs:3", "b Program.cs: 9", "frobnicate", "print x", "exit"])
expect("errors are reported", out, "No source file matches 'Nope.cs'", "is not a location", "command not available: frobnicate",
       "The program is not running yet")

out = run(["debug", "samples/HelloDebug"], [f"b Program.cs:{step_into}", "c", "p number", "exit"])
expect("debug builds and runs", out, "Build succeeded", f"[Breakpoint 1] HelloDebug.Program.Main()", "(digger) p number\n7")

print("FAIL" if failures else "PASS")
sys.exit(1 if failures else 0)
