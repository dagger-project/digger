using System;
using System.Collections.Generic;
using System.Text;

namespace Digger.Engine.Runtime;

/// <summary>
/// Builds a command line that dbgshim's CreateProcess (Windows rules, also on the Unix PAL)
/// splits back into the original arguments.
/// </summary>
public static class CommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        Append(builder, executable);
        foreach (var argument in arguments)
        {
            _ = builder.Append(' ');
            Append(builder, argument);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            _ = builder.Append(argument);
            return;
        }

        _ = builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are doubled, and the quote is escaped.
                _ = builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                _ = builder.Append('\\', backslashes);
            }

            backslashes = 0;
            _ = builder.Append(c);
        }

        // Trailing backslashes precede the closing quote, so they are doubled.
        _ = builder.Append('\\', backslashes * 2).Append('"');
    }
}
