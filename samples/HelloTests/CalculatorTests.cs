// A test project to debug with `digger test samples/HelloTests`.
using Xunit;

namespace HelloTests;

public sealed class CalculatorTests
{
    [Fact]
    public void AddsNumbers()
    {
        var total = Calculator.Add(2, 3);
        Assert.Equal(5, total);                            // assert in AddsNumbers
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(9, 3)]
    public void Divides(int dividend, int divisor)
    {
        var quotient = dividend / divisor;                 // breakpoint in Divides
        Assert.Equal(dividend, quotient * divisor);
    }
}

internal static class Calculator
{
    public static int Add(int a, int b) => a + b;
}
