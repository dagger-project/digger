using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Digger.Protocol;

/// <summary>
/// Content-Length framed DAP transport. Reading is async over a <see cref="PipeReader"/>;
/// writing is synchronous and serialized, so events can be sent from any thread.
/// </summary>
public sealed class DapConnection : IDisposable
{
    private const int MaxMessageSize = 64 * 1024 * 1024;
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Lock _writeLock = new();
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly Utf8JsonWriter _json;
    private readonly Action<string>? _trace;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> _pendingRequests = new();
    private int _seq;

    public DapConnection(Stream input, Stream output, Action<string>? trace = null)
    {
        _input = input;
        _output = output;
        _trace = trace;
        // Relaxed escaping: DAP is not embedded in HTML, so quotes and angle brackets
        // (common in type names like List<int>) can stay readable.
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions
        {
            SkipValidation = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>Yields requests until the input stream closes.</summary>
    public async IAsyncEnumerable<DapRequest> ReadRequestsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reader = PipeReader.Create(_input, new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;
                while (TryReadMessage(ref buffer, out var body))
                {
                    _trace?.Invoke("<- " + System.Text.Encoding.UTF8.GetString(body.Span));
                    if (DapResponse.TryParse(body) is { } response)
                    {
                        if (_pendingRequests.TryRemove(response.RequestSeq, out var pending))
                        {
                            _ = pending.TrySetResult(response);
                        }
                    }
                    else if (DapRequest.Parse(body) is { } request)
                    {
                        yield return request;
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted || result.IsCanceled)
                {
                    yield break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
            foreach (var pending in _pendingRequests.Values)
            {
                _ = pending.TrySetResult(new DapResponse(0, Success: false, "The client disconnected."));
            }
        }
    }

    internal static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> body)
    {
        body = default;
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> headers, HeaderTerminator, advancePastDelimiter: true))
        {
            return false;
        }

        var length = ParseContentLength(headers);
        if (reader.Remaining < length)
        {
            return false;
        }

        var bytes = new byte[length];
        reader.UnreadSequence.Slice(0, length).CopyTo(bytes);
        reader.Advance(length);
        buffer = buffer.Slice(reader.Position);
        body = bytes;
        return true;
    }

    private static int ParseContentLength(ReadOnlySequence<byte> headers)
    {
        Span<byte> scratch = stackalloc byte[256];
        if (headers.Length > scratch.Length)
        {
            throw new DapException("DAP header block is too large.");
        }

        headers.CopyTo(scratch);
        ReadOnlySpan<byte> remaining = scratch[..(int)headers.Length];
        var prefix = "Content-Length:"u8;
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf("\r\n"u8);
            var line = newline < 0 ? remaining : remaining[..newline];
            remaining = newline < 0 ? default : remaining[(newline + 2)..];
            if (line.StartsWith(prefix)
                && Utf8Parser.TryParse(line[prefix.Length..].Trim((byte)' '), out int length, out _)
                && length is >= 0 and <= MaxMessageSize)
            {
                return length;
            }
        }

        throw new DapException("DAP message is missing a valid Content-Length header.");
    }

    // ---- Writing ----------------------------------------------------------------------

    public void SendResponse(DapRequest request) =>
        Write(request.Command, request.Seq, success: true, message: null, static _ => { });

    public void SendResponse<T>(DapRequest request, T body, JsonTypeInfo<T> typeInfo) =>
        Write(request.Command, request.Seq, success: true, message: null, writer =>
        {
            writer.WritePropertyName("body"u8);
            JsonSerializer.Serialize(writer, body, typeInfo);
        });

    public void SendError(DapRequest request, string message) =>
        Write(request.Command, request.Seq, success: false, message, writer =>
        {
            writer.WritePropertyName("body"u8);
            writer.WriteStartObject();
            writer.WritePropertyName("error"u8);
            writer.WriteStartObject();
            writer.WriteNumber("id"u8, 1);
            writer.WriteString("format"u8, message);
            writer.WriteBoolean("showUser"u8, true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    public void SendEvent(string name) => WriteEvent(name, static _ => { });

    public void SendEvent<T>(string name, T body, JsonTypeInfo<T> typeInfo) =>
        WriteEvent(name, writer =>
        {
            writer.WritePropertyName("body"u8);
            JsonSerializer.Serialize(writer, body, typeInfo);
        });

    /// <summary>Sends a reverse request (e.g. <c>runInTerminal</c>); the task completes with the client's response.</summary>
    public Task<DapResponse> SendRequestAsync<T>(string command, T arguments, JsonTypeInfo<T> typeInfo)
    {
        var completion = new TaskCompletionSource<DapResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writeLock)
        {
            var seq = ++_seq;
            _pendingRequests[seq] = completion;
            _json.WriteStartObject();
            _json.WriteNumber("seq"u8, seq);
            _json.WriteString("type"u8, "request"u8);
            _json.WriteString("command"u8, command);
            _json.WritePropertyName("arguments"u8);
            JsonSerializer.Serialize(_json, arguments, typeInfo);
            _json.WriteEndObject();
            Flush();
        }

        return completion.Task;
    }

    private void Write(string command, int requestSeq, bool success, string? message, Action<Utf8JsonWriter> writeBody)
    {
        lock (_writeLock)
        {
            _json.WriteStartObject();
            _json.WriteNumber("seq"u8, ++_seq);
            _json.WriteString("type"u8, "response"u8);
            _json.WriteNumber("request_seq"u8, requestSeq);
            _json.WriteBoolean("success"u8, success);
            _json.WriteString("command"u8, command);
            if (message is not null)
            {
                _json.WriteString("message"u8, message);
            }

            writeBody(_json);
            _json.WriteEndObject();
            Flush();
        }
    }

    private void WriteEvent(string name, Action<Utf8JsonWriter> writeBody)
    {
        lock (_writeLock)
        {
            _json.WriteStartObject();
            _json.WriteNumber("seq"u8, ++_seq);
            _json.WriteString("type"u8, "event"u8);
            _json.WriteString("event"u8, name);
            writeBody(_json);
            _json.WriteEndObject();
            Flush();
        }
    }

    private void Flush()
    {
        _json.Flush();
        var body = _buffer.WrittenSpan;
        Span<byte> header = stackalloc byte[64];
        var prefix = "Content-Length: "u8;
        prefix.CopyTo(header);
        _ = Utf8Formatter.TryFormat(body.Length, header[prefix.Length..], out var digits);
        var headerLength = prefix.Length + digits;
        "\r\n\r\n"u8.CopyTo(header[headerLength..]);
        headerLength += 4;

        try
        {
            _output.Write(header[..headerLength]);
            _output.Write(body);
            _output.Flush();
            _trace?.Invoke("-> " + System.Text.Encoding.UTF8.GetString(body));
        }
        catch (IOException)
        {
            // The client went away; the read loop will observe EOF and shut down.
        }
        finally
        {
            _buffer.ResetWrittenCount();
            _json.Reset();
        }
    }

    public void Dispose()
    {
        _json.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}
