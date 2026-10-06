using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Digger.Protocol;

/// <summary>Reads an <see cref="int"/> that clients may send either as a number or a string.</summary>
public sealed class FlexibleInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String when int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            JsonTokenType.String => throw new JsonException($"'{reader.GetString()}' is not a valid integer."),
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for an integer."),
        };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
