using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Digger.Engine.Runtime;

namespace Digger.Cli;

/// <summary>What <c>digger debug</c> builds: a project (file or directory) and how.</summary>
internal sealed record BuildSettings(string? Project)
{
    public string Configuration { get; init; } = "Debug";

    public string? Framework { get; init; }

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

/// <summary>Builds a project with <c>dotnet build</c> and finds the assembly it produced.</summary>
internal static class ProjectBuilder
{
    /// <summary>Builds and returns the program path, or null (with the reason printed) on failure.</summary>
    public static string? Build(BuildSettings settings)
    {
        var dotnet = DotnetLocator.Find(null) ?? "dotnet";
        List<string> common = [];
        if (settings.Project is { } project)
        {
            common.Add(project);
        }

        common.Add("-p:Configuration=" + settings.Configuration);
        if (settings.Framework is { } framework)
        {
            common.Add("-p:TargetFramework=" + framework);
        }

        common.AddRange(settings.ExtraArguments);

        if (Run(dotnet, ["build", "-nologo", .. common], captureOutput: false).ExitCode is not 0 and var code)
        {
            Console.Error.WriteLine($"digger: build failed (dotnet build exited with {code})");
            return null;
        }

        return FindTargetPath(dotnet, common);
    }

    /// <summary>The built assembly of an already-built project.</summary>
    public static string? FindTargetPath(BuildSettings settings)
    {
        var dotnet = DotnetLocator.Find(null) ?? "dotnet";
        List<string> common = settings.Project is { } project ? [project] : [];
        common.Add("-p:Configuration=" + settings.Configuration);
        if (settings.Framework is { } framework)
        {
            common.Add("-p:TargetFramework=" + framework);
        }

        common.AddRange(settings.ExtraArguments);
        return FindTargetPath(dotnet, common);
    }

    private static string? FindTargetPath(string dotnet, List<string> common)
    {
        var (exitCode, output) = Run(dotnet, ["msbuild", "-nologo", "-getProperty:TargetPath", "-getProperty:TargetFrameworks", .. common], captureOutput: true);
        if (exitCode != 0)
        {
            Console.Error.Write(output);
            Console.Error.WriteLine("digger: could not determine the program built by the project");
            return null;
        }

        var (targetPath, targetFrameworks) = ParseProperties(output);
        if (string.IsNullOrEmpty(targetPath))
        {
            Console.Error.WriteLine(string.IsNullOrEmpty(targetFrameworks)
                ? "digger: the project does not produce a program (no TargetPath)"
                : $"digger: the project targets several frameworks ({targetFrameworks}); choose one with --framework");
            return null;
        }

        if (!File.Exists(targetPath))
        {
            Console.Error.WriteLine($"digger: '{targetPath}' does not exist; was the project built?");
            return null;
        }

        return targetPath;
    }

    /// <summary>Reads the JSON that <c>-getProperty</c> prints for several properties.</summary>
    internal static (string? TargetPath, string? TargetFrameworks) ParseProperties(string output)
    {
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, null);
        }

        using var document = System.Text.Json.JsonDocument.Parse(output[start..]);
        if (!document.RootElement.TryGetProperty("Properties", out var properties))
        {
            return (null, null);
        }

        return (Get("TargetPath"), Get("TargetFrameworks"));

        string? Get(string name) => properties.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> arguments, bool captureOutput)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var output = captureOutput ? process.StandardOutput.ReadToEnd() : "";
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
