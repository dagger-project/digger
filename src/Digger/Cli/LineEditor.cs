using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Digger.Cli;

/// <summary>Completion candidates for the word that starts at <c>Start</c> in the line.</summary>
internal readonly record struct Completion(int Start, IReadOnlyList<string> Candidates);

/// <summary>
/// A small readline: cursor movement, emacs-style editing keys, persistent history and tab
/// completion. Falls back to <see cref="Console.ReadLine"/> when stdin is not a terminal.
/// </summary>
internal sealed class LineEditor
{
    private const int MaxHistory = 1000;

    private readonly List<string> _history = [];
    private readonly string? _historyPath;
    private readonly bool _interactive;
    private readonly StringBuilder _line = new();
    private int _cursor;
    private string _prompt = "";

    public LineEditor(string? historyPath)
    {
        _historyPath = historyPath;
        _interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("TERM") is not ("dumb" or null or "");
        LoadHistory();
    }

    /// <summary>Called on tab with the line and the cursor position.</summary>
    public Func<string, int, Completion>? Completer { get; set; }

    /// <summary>Reads a line; null at end of input (Ctrl-D on an empty line).</summary>
    public string? ReadLine(string prompt)
    {
        if (!_interactive)
        {
            Console.Write(prompt);
            var line = Console.ReadLine();
            if (line is not null && Console.IsInputRedirected)
            {
                Console.WriteLine(line); // echo scripted input so transcripts read naturally
            }

            return line;
        }

        _prompt = prompt;
        _line.Clear();
        _cursor = 0;
        var historyIndex = _history.Count;
        var draft = "";
        Console.Write(prompt);

        var treatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
                var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        var result = _line.ToString();
                        AddHistory(result);
                        return result;
                    case ConsoleKey.Backspace:
                        if (_cursor > 0)
                        {
                            _ = _line.Remove(--_cursor, 1);
                        }

                        break;
                    case ConsoleKey.Delete:
                        if (_cursor < _line.Length)
                        {
                            _ = _line.Remove(_cursor, 1);
                        }

                        break;
                    case ConsoleKey.LeftArrow:
                        _cursor = control || alt ? WordStart() : Math.Max(0, _cursor - 1);
                        break;
                    case ConsoleKey.RightArrow:
                        _cursor = control || alt ? WordEnd() : Math.Min(_line.Length, _cursor + 1);
                        break;
                    case ConsoleKey.Home:
                        _cursor = 0;
                        break;
                    case ConsoleKey.End:
                        _cursor = _line.Length;
                        break;
                    case ConsoleKey.UpArrow:
                        Recall(-1);
                        break;
                    case ConsoleKey.DownArrow:
                        Recall(+1);
                        break;
                    case ConsoleKey.Tab:
                        Complete();
                        break;
                    case ConsoleKey.Escape:
                        break;
                    default:
                        if (control)
                        {
                            if (HandleControl(key.Key) is { } done)
                            {
                                return done.Length == 0 && key.Key == ConsoleKey.D ? null : done;
                            }
                        }
                        else if (alt && key.Key == ConsoleKey.B)
                        {
                            _cursor = WordStart();
                        }
                        else if (alt && key.Key == ConsoleKey.F)
                        {
                            _cursor = WordEnd();
                        }
                        else if (!char.IsControl(key.KeyChar))
                        {
                            _ = _line.Insert(_cursor++, key.KeyChar);
                        }

                        break;
                }

