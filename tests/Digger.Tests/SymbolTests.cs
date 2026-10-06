using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Digger.Engine.Symbols;
using Xunit;

namespace Digger.Tests;

/// <summary>Reads the HelloDebug sample's real PDB.</summary>
public sealed class SymbolTests : IDisposable
{
    private readonly ModuleMetadata _module;
    private readonly string _source;

    public SymbolTests()
    {
        _module = ModuleMetadata.TryOpen(Path.Combine(AppContext.BaseDirectory, "HelloDebug.dll"), loadSymbols: true)
            ?? throw new InvalidOperationException("HelloDebug.dll was not copied next to the tests.");
        _source = Path.GetFullPath(Path.Combine(RepoRoot(), "samples", "HelloDebug", "Program.cs"));
    }

    private SymbolReader Symbols => _module.Symbols ?? throw new InvalidOperationException("No PDB.");

    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private int LineOf(string marker) =>
        Array.FindIndex(File.ReadAllLines(_source), l => l.Contains(marker, StringComparison.Ordinal)) + 1;

    [Fact]
    public void ResolvesBreakpointToMethodAndOffset()
    {
        var line = LineOf("var result = a + b;");
        var resolution = Symbols.ResolveBreakpoint(_source, line);
        Assert.NotNull(resolution);
        Assert.Equal(line, resolution.Location.StartLine);
        var target = Assert.Single(resolution.Targets);
        Assert.Equal("Add", _module.GetMethodName(target.MethodToken));
    }

    [Fact]
    public void BlankLineBindsToNextStatement()
    {
        var line = LineOf("var total = 0;") - 1; // blank line above
        var resolution = Symbols.ResolveBreakpoint(_source, line);
        Assert.NotNull(resolution);
        Assert.Equal(line + 1, resolution.Location.StartLine);
    }

    [Fact]
    public void LineInsideLambdaPrefersEnclosingStatement()
    {
        var line = LineOf("numbers.Select(n => n * number)");
        var resolution = Symbols.ResolveBreakpoint(_source, line)!;
        var target = Assert.Single(resolution.Targets);
        Assert.Equal("MoveNext", _module.GetMethodName(target.MethodToken)); // async Main body
    }

    [Fact]
    public void LineInsideMultiLineLambdaBindsToTheLambda()
    {
        var line = LineOf("inside package callback");
        var resolution = Symbols.ResolveBreakpoint(_source, line)!;
        Assert.Equal(line, resolution.Location.StartLine);
        Assert.StartsWith("<Main>b__", _module.GetMethodName(Assert.Single(resolution.Targets).MethodToken), StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncMethods_MapBetweenKickoffAndMoveNext()
    {
        var kickoff = _module.FindMethodsByName("Program.ComputeAsync").Single();
        var moveNext = Symbols.GetMoveNextMethod(kickoff);
        Assert.NotNull(moveNext);
        Assert.Equal(kickoff, Symbols.GetKickoffMethod(moveNext.Value));
        Assert.Equal("HelloDebug.Program.ComputeAsync", _module.GetMethodDisplayName(moveNext.Value));
    }

    [Fact]
    public void LocalsAreScopedToOffsets()
    {
        var add = _module.FindMethodsByName("Program.Add").Single();
        var offset = Symbols.GetFirstStatementOffset(add)!.Value;
        Assert.Contains(Symbols.GetLocals(add, offset), l => l.Name == "result");
        Assert.Equal(["a", "b"], _module.GetParameterNames(add));
    }

    [Fact]
    public void StepRangeCoversExactlyOneStatement()
    {
        var add = _module.FindMethodsByName("Program.Add").Single();
        var points = Symbols.GetSequencePoints(add).Where(p => !p.IsHidden).ToArray();
        var (start, end) = Symbols.GetStepRange(add, (uint)points[1].Offset, methodSize: 100);
        Assert.Equal((uint)points[1].Offset, start);
        Assert.Equal((uint)points[2].Offset, end);
    }

    [Fact]
    public void EnumMembersAndFlags()
    {
        var access = _module.Reader.TypeDefinitions
            .Select(h => (Handle: h, Name: _module.Reader.GetString(_module.Reader.GetTypeDefinition(h).Name)))
            .Single(t => t.Name == "Access");
        var token = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(access.Handle);
        Assert.True(_module.IsFlagsEnum(token));
        Assert.Contains((2UL, "Write"), _module.GetEnumMembers(token));
    }

    [Fact]
    public void DebugBuildsAreNotOptimized() => Assert.False(_module.IsOptimized);

    [Fact]
    public void ReadsDebuggerDisplay()
    {
        var order = _module.FindType("HelloDebug.Order");
        Assert.NotEqual(0u, order);
        Assert.Equal("Order #{Id} for {Customer,nq} ({Lines.Count} lines)", _module.GetDebuggerDisplay(order));
        Assert.Null(_module.GetDebuggerDisplay(_module.FindType("HelloDebug.Program")));
    }

    [Fact]
    public void DetectsEnumerableTypes()
    {
        var iterator = _module.Reader.TypeDefinitions
            .Select(h => (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(h))
            .Single(t => _module.GetTypeName(t).Contains("<Squares>", StringComparison.Ordinal));
        Assert.True(_module.DeclaresEnumerable(iterator));
        Assert.False(_module.DeclaresEnumerable(_module.FindType("HelloDebug.Order")));
    }

    [Fact]
    public void ReadsEmbeddedSources()
    {
        var text = Symbols.GetEmbeddedSource(_source);
        Assert.NotNull(text);
        Assert.Equal(File.ReadAllText(_source), System.Text.Encoding.UTF8.GetString(text).TrimStart('\uFEFF'));
    }

    [Fact]
    public void FindTypeUsesMetadataNames()
    {
        Assert.NotEqual(0u, _module.FindType("HelloDebug.Extras"));
        Assert.Equal(0u, _module.FindType("HelloDebug.DoesNotExist"));
    }

    public void Dispose() => _module.Dispose();
}
