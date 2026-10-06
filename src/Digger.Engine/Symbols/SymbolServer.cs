using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using Digger.Engine.Infrastructure;

namespace Digger.Engine.Symbols;

/// <summary>
/// Downloads portable PDBs from symbol servers using the SSQP layout
/// (<c>{server}/{pdb name}/{guid}{age}/{pdb name}</c>, age <c>FFFFFFFF</c> for portable PDBs).
/// The .NET runtime and most NuGet packages publish symbols with Source Link, so a
/// downloaded PDB usually brings source along with it.
/// </summary>
internal static class SymbolServer
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly HashSet<string> Attempted = new(StringComparer.Ordinal);

    private static readonly string[] Servers =
    [
        "https://msdl.microsoft.com/download/symbols",
        "https://symbols.nuget.org/download/symbols",
    ];

    /// <summary>A local copy of the module's PDB, or null if no server has it.</summary>
    public static string? TryGet(ModuleMetadata module)
    {
        if (module.SymbolServerKey is not var (pdbName, key))
        {
            Log.Info($"No CodeView entry in {module.Path}");
            return null;
        }

        Log.Info($"Symbol lookup {pdbName}/{key}");

        var path = CachePath(pdbName, key);
        if (File.Exists(path))
        {
            return path;
        }

        // One network attempt per PDB per session.
        if (!Attempted.Add(key))
        {
            return null;
        }

        foreach (var server in Servers)
        {
            try
            {
                using var response = Http.GetAsync(new Uri($"{server}/{pdbName}/{key}/{pdbName}")).GetAwaiter().GetResult();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    continue;
                }

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                // Portable PDBs are ECMA-335 metadata ("BSJB"); Windows PDBs are not readable here.
                if (bytes.Length < 4 || bytes[0] != (byte)'B' || bytes[1] != (byte)'S' || bytes[2] != (byte)'J' || bytes[3] != (byte)'B')
                {
                    continue;
                }

                _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                Log.Info($"Downloaded symbols {pdbName} from {server}");
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Threading.Tasks.TaskCanceledException)
            {
                Log.Warn($"Symbol server {server} failed for {pdbName}: {ex.Message}");
            }
        }

        return null;
    }

    private static string CachePath(string pdbName, string key) =>
        Path.Combine(SourceResolver.CacheDirectory, "symbols", pdbName, key, pdbName);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("digger/0.1");
        return client;
    }
}
