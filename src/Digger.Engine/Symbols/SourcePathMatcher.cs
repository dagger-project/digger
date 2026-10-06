using System;
using System.IO;

namespace Digger.Engine.Symbols;

/// <summary>
/// Decides whether a path from the editor and a document path recorded in a PDB refer to
/// the same file. Exact matches win; suffix matches are accepted only for PDB paths that
/// do not exist locally (deterministic "/_/" builds or binaries built on another machine),
/// so two different <c>Program.cs</c> files never collide.
/// </summary>
public static class SourcePathMatcher
{
    private const int ExactScore = 10_000;

    private static StringComparison Comparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Returns 0 for no match; higher is better.</summary>
    public static int Score(string requestedPath, string documentPath)
    {
        if (string.Equals(requestedPath, documentPath, Comparison))
        {
            return ExactScore;
        }

        var requested = Normalize(requestedPath);
        var document = Normalize(documentPath);
        if (string.Equals(requested, document, Comparison))
        {
            return ExactScore;
        }

        var matched = CountMatchingTrailingSegments(requested, document);
        if (matched == 0)
        {
            return 0;
        }

        var isMapped = document.StartsWith("/_/", StringComparison.Ordinal)
            || document.StartsWith("/_1/", StringComparison.Ordinal);
        var minimum = isMapped ? 1 : 2;
        if (matched < minimum || (!isMapped && File.Exists(documentPath)))
        {
            return 0;
        }

        return matched;
    }

    internal static string Normalize(string path) => path.Replace('\\', '/');

    internal static int CountMatchingTrailingSegments(string a, string b)
    {
        var segmentsA = a.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segmentsB = b.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        for (int i = segmentsA.Length - 1, j = segmentsB.Length - 1; i >= 0 && j >= 0; i--, j--)
        {
            if (!string.Equals(segmentsA[i], segmentsB[j], Comparison))
            {
                break;
            }

            count++;
        }

        return count;
    }
}
