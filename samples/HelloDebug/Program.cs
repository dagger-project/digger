// A small program that exercises the debugger: locals, objects, collections,
// async state machines, lambdas, exceptions and threads.

namespace HelloDebug;

public enum Color { Red, Green, Blue }

[Flags]
public enum Access { None = 0, Read = 1, Write = 2, Execute = 4 }

public sealed record Person(string Name, int Age)
{
    public string Greeting => $"Hello, {Name}!";
    public List<string> Tags { get; } = ["a", "b"];
}

public static class Program
{
    private static int s_counter = 42;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("HelloDebug starting");
        switch (args.FirstOrDefault())
        {
            case "spin":                                   // for pause / attach
                for (var tick = 0; tick < 600; tick++)
                {
                    Tick(tick);
                }

                return 0;
            case "crash":                                  // unhandled exception
                Fail("crash");
                break;
            case "extras":                                 // DebuggerDisplay, collections, async step-out
                return await Extras.RunAsync();
            case "package":                                // a NuGet package frame below user code
                using (Microsoft.Extensions.Primitives.ChangeToken.OnChange(
                    () =>
                    {
                        Console.WriteLine("called from a package"); // inside package callback
                        return new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
                    },
                    () => { }))
                {
                }

                return 0;
            case "read":                                   // reads stdin (console: integratedTerminal)
                Console.Write("Your name? ");
                var name = Console.ReadLine();
                Console.WriteLine($"Hi {name}");           // after read
                return name?.Length ?? -1;
        }

        var number = 7;
        var pi = 3.14159;
        var text = "line1\nline2 \"quoted\"";
        var person = new Person("Ada", 36);
        var numbers = new List<int> { 1, 2, 3, 5, 8 };
        var map = new Dictionary<string, int>(StringComparer.Ordinal) { ["one"] = 1, ["two"] = 2 };
        var array = new[] { 10, 20, 30 };
        var grid = new int[2, 3] { { 1, 2, 3 }, { 4, 5, 6 } };
        decimal price = 19.99m;
        var when = new DateTime(2024, 5, 17, 13, 45, 0, DateTimeKind.Utc);
        var color = Color.Green;
        var access = Access.Read | Access.Write;
        int? maybe = 5;
        int? nothing = null;
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        var sum = Add(number, 3);                          // step into here
        Console.WriteLine($"sum = {sum}");

        var total = 0;
        for (var i = 0; i < 10; i++)
        {
            total += i;                                    // conditional breakpoint: i == 5
        }

        var doubled = numbers.Select(n => n * number).ToList();
        Console.WriteLine($"doubled: {string.Join(",", doubled)}, total {total}");

        var result = await ComputeAsync(person, 3);
        Console.WriteLine($"async result = {result}");

        var worker = new Thread(() => Console.WriteLine("worker thread ran")) { Name = "Worker" };
        worker.Start();
        worker.Join();

        try
        {
            Fail(person.Name);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"caught: {ex.Message}");
        }

        Console.Error.WriteLine("done (stderr)");
        GC.KeepAlive((pi, text, map, array, grid, price, when, color, access, maybe, nothing, id, s_counter));
        return args.Length > 0 && args[0] == "fail" ? 3 : 0;
    }

    private static void Tick(int tick)
    {
        Thread.Sleep(100);                                 // spin loop body
        s_counter = tick;
    }

    private static int Add(int a, int b)
    {
        var result = a + b;
        return result;
    }

    private static async Task<int> ComputeAsync(Person who, int times)
    {
        var accumulator = 0;
        for (var i = 0; i < times; i++)
        {
            await Task.Delay(10);
            accumulator += who.Age;                        // breakpoint inside async
        }

        return accumulator;
    }

    private static void Fail(string name) =>
        throw new InvalidOperationException($"Something went wrong for {name}");
}
