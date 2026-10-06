using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Digger.Engine.Infrastructure;
using Digger.Engine.Runtime;

namespace Digger.Engine.Symbols;

/// <summary>
/// Turns a document path recorded in a PDB into a file the editor can open:
/// <list type="number">
/// <item><c>sourceFileMap</c> prefix replacement (binaries built in a container or on CI),</item>
/// <item>the recorded path itself, if it exists,</item>
/// <item>the source embedded in the PDB, written to the cache,</item>
/// <item>the Source Link URL, downloaded to the cache.</item>
/// </list>
/// Cached files keep their original file name so editors pick the right language. The
/// reverse map lets breakpoints be set in cached and mapped files.
/// </summary>
public sealed class SourceResolver(IReadOnlyDictionary<string, string>? sourceFileMap, bool sourceLink)
{
    private static readonly HttpClient Http = CreateClient();

    private readonly List<(string From, string To)> _map = SortedMap(sourceFileMap);
    private readonly Dictionary<(ulong Module, string Document), string> _resolved = [];
    private readonly Dictionary<string, string> _localToDocument = new(StringComparer.Ordinal);

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "digger");

    /// <summary>A local path for <paramref name="document"/> (the input if nothing better is found).</summary>
    public string Resolve(LoadedModule? module, string document)
    {
        var key = (module?.BaseAddress ?? 0, document);
        if (_resolved.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = ResolveCore(module, document);
        _resolved[key] = resolved;
        if (!string.Equals(resolved, document, StringComparison.Ordinal))
        {
            _localToDocument[resolved] = document;
        }

        return resolved;
    }

    /// <summary>Maps an editor path back to the path recorded in PDBs (for breakpoints).</summary>
    public string ToDocumentPath(string localPath)
    {
        if (_localToDocument.TryGetValue(localPath, out var document))
        {
            return document;
        }

        foreach (var (from, to) in _map)
        {
            if (localPath.StartsWith(to, StringComparison.Ordinal))
            {
                return from + localPath[to.Length..];
            }
        }

        return localPath;
    }

    private string ResolveCore(LoadedModule? module, string document)
    {
        var mapped = MapToLocal(document);
        if (File.Exists(mapped))
        {
            return mapped;
        }

        if (module?.Symbols is not { } symbols)
        {
            return mapped;
        }

        var fileName = Path.GetFileName(document.Replace('\\', '/'));
        try
        {
            if (symbols.GetEmbeddedSource(document) is { } embedded)
            {
                var path = CachePath("embedded", module.Path + "|" + document, fileName);
                if (!File.Exists(path))
                {
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, embedded);
                }

                return path;
            }

            if (sourceLink && symbols.GetSourceLinkUrl(document) is { } url)
            {
                var path = CachePath("sourcelink", url.AbsoluteUri, fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                Log.Info($"Source Link: downloading {url}");
                var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                return path;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or System.Threading.Tasks.TaskCanceledException)
        {
            Log.Warn($"Could not get source for '{document}': {ex.Message}");
        }

        return mapped;
    }

    private string MapToLocal(string document)
    {
        foreach (var (from, to) in _map)
        {
            if (document.StartsWith(from, StringComparison.Ordinal))
            {
                return to + document[from.Length..];
            }
        }

        return document;
    }

    private static string CachePath(string kind, string identity, string fileName)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(CacheDirectory, "sources", kind, hash, fileName);
    }

    private static List<(string, string)> SortedMap(IReadOnlyDictionary<string, string>? map)
    {
        var result = new List<(string From, string To)>();
        foreach (var (from, to) in map ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            result.Add((from.Replace('\\', '/'), to));
        }

        // Longest prefix first.
        result.Sort(static (a, b) => b.From.Length.CompareTo(a.From.Length));
        return result;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("digger/0.1");
        return client;
    }
}
