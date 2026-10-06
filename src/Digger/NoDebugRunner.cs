using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Digger.Engine.Runtime;

namespace Digger;

/// <summary>Runs a program without the debugger ("Run without debugging").</summary>
internal static class NoDebugRunner
{
    public static Process Start(Protocol.LaunchArguments arguments, Action<string, string> output, Action<int> exited)
    {
        var program = Path.GetFullPath(arguments.Program!);
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = arguments.Cwd is { Length: > 0 } cwd ? Path.GetFullPath(cwd) : Path.GetDirectoryName(program),
        };

        if (program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = DotnetLocator.Find(arguments.DotnetPath) ?? "dotnet";
            info.ArgumentList.Add(program);
        }
        else
        {
            info.FileName = program;
        }

        foreach (var argument in arguments.Args ?? [])
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in arguments.Env ?? [])
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start '{program}'.");
        process.StandardInput.Close();
        var stdout = PumpAsync(process.StandardOutput, "stdout", output);
        var stderr = PumpAsync(process.StandardError, "stderr", output);
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            exited(process.ExitCode);
        });
        return process;
    }

    private static async Task PumpAsync(StreamReader reader, string category, Action<string, string> output)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            output(category, new string(buffer, 0, read));
        }
    }
}
