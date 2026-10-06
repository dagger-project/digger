using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Digger.Engine.Evaluation;

/// <summary>AST for the C# expression subset the evaluator understands.</summary>
public abstract record Expr;

public sealed record LiteralExpr(object? Value) : Expr;

public sealed record NameExpr(string Name) : Expr;

public sealed record MemberExpr(Expr Target, string Name) : Expr;

public sealed record IndexExpr(Expr Target, Expr Index) : Expr;

public sealed record CallExpr(Expr Target, string Method, IReadOnlyList<Expr> Arguments) : Expr;

public sealed record UnaryExpr(string Operator, Expr Operand) : Expr;

public sealed record BinaryExpr(string Operator, Expr Left, Expr Right) : Expr;

public sealed record ConditionalExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr;

/// <summary>A syntax error in a debugger expression.</summary>
public sealed class ExpressionException : Exception
{
    public ExpressionException(string message)
        : base(message)
    {
    }

    public ExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Recursive-descent parser for: literals, names, <c>this</c>, member access, indexers,
/// method calls, unary <c>! - ~</c>, arithmetic, comparison, logical operators, <c>??</c>
/// and the conditional operator — with C# precedence.
/// </summary>
public sealed class ExpressionParser
{
    private readonly List<Token> _tokens;
    private int _position;

    private ExpressionParser(List<Token> tokens) => _tokens = tokens;

    public static Expr Parse(string text)
    {
        var parser = new ExpressionParser(Tokenize(text));
        var expression = parser.ParseConditional();
        if (parser.Current.Kind != TokenKind.End)
        {
            throw new ExpressionException($"Unexpected '{parser.Current.Text}'.");
        }

        return expression;
    }

    // ---- Grammar ----------------------------------------------------------------------

    private Expr ParseConditional()
    {
        var condition = ParseBinary(0);
        if (!Accept("?"))
        {
            return condition;
        }

        var whenTrue = ParseConditional();
        Expect(":");
        return new ConditionalExpr(condition, whenTrue, ParseConditional());
    }

    // Lowest to highest precedence.
    private static readonly string[][] Levels =
    [
        ["??"],
        ["||"],
        ["&&"],
        ["|"],
        ["^"],
        ["&"],
        ["==", "!="],
        ["<", ">", "<=", ">="],
        ["<<", ">>"],
        ["+", "-"],
        ["*", "/", "%"],
    ];

    private Expr ParseBinary(int level)
    {
        if (level == Levels.Length)
        {
            return ParseUnary();
        }

        var left = ParseBinary(level + 1);
        while (Current.Kind == TokenKind.Operator && Array.IndexOf(Levels[level], Current.Text) >= 0)
        {
            var op = Next().Text;
            var right = ParseBinary(op == "??" ? level : level + 1);
            left = new BinaryExpr(op, left, right);
        }

        return left;
    }

    private Expr ParseUnary()
    {
        if (Current.Kind == TokenKind.Operator && Current.Text is "!" or "-" or "+" or "~")
        {
            var op = Next().Text;
            return new UnaryExpr(op, ParseUnary());
        }

        return ParsePostfix(ParsePrimary());
    }

    private Expr ParsePostfix(Expr expression)
    {
        while (true)
        {
            if (Accept(".") || Accept("?."))
            {
                var name = Expect(TokenKind.Identifier).Text;
                if (Accept("("))
                {
                    expression = new CallExpr(expression, name, ParseArguments());
                }
                else
                {
                    expression = new MemberExpr(expression, name);
                }
            }
            else if (Accept("["))
            {
                var index = ParseConditional();
                Expect("]");
                expression = new IndexExpr(expression, index);
            }
            else if (Current.Kind == TokenKind.Operator && Current.Text == "(" && expression is NameExpr name)
            {
                // Method on the implicit 'this'.
                _ = Next();
                expression = new CallExpr(new NameExpr("this"), name.Name, ParseArguments());
            }
            else
            {
                return expression;
            }
        }
    }

    private List<Expr> ParseArguments()
    {
        var arguments = new List<Expr>();
        if (Accept(")"))
        {
            return arguments;
        }

        do
        {
            arguments.Add(ParseConditional());
        }
        while (Accept(","));

        Expect(")");
        return arguments;
    }

