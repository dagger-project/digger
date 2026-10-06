using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Digger.Protocol;
using Xunit;

namespace Digger.Tests;

public sealed class ProtocolTests
{
    private static byte[] Frame(string json) =>
        Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");

    [Fact]
    public void TryReadMessage_ReadsConsecutiveMessagesAndLeavesPartialOnes()
    {
        var first = Frame("""{"seq":1,"type":"request","command":"a"}""");
        var second = Frame("""{"seq":2,"type":"request","command":"b"}""");
        var bytes = new byte[first.Length + second.Length - 5];
        first.CopyTo(bytes, 0);
        Array.Copy(second, 0, bytes, first.Length, second.Length - 5);
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.True(DapConnection.TryReadMessage(ref buffer, out var body));
        Assert.Equal("a", DapRequest.Parse(body)!.Command);
        Assert.False(DapConnection.TryReadMessage(ref buffer, out _));
        Assert.Equal(second.Length - 5, buffer.Length);
    }

    [Fact]
    public void TryReadMessage_IgnoresExtraHeaders()
    {
        var json = """{"seq":1,"type":"request","command":"x"}""";
        var bytes = Encoding.UTF8.GetBytes($"Content-Type: application/json\r\nContent-Length: {json.Length}\r\n\r\n{json}");
        var buffer = new ReadOnlySequence<byte>(bytes);
        Assert.True(DapConnection.TryReadMessage(ref buffer, out var body));
        Assert.Equal("x", DapRequest.Parse(body)!.Command);
    }

    [Fact]
    public void ParsesReverseRequestResponses()
    {
        var response = DapResponse.TryParse(Encoding.UTF8.GetBytes("""{"seq":9,"type":"response","request_seq":4,"success":false,"command":"runInTerminal","message":"no terminal"}"""));
        Assert.Equal(new DapResponse(4, false, "no terminal"), response);
        Assert.Null(DapResponse.TryParse(Encoding.UTF8.GetBytes("""{"seq":1,"type":"request","command":"threads"}""")));
    }

    [Fact]
    public void Parse_ReturnsNullForNonRequests() =>
        Assert.Null(DapRequest.Parse(Encoding.UTF8.GetBytes("""{"seq":3,"type":"response","command":"runInTerminal"}""")));

    [Fact]
    public void Arguments_KeepDefaultsForOmittedFields()
    {
        // Regression: init-only properties made the source generator reset defaults to false.
        var request = DapRequest.Parse(Encoding.UTF8.GetBytes("""{"seq":1,"type":"request","command":"launch","arguments":{"program":"a.dll"}}"""))!;
        var arguments = request.GetArguments(DapJsonContext.Default.LaunchArguments);
        Assert.Equal("a.dll", arguments.Program);
        Assert.True(arguments.JustMyCode);
        Assert.True(arguments.EvaluateProperties);
        Assert.True(arguments.EnableStepFiltering);
        Assert.False(arguments.StopAtEntry);
    }

    [Fact]
    public void Attach_AcceptsProcessIdAsString()
    {
        var request = DapRequest.Parse(Encoding.UTF8.GetBytes("""{"seq":1,"type":"request","command":"attach","arguments":{"processId":"1234"}}"""))!;
        Assert.Equal(1234, request.GetArguments(DapJsonContext.Default.AttachArguments).ProcessId);
    }

    [Fact]
    public void MissingArguments_RaiseDapException()
    {
        var request = DapRequest.Parse(Encoding.UTF8.GetBytes("""{"seq":1,"type":"request","command":"launch"}"""))!;
        Assert.Throws<DapException>(() => request.GetArguments(DapJsonContext.Default.LaunchArguments));
    }

    [Fact]
    public async Task Writer_FramesResponsesAndEvents()
    {
        using var output = new MemoryStream();
        using (var connection = new DapConnection(Stream.Null, output))
        {
            var request = DapRequest.Parse(Encoding.UTF8.GetBytes("""{"seq":7,"type":"request","command":"threads"}"""))!;
            connection.SendResponse(request, new ThreadsResponseBody([new DapThread(1, "Main <Thread>")]), DapJsonContext.Default.ThreadsResponseBody);
            connection.SendEvent("initialized");
        }

        var text = Encoding.UTF8.GetString(output.ToArray());
        var bodies = text.Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, bodies.Length);

        var first = bodies[0];
        var separator = first.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var length = int.Parse(first[..separator], System.Globalization.CultureInfo.InvariantCulture);
        var json = first[(separator + 4)..];
        Assert.Equal(length, Encoding.UTF8.GetByteCount(json));
        Assert.Contains("Main <Thread>", json, StringComparison.Ordinal); // relaxed escaping

        using var document = JsonDocument.Parse(json);
        Assert.Equal(7, document.RootElement.GetProperty("request_seq").GetInt32());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        await Task.CompletedTask;
    }
}
