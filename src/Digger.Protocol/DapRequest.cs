using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Digger.Protocol;

/// <summary>
/// An incoming request. The JSON body is kept as raw UTF-8 and <c>arguments</c> is
/// deserialized lazily into the typed shape the handler asks for.
/// </summary>
public sealed class DapRequest
{
    private readonly ReadOnlyMemory<byte> _arguments;

    public DapRequest(int seq, string command, ReadOnlyMemory<byte> arguments)
    {
        Seq = seq;
        Command = command;
        _arguments = arguments;
    }

    public int Seq { get; }

    public string Command { get; }

    public bool HasArguments => !_arguments.IsEmpty;

    /// <summary>Deserializes <c>arguments</c>; throws <see cref="DapException"/> if missing or malformed.</summary>
    public T GetArguments<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (_arguments.IsEmpty)
        {
            throw new DapException($"'{Command}' requires arguments.");
        }

        try
        {
            return JsonSerializer.Deserialize(_arguments.Span, typeInfo)
                ?? throw new DapException($"'{Command}' arguments are null.");
        }
        catch (JsonException ex)
        {
            throw new DapException($"Invalid '{Command}' arguments: {ex.Message}", ex);
        }
    }

    /// <summary>Like <see cref="GetArguments{T}"/> but falls back to <paramref name="fallback"/> when absent.</summary>
    public T GetArgumentsOrDefault<T>(JsonTypeInfo<T> typeInfo, T fallback)
        where T : class => _arguments.IsEmpty ? fallback : GetArguments(typeInfo);

    /// <summary>
    /// Parses a framed message body. Returns null for messages that are not requests
    /// (clients may send responses to reverse requests, which we ignore).
    /// </summary>
    public static DapRequest? Parse(ReadOnlyMemory<byte> body)
    {
        var reader = new Utf8JsonReader(body.Span, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new DapException("A DAP message must be a JSON object.");
        }

        var seq = 0;
        string? type = null;
        string? command = null;
        ReadOnlyMemory<byte> arguments = default;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("seq"u8))
            {
                _ = reader.Read();
                seq = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                _ = reader.Read();
                type = reader.GetString();
            }
            else if (reader.ValueTextEquals("command"u8))
            {
                _ = reader.Read();
                command = reader.GetString();
            }
            else if (reader.ValueTextEquals("arguments"u8))
            {
                _ = reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                var end = (int)reader.BytesConsumed;
                if (reader.TokenType is not JsonTokenType.Null)
                {
                    arguments = body[start..end];
                }
            }
            else
            {
                _ = reader.Read();
                reader.Skip();
            }
        }

        if (!string.Equals(type, "request", StringComparison.Ordinal))
        {
            return null;
        }

        return command is null
            ? throw new DapException("Request is missing 'command'.")
            : new DapRequest(seq, command, arguments);
    }
}

/// <summary>An error reported back to the client as a failed response.</summary>
public sealed class DapException : Exception
{
    public DapException(string message)
        : base(message)
    {
    }

    public DapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A client's response to a reverse request.</summary>
public sealed record DapResponse(int RequestSeq, bool Success, string? Message)
{
    /// <summary>Parses a response message; null for anything else.</summary>
    public static DapResponse? TryParse(ReadOnlyMemory<byte> body)
    {
        var reader = new Utf8JsonReader(body.Span);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        string? type = null;
        string? message = null;
        var requestSeq = 0;
        var success = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                _ = reader.Read();
                type = reader.GetString();
            }
            else if (reader.ValueTextEquals("request_seq"u8))
            {
                _ = reader.Read();
                requestSeq = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("success"u8))
            {
                _ = reader.Read();
                success = reader.TokenType == JsonTokenType.True;
            }
            else if (reader.ValueTextEquals("message"u8))
            {
                _ = reader.Read();
                message = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                _ = reader.Read();
                reader.Skip();
            }
        }

        return string.Equals(type, "response", StringComparison.Ordinal) ? new DapResponse(requestSeq, success, message) : null;
    }
}
