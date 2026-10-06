using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;

namespace HelloDebug;

[DebuggerDisplay("Order #{Id} for {Customer,nq} ({Lines.Count} lines)")]
public sealed class Order
{
    public int Id { get; init; }
    public string Customer { get; init; } = "";
    public List<string> Lines { get; } = ["apple", "pear"];
}

[DebuggerDisplay("{X}, {Y}")]
public readonly record struct Point(int X, int Y);

public static class Extras
{
    public static async Task<int> RunAsync()
    {
        var order = new Order { Id = 7, Customer = "Ada" };
        var point = new Point(3, 4);
        var queue = new Queue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);
        _ = queue.Dequeue();                               // makes the ring buffer start at 1
        queue.Enqueue(4);
        var linked = new LinkedList<string>(["x", "y", "z"]);
        var immutable = ImmutableArray.Create(5, 6, 7);
        var immutableList = ImmutableList.Create("p", "q");
        var concurrent = new ConcurrentDictionary<string, int>(StringComparer.Ordinal) { ["k1"] = 1, ["k2"] = 2 };
        var pair = new KeyValuePair<string, int>("answer", 42);
        IEnumerable<int> squares = Squares(4);
        Console.WriteLine("extras ready");                 // inspect extras here

        var value = await OuterAsync();
        GC.KeepAlive((order, point, queue, linked, immutable, immutableList, concurrent, pair, squares));
        return value;
    }

    private static IEnumerable<int> Squares(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return i * i;
        }
    }

    private static async Task<int> OuterAsync()
    {
        var inner = await InnerAsync();                    // awaiting caller
        return inner + 1;
    }

    private static async Task<int> InnerAsync()
    {
        await Task.Delay(10);
        var answer = 41;                                   // step out of async from here
        return answer;
    }
}
