using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surface.Runtime;

[JsonConverter(typeof(SemanticEdgeJsonConverter))]
public readonly record struct SemanticEdge(string Kind, RecordId From, RecordId To);

public sealed class SemanticEdgeJsonConverter : JsonConverter<SemanticEdge>
{
    public override SemanticEdge Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var from = RecordId.Parse(root.GetProperty("from").GetString()!);
        var kind = root.GetProperty("kind").GetString()!;
        var to = RecordId.Parse(root.GetProperty("to").GetString()!);

        return new SemanticEdge(kind, from, to);
    }

    public override void Write(Utf8JsonWriter writer, SemanticEdge value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("from", value.From.ToString());
        writer.WriteString("kind", value.Kind);
        writer.WriteString("to", value.To.ToString());
        writer.WriteEndObject();
    }
}

public sealed class SurrealProtocolException(string message)
    : Exception(message);

public readonly record struct SurrealStatementResponse(
    string? Status,
    string? Time,
    JsonElement Result,
    JsonElement Raw);

public static class SurrealResultReader
{
    public static IReadOnlyList<SurrealStatementResponse> ReadStatements(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new SurrealProtocolException($"Expected top-level JSON array from Surreal HTTP response, but got {root.ValueKind}.");
        }

        var list = new List<SurrealStatementResponse>(root.GetArrayLength());

        var index = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new SurrealProtocolException($"Expected statement envelope object at index {index}, but got {item.ValueKind}.");
            }

            var status = TryGetString(item, "status");
            var time = TryGetString(item, "time");
            var result = item.TryGetProperty("result", out var resultProp)
                ? resultProp
                : default;

            list.Add(new SurrealStatementResponse(status, time, result, item));
            index++;
        }

        return list;
    }


    public static IReadOnlyList<T> ReadMany<T>(
        JsonElement root,
        JsonSerializerOptions json,
        int statementIndex = 0)
    {
        var statement = GetSuccessfulStatement(root, statementIndex);

        return statement.Result.ValueKind switch
        {
            JsonValueKind.Array => DeserializeArray<T>(statement.Result, json),
            JsonValueKind.Object => [DeserializeRequired<T>(statement.Result, json)],
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            _ => [DeserializeRequired<T>(statement.Result, json)]
        };
    }

    public static T? ReadSingleOrDefault<T>(
        JsonElement root,
        JsonSerializerOptions json,
        int statementIndex = 0)
    {
        var statement = GetSuccessfulStatement(root, statementIndex);
        return ReadSingleOrDefault<T>(statement.Result, json);
    }

    public static int Count(JsonElement root, int statementIndex = 0)
    {
        var statement = GetSuccessfulStatement(root, statementIndex);
        return statement.Result.ValueKind == JsonValueKind.Array
            ? statement.Result.GetArrayLength()
            : 0;
    }

    public static SurrealStatementResponse GetSuccessfulStatement(
        JsonElement root,
        int statementIndex = 0)
    {
        var statements = ReadStatements(root);

        if ((uint)statementIndex >= (uint)statements.Count)
        {
            throw new SurrealProtocolException($"Requested statement #{statementIndex}, but response only contains {statements.Count} statement(s).");
        }

        var statement = statements[statementIndex];

        if (!string.Equals(statement.Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new SurrealQueryException(
                statementIndex,
                statement.Status,
                statement.Time,
                ExtractErrorText(statement.Raw));
        }

        return statement;
    }

    private static T? ReadSingleOrDefault<T>(JsonElement result, JsonSerializerOptions json)
    {
        return result.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => default,
            JsonValueKind.Array => ReadSingleFromArrayOrDefault<T>(result, json),
            //JsonValueKind.Object => DeserializeRequired<T>(result, json),
            _ => DeserializeRequired<T>(result, json)
        };
    }

    private static T? ReadSingleFromArrayOrDefault<T>(JsonElement array, JsonSerializerOptions json)
    {
        using var enumerator = array.EnumerateArray();

        if (!enumerator.MoveNext())
        {
            return default;
        }

        var first = enumerator.Current;
        var value = DeserializeRequired<T>(first, json);

        return enumerator.MoveNext()
            ? throw new SurrealProtocolException("Expected at most one result row, but multiple rows were returned.")
            : value;
    }

    private static IReadOnlyList<T> DeserializeArray<T>(JsonElement array, JsonSerializerOptions json)
    {
        var list = new List<T>();

        foreach (var item in array.EnumerateArray())
        {
            list.Add(DeserializeRequired<T>(item, json));
        }

        return list;
    }

    private static T DeserializeRequired<T>(JsonElement element, JsonSerializerOptions json)
    {
        var value = element.Deserialize<T>(json);
        return value ?? throw new SurrealProtocolException($"Failed to deserialize Surreal result element to {typeof(T).Name}.");
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => prop.GetRawText()
        };
    }

    private static string? ExtractErrorText(JsonElement statement)
    {
        if (statement.ValueKind != JsonValueKind.Object)
        {
            return statement.GetRawText();
        }

        foreach (var name in new[] { "detail", "error", "message" })
        {
            if (statement.TryGetProperty(name, out var prop))
            {
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
            }
        }

        if (statement.TryGetProperty("result", out var result))
        {
            return result.ValueKind == JsonValueKind.String ? result.GetString() : result.GetRawText();
        }

        return statement.GetRawText();
    }
}

public sealed class SurrealQueryException(int statementIndex, string? status, string? time, string? rawError)
    : Exception(BuildMessage(statementIndex, status, time, rawError))
{
    public int StatementIndex { get; } = statementIndex;
    public string? Status { get; } = status;
    public string? Time { get; } = time;
    public string? RawError { get; } = rawError;

    private static string BuildMessage(int statementIndex, string? status, string? time, string? rawError)
    {
        var msg = $"Surreal query statement #{statementIndex} failed";
        if (!string.IsNullOrWhiteSpace(status))
        {
            msg += $" with status '{status}'";
        }

        if (!string.IsNullOrWhiteSpace(time))
        {
            msg += $" after {time}";
        }

        if (!string.IsNullOrWhiteSpace(rawError))
        {
            msg += $": {rawError}";
        }

        return msg;
    }
}


public readonly struct SurrealResultSet(JsonElement root)
{
    public JsonElement Root => root;

    /// <summary>Decodes the statement result as a list. Returns empty for null/undefined results.</summary>
    public List<T> DecodeList<T>(int statementIndex = 0)
        => [.. SurrealResultReader.ReadMany<T>(root, SurrealJson.SerializerOptions, statementIndex)];

    /// <summary>Decodes the first row of the statement result, or default when empty.</summary>
    public T? DecodeFirst<T>(int statementIndex = 0)
        => SurrealResultReader.ReadSingleOrDefault<T>(root, SurrealJson.SerializerOptions, statementIndex);

    /// <summary>Row count of the statement result (0 for non-arrays).</summary>
    public int Count(int statementIndex = 0)
        => SurrealResultReader.Count(root, statementIndex);

    /// <summary>Raw result element for the statement (envelope-aware: unwraps <c>.result</c>).</summary>
    public JsonElement ResultAt(int statementIndex = 0)
        => SurrealResultReader.GetSuccessfulStatement(root, statementIndex).Result;
}
