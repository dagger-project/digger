using System;
using System.Globalization;

namespace Digger.Engine.Breakpoints;

/// <summary>
/// Evaluates DAP hit conditions against a hit count: <c>5</c> (== 5), <c>&gt;= 5</c>,
/// <c>&gt; 5</c>, <c>&lt; 5</c>, <c>&lt;= 5</c>, <c>== 5</c>, <c>% 5</c> (every 5th hit).
/// </summary>
public static class HitCondition
{
    public static bool IsSatisfied(string? condition, int hitCount)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var text = condition.AsSpan().Trim();
        ReadOnlySpan<string> operators = [">=", "<=", "==", ">", "<", "%", "="];
        var op = "==";
        foreach (var candidate in operators)
        {
            if (text.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                text = text[candidate.Length..].Trim();
                break;
            }
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return true; // malformed: behave like a plain breakpoint
        }

        return op switch
        {
            ">=" => hitCount >= n,
            "<=" => hitCount <= n,
            ">" => hitCount > n,
            "<" => hitCount < n,
            "%" => n > 0 && hitCount % n == 0,
            _ => hitCount == n,
        };
    }
}