                Render();
            }
        }
        finally
        {
            Console.TreatControlCAsInput = treatControlC;
        }

        void Recall(int direction)
        {
            var next = historyIndex + direction;
            if (next < 0 || next > _history.Count)
            {
                return;
            }

            if (historyIndex == _history.Count)
            {
                draft = _line.ToString();
            }

            historyIndex = next;
            _ = _line.Clear().Append(historyIndex == _history.Count ? draft : _history[historyIndex]);
            _cursor = _line.Length;
        }
    }

    /// <summary>Handles a Ctrl key; returns the finished line for keys that end input.</summary>
    private string? HandleControl(ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.A:
                _cursor = 0;
                break;
            case ConsoleKey.E:
                _cursor = _line.Length;
                break;
            case ConsoleKey.B:
                _cursor = Math.Max(0, _cursor - 1);
                break;
            case ConsoleKey.F:
                _cursor = Math.Min(_line.Length, _cursor + 1);
                break;
            case ConsoleKey.U:
                _ = _line.Remove(0, _cursor);
                _cursor = 0;
                break;
            case ConsoleKey.K:
                _ = _line.Remove(_cursor, _line.Length - _cursor);
                break;
            case ConsoleKey.W:
                var start = WordStart();
                _ = _line.Remove(start, _cursor - start);
                _cursor = start;
                break;
            case ConsoleKey.L:
                Console.Write("\e[H\e[2J");
                break;
            case ConsoleKey.C:
                Console.WriteLine("^C");
                return "";
            case ConsoleKey.D:
                if (_line.Length == 0)
                {
                    Console.WriteLine();
                    return "";
                }

                if (_cursor < _line.Length)
                {
                    _ = _line.Remove(_cursor, 1);
                }

                break;
        }

        return null;
    }

    private void Complete()
    {
        if (Completer is null)
        {
            return;
        }

        var line = _line.ToString();
        var (start, candidates) = Completer(line, _cursor);
        if (candidates.Count == 0)
        {
            return;
        }

        var prefix = CommonPrefix(candidates);
        var typed = line[start.._cursor];
        if (prefix.Length > typed.Length || candidates.Count == 1)
        {
            var insert = candidates.Count == 1 ? candidates[0] + (candidates[0].EndsWith('.') || candidates[0].EndsWith(':') ? "" : " ") : prefix;
            _ = _line.Remove(start, _cursor - start).Insert(start, insert);
            _cursor = start + insert.Length;
            return;
        }

        // Nothing more to add: show the choices and redraw the prompt below them.
        Console.WriteLine();
        Console.WriteLine(string.Join("  ", candidates));
        Console.Write(_prompt);
    }

    private static string CommonPrefix(IReadOnlyList<string> values)
    {
        var prefix = values[0];
        foreach (var value in values)
        {
            var length = 0;
            while (length < prefix.Length && length < value.Length && prefix[length] == value[length])
            {
                length++;
            }

            prefix = prefix[..length];
        }

        return prefix;
    }

    private int WordStart()
    {
        var position = _cursor;
        while (position > 0 && _line[position - 1] == ' ')
        {
            position--;
        }

        while (position > 0 && _line[position - 1] != ' ')
        {
            position--;
        }

        return position;
    }

    private int WordEnd()
    {
        var position = _cursor;
        while (position < _line.Length && _line[position] == ' ')
        {
            position++;
        }

        while (position < _line.Length && _line[position] != ' ')
        {
            position++;
        }

        return position;
    }

    /// <summary>Redraws the line. Lines wider than the terminal scroll horizontally.</summary>
    private void Render()
    {
        var width = Math.Max(20, SafeWindowWidth() - _prompt.Length - 1);
        var offset = _cursor < width ? 0 : _cursor - width + 1;
        var visible = _line.ToString(offset, Math.Min(width, _line.Length - offset));
        var builder = new StringBuilder();
        _ = builder.Append('\r').Append(_prompt).Append(visible).Append("\e[K");
        var back = offset + visible.Length - _cursor;
        if (back > 0)
        {
            _ = builder.Append("\e[").Append(back).Append('D');
        }

        Console.Write(builder.ToString());
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        }
        catch (IOException)
        {
            return 80;
        }
    }

    // ---- History ------------------------------------------------------------------------

    private void LoadHistory()
    {
        if (_historyPath is null || !File.Exists(_historyPath))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_historyPath);
            _history.AddRange(lines.AsSpan(Math.Max(0, lines.Length - MaxHistory)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a broken file must not stop the debugger.
        }
    }

    private void AddHistory(string line)
    {
        if (line.Trim().Length == 0 || (_history.Count > 0 && _history[^1] == line))
        {
            return;
        }

        _history.Add(line);
        if (_historyPath is null)
        {
            return;
        }

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            if (_history.Count > MaxHistory * 2)
            {
                _history.RemoveRange(0, _history.Count - MaxHistory);
                File.WriteAllLines(_historyPath, _history);
            }
            else
            {
                File.AppendAllLines(_historyPath, [line]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See LoadHistory.
        }
    }
}
