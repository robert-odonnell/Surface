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

    //public static JsonElement ResultAt(JsonElement response, int statementIndex)
    //{
    //    if (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() <= statementIndex)
    //    {
    //        return default;
    //    }
    //    var row = response[statementIndex];
    //    if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("result", out var result))
    //    {
    //        return result;
    //    }
    //    return row;
    //}

    //public static List<T> DecodeList<T>(JsonElement response, int statementIndex = 0)
    //{
    //    var result = ResultAt(response, statementIndex);
    //    if (result.ValueKind != JsonValueKind.Array)
    //    {
    //        return [];
    //    }

    //    var list = new List<T>(result.GetArrayLength());
    //    foreach (var item in result.EnumerateArray())
    //    {
    //        var v = item.Deserialize<T>(SerializerOptions);
    //        if (v is not null)
    //        {
    //            list.Add(v);
    //        }
    //    }
    //    return list;
    //}

    //public static T? DecodeFirst<T>(JsonElement response, int statementIndex = 0) where T : class
    //{
    //    var result = ResultAt(response, statementIndex);
    //    if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
    //    {
    //        return null;
    //    }

    //    return result[0].Deserialize<T>(SerializerOptions);
    //}

    //public static int Count(JsonElement response, int statementIndex = 0)
    //{
    //    var result = ResultAt(response, statementIndex);
    //    return result.ValueKind == JsonValueKind.Array ? result.GetArrayLength() : 0;
    //}
}

