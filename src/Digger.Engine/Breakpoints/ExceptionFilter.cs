using System;
using System.Collections.Generic;

namespace Digger.Engine.Breakpoints;

/// <summary>
/// Chooses which thrown exceptions stop the program. Patterns are type names, full
/// (<c>System.IO.IOException</c>) or simple (<c>IOException</c>), or a namespace prefix ending
/// in <c>*</c> (<c>System.IO.*</c>); a leading <c>!</c> excludes. A pattern matches derived
/// types too. With no including patterns every type is selected; exclusions always win.
/// </summary>
public sealed class ExceptionFilter
{
    private readonly List<string> _include = [];
    private readonly List<string> _exclude = [];

    public static ExceptionFilter All { get; } = new();

    /// <summary>Parses patterns separated by commas or whitespace.</summary>
    public static ExceptionFilter Parse(IEnumerable<string> patterns)
    {
        var filter = new ExceptionFilter();
        foreach (var pattern in patterns)
        {
            foreach (var part in pattern.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part[0] == '!')
                {
                    if (part.Length > 1)
                    {
                        filter._exclude.Add(part[1..]);
                    }
                }
                else
                {
                    filter._include.Add(part);
                }
            }
        }

        return filter;
    }

    public static ExceptionFilter Parse(string? patterns) => patterns is null ? All : Parse([patterns]);

    public bool IsEmpty => _include.Count == 0 && _exclude.Count == 0;

    /// <summary>Whether an exception whose type and base types are <paramref name="typeChain"/> is selected.</summary>
    public bool Matches(IReadOnlyList<string> typeChain)
    {
        if (AnyMatch(_exclude, typeChain))
        {
            return false;
        }

        return _include.Count == 0 || AnyMatch(_include, typeChain);
    }

    private static bool AnyMatch(List<string> patterns, IReadOnlyList<string> typeChain)
    {
        foreach (var pattern in patterns)
        {
            foreach (var type in typeChain)
            {
                if (Matches(pattern, type))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Matches(string pattern, string type)
    {
        if (pattern.EndsWith('*'))
        {
            return type.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }

        if (type == pattern)
        {
            return true;
        }

        // A simple name matches the last segment (nested types are Outer.Inner).
        return type.Length > pattern.Length
            && type.EndsWith(pattern, StringComparison.Ordinal)
            && type[type.Length - pattern.Length - 1] == '.';
    }

    /// <summary>E.g. "IOException, ArgumentException" or "all types except OperationCanceledException".</summary>
    public override string ToString()
    {
        var included = _include.Count == 0 ? "all types" : string.Join(", ", _include);
        return _exclude.Count == 0 ? included : included + " except " + string.Join(", ", _exclude);
    }
}
