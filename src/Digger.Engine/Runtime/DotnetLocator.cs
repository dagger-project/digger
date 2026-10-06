using System;
using System.IO;

namespace Digger.Engine.Runtime;

/// <summary>Finds the <c>dotnet</c> host used to run framework-dependent .dll programs.</summary>
public static class DotnetLocator
{
    public static string? Find(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var variable in (ReadOnlySpan<string>)["DOTNET_HOST_PATH"])
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } hostPath && File.Exists(hostPath))
            {
                return hostPath;
            }
        }

        foreach (var variable in (ReadOnlySpan<string>)["DOTNET_ROOT", "DOTNET_INSTALL_DIR"])
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root
                && Path.Combine(root, executable) is var candidate && File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                // Resolve symlinks such as /usr/bin/dotnet -> /usr/share/dotnet/dotnet.
                return new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in (ReadOnlySpan<string>)["/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/local/share/dotnet/dotnet", Path.Combine(home, ".dotnet", "dotnet")])
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