    private Expr ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
            case TokenKind.String:
            case TokenKind.Char:
                return new LiteralExpr(token.Value);
            case TokenKind.Identifier:
                return token.Text switch
                {
                    "true" => new LiteralExpr(true),
                    "false" => new LiteralExpr(false),
                    "null" => new LiteralExpr(null),
                    _ => new NameExpr(token.Text),
                };
            case TokenKind.Operator when token.Text == "(":
                var inner = ParseConditional();
                Expect(")");
                return inner;
            case TokenKind.End:
                throw new ExpressionException("Unexpected end of expression.");
            default:
                throw new ExpressionException($"Unexpected '{token.Text}'.");
        }
    }

    // ---- Token helpers ----------------------------------------------------------------

    private Token Current => _tokens[_position];

    private Token Next() => _tokens[_position < _tokens.Count - 1 ? _position++ : _position];

    private bool Accept(string op)
    {
        if (Current.Kind == TokenKind.Operator && Current.Text == op)
        {
            _position++;
            return true;
        }

        return false;
    }

    private void Expect(string op)
    {
        if (!Accept(op))
        {
            throw new ExpressionException($"Expected '{op}' but found '{Current.Text}'.");
        }
    }

    private Token Expect(TokenKind kind) =>
        Current.Kind == kind ? Next() : throw new ExpressionException($"Expected {kind} but found '{Current.Text}'.");

    // ---- Lexer ------------------------------------------------------------------------

    private enum TokenKind
    {
        Identifier,
        Number,
        String,
        Char,
        Operator,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, object? Value = null);

    private static readonly string[] Operators =
    [
        "??", "?.", "||", "&&", "==", "!=", "<=", ">=", "<<", ">>",
        "+", "-", "*", "/", "%", "!", "~", "<", ">", "&", "|", "^",
        "(", ")", "[", "]", ".", ",", "?", ":",
    ];

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (char.IsLetter(c) || c is '_' or '@' or '$')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i].TrimStart('@')));
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                tokens.Add(ReadNumber(text, ref i));
            }
            else if (c == '"')
            {
                tokens.Add(ReadString(text, ref i));
            }
            else if (c == '\'')
            {
                var token = ReadString(text, ref i, '\'');
                var value = (string)token.Value!;
                if (value.Length != 1)
                {
                    throw new ExpressionException("Invalid character literal.");
                }

                tokens.Add(new Token(TokenKind.Char, token.Text, value[0]));
            }
            else
            {
                var op = Array.Find(Operators, o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0)
                    ?? throw new ExpressionException($"Unexpected character '{c}'.");
                tokens.Add(new Token(TokenKind.Operator, op));
                i += op.Length;
            }
        }

        tokens.Add(new Token(TokenKind.End, "<end>"));
        return tokens;
    }

    private static Token ReadNumber(string text, ref int i)
    {
        var start = i;
        var culture = CultureInfo.InvariantCulture;
        if (text[i] == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
        {
            i += 2;
            var hexStart = i;
            while (i < text.Length && (char.IsAsciiHexDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }

            var hex = text[hexStart..i].Replace("_", string.Empty, StringComparison.Ordinal);
            SkipIntegerSuffix(text, ref i);
            return new Token(TokenKind.Number, text[start..i], long.Parse(hex, NumberStyles.HexNumber, culture));
        }

        var isReal = false;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_' || text[i] == '.'
            || ((text[i] is 'e' or 'E') && isReal) || ((text[i] is '+' or '-') && text[i - 1] is 'e' or 'E')))
        {
            isReal |= text[i] == '.';
            i++;
        }

        var digits = text[start..i].Replace("_", string.Empty, StringComparison.Ordinal);
        if (i < text.Length && text[i] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M')
        {
            var suffix = char.ToLowerInvariant(text[i++]);
            object real = suffix switch
            {
                'f' => float.Parse(digits, culture),
                'm' => decimal.Parse(digits, culture),
                _ => double.Parse(digits, culture),
            };
            return new Token(TokenKind.Number, text[start..i], real);
        }

        if (isReal)
        {
            return new Token(TokenKind.Number, text[start..i], double.Parse(digits, culture));
        }

        SkipIntegerSuffix(text, ref i);
        object integer = int.TryParse(digits, NumberStyles.None, culture, out var small) ? small
            : long.TryParse(digits, NumberStyles.None, culture, out var large) ? large
            : ulong.Parse(digits, NumberStyles.None, culture);
        return new Token(TokenKind.Number, text[start..i], integer);
    }

    private static void SkipIntegerSuffix(string text, ref int i)
    {
        while (i < text.Length && text[i] is 'u' or 'U' or 'l' or 'L')
        {
            i++;
        }
    }

    private static Token ReadString(string text, ref int i, char quote = '"')
    {
        var start = i++;
        var builder = new StringBuilder();
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                _ = builder.Append(text[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    var other => other,
                });
            }
            else
            {
                _ = builder.Append(text[i]);
            }

            i++;
        }

        if (i >= text.Length)
        {
            throw new ExpressionException("Unterminated literal.");
        }

        i++;
        return new Token(TokenKind.String, text[start..i], builder.ToString());
    }
}
