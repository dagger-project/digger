using System.Collections.Generic;
using Digger.Engine.Breakpoints;
using Digger.Engine.Evaluation;
using Digger.Engine.Runtime;
using Digger.Engine.Symbols;
using Xunit;

namespace Digger.Tests;

public sealed class EngineTests
{
    [Theory]
    [InlineData(null, 1, true)]
    [InlineData("3", 2, false)]
    [InlineData("3", 3, true)]
    [InlineData(">= 3", 4, true)]
    [InlineData("> 3", 3, false)]
    [InlineData("< 3", 2, true)]
    [InlineData("% 2", 4, true)]
    [InlineData("% 2", 5, false)]
    [InlineData("garbage", 5, true)]
    public void HitCondition_Evaluates(string? condition, int hitCount, bool expected) =>
        Assert.Equal(expected, HitCondition.IsSatisfied(condition, hitCount));

    [Theory]
    [InlineData(new[] { "a" }, "prog a")]
    [InlineData(new[] { "with space" }, "prog \"with space\"")]
    [InlineData(new[] { "" }, "prog \"\"")]
    [InlineData(new[] { "say \"hi\"" }, "prog \"say \\\"hi\\\"\"")]
    [InlineData(new[] { "C:\\path with\\" }, "prog \"C:\\path with\\\\\"")]
    public void CommandLine_QuotesLikeCreateProcess(string[] arguments, string expected) =>
        Assert.Equal(expected, CommandLine.Build("prog", arguments));

    [Fact]
    public void EnvironmentBlock_IsUtf8DoubleNulTerminatedAndHonorsRemovals()
    {
        var block = System.Text.Encoding.UTF8.GetString(RuntimeLauncher.BuildEnvironmentBlock(new Dictionary<string, string?>(System.StringComparer.Ordinal)
        {
            ["DIGGER_TEST_ADDED"] = "1",
            ["DIGGER_TEST_UNICODE"] = "é",
            ["PATH"] = null,
        }));
        Assert.EndsWith("\0\0", block, System.StringComparison.Ordinal);
        Assert.Contains("DIGGER_TEST_ADDED=1\0", block, System.StringComparison.Ordinal);
        Assert.Contains("DIGGER_TEST_UNICODE=é\0", block, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\0PATH=", "\0" + block, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/src/app/Program.cs", "/src/app/Program.cs", true)]
    [InlineData("/home/me/repo/src/app/Program.cs", "/_/src/app/Program.cs", true)]
    [InlineData("/home/me/repo/src/app/Program.cs", "/_/src/other/Program.cs", true)]
    [InlineData("/home/me/a/Program.cs", "/home/me/b/Startup.cs", false)]
    public void SourcePathMatcher_Matches(string requested, string document, bool matches) =>
        Assert.Equal(matches, SourcePathMatcher.Score(requested, document) > 0);

    [Fact]
    public void SourcePathMatcher_PrefersLongerSuffix() =>
        Assert.True(SourcePathMatcher.Score("/r/src/app/Program.cs", "/_/src/app/Program.cs")
            > SourcePathMatcher.Score("/r/src/app/Program.cs", "/_/src/other/Program.cs"));

    [Theory]
    [InlineData("<Main>b__0_0", "Main.AnonymousMethod__0_0")]
    [InlineData("<Main>g__Local|0_1", "Main.Local")]
    [InlineData("MoveNext", "MoveNext")]
    public void MethodNames_ArePrettified(string raw, string expected) =>
        Assert.Equal(expected, ModuleMetadata.PrettifyMethodName(raw));

    [Fact]
    public void TypeNames_DropCompilerGeneratedSegments() =>
        Assert.Equal("App.Program", ModuleMetadata.PrettifyTypeName("App.Program.<>c__DisplayClass0_0"));
}

public sealed class ExpressionParserTests
{
    [Fact]
    public void Precedence_FollowsCSharp()
    {
        var expression = ExpressionParser.Parse("a + b * c == d && !e");
        var and = Assert.IsType<BinaryExpr>(expression);
        Assert.Equal("&&", and.Operator);
        var equals = Assert.IsType<BinaryExpr>(and.Left);
        Assert.Equal("==", equals.Operator);
        var plus = Assert.IsType<BinaryExpr>(equals.Left);
        Assert.Equal("+", plus.Operator);
        Assert.Equal("*", Assert.IsType<BinaryExpr>(plus.Right).Operator);
        Assert.IsType<UnaryExpr>(and.Right);
    }

    [Fact]
    public void Postfix_ChainsMembersIndexersAndCalls()
    {
        var call = Assert.IsType<CallExpr>(ExpressionParser.Parse("this.items[i + 1].Name.ToUpper()"));
        Assert.Equal("ToUpper", call.Method);
        var member = Assert.IsType<MemberExpr>(call.Target);
        Assert.Equal("Name", member.Name);
        var index = Assert.IsType<IndexExpr>(member.Target);
        Assert.IsType<BinaryExpr>(index.Index);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("0x10", 16L)]
    [InlineData("1.5", 1.5)]
    [InlineData("2.5f", 2.5f)]
    [InlineData("'x'", 'x')]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("true", true)]
    public void Literals_ParseToClrValues(string text, object expected) =>
        Assert.Equal(expected, Assert.IsType<LiteralExpr>(ExpressionParser.Parse(text)).Value);

    [Fact]
    public void Conditional_And_NullCoalescing()
    {
        Assert.IsType<ConditionalExpr>(ExpressionParser.Parse("a ? b : c ?? d"));
        Assert.Equal("??", Assert.IsType<BinaryExpr>(ExpressionParser.Parse("a ?? b ?? c")).Operator);
    }

    [Theory]
    [InlineData("a +")]
    [InlineData("(a")]
    [InlineData("a ] b")]
    [InlineData("\"unterminated")]
    public void SyntaxErrors_AreReported(string text) =>
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse(text));
}
