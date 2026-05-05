using Cysharp.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Disruptor.Surface.Runtime;

public static class SurrealJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false),
            new UlidJsonConverter()
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

