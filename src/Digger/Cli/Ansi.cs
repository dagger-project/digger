using System;

namespace Digger.Cli;

/// <summary>Terminal colors, off when stdout is not a terminal or NO_COLOR is set.</summary>
internal static class Ansi
{
    public static bool Enabled { get; set; } = !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("NO_COLOR") is null or ""
        && Environment.GetEnvironmentVariable("TERM") is not "dumb";

    public static string Bold(string text) => Wrap("1", text);

    public static string Dim(string text) => Wrap("2", text);

    public static string Red(string text) => Wrap("31", text);

    public static string Green(string text) => Wrap("32", text);

    public static string Yellow(string text) => Wrap("33", text);

    public static string Blue(string text) => Wrap("34", text);

    public static string Cyan(string text) => Wrap("36", text);

    /// <summary>Removes color codes, e.g. for a transcript file.</summary>
    public static string? Strip(string? text) =>
        text is null || !text.Contains('\e', StringComparison.Ordinal)
            ? text
            : System.Text.RegularExpressions.Regex.Replace(text, @"\e\[[0-9;]*m", "", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string Wrap(string code, string text) => Enabled ? $"\e[{code}m{text}\e[0m" : text;
}
