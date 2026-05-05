using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Transport.Http;
public sealed record SurrealConfig(
    Uri Url,
    string Namespace,
    string Database,
    string User,
    string Password,
    TimeSpan Timeout); 

public sealed class SurrealHttpClient : ISurrealTransport, IDisposable
{
    private readonly SurrealConfig config;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim signInGate = new(1, 1);
    private string? bearerToken;
    private bool hasAuthenticatedSession;

    public SurrealHttpClient(SurrealConfig config, HttpClient httpClient)
    {
        this.config = config;
        this.httpClient = httpClient;

        // BaseAddress + Timeout are honoured if the caller pre-configured them, but
        // we fill them in from config so a bare `new HttpClient()` Just Works.
        httpClient.BaseAddress ??= EnsureTrailingSlash(config.Url);
        if (httpClient.Timeout == TimeSpan.FromSeconds(100)) // .NET default — assume unset
        {
            httpClient.Timeout = config.Timeout;
        }
    }

    private static Uri EnsureTrailingSlash(Uri url)
        => url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");

    public void Dispose() => signInGate.Dispose();

    public ValueTask DisposeAsync()
    {
        signInGate.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        using var response = await SendRpcQueryAsync(sql, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(payload);
        var normalized = ExtractRpcResult(doc.RootElement);
        ValidateSqlStatuses(normalized);
        return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(normalized));
    }

    /// <summary>
    /// POST a SurrealDB JSON-RPC <c>query</c> call to <c>/rpc</c>. The SQL travels
    /// unmodified in <c>params[0]</c>; the binding dictionary travels in
    /// <c>params[1]</c> and SurrealDB binds it server-side — no <c>LET</c> prefix
    /// stitched into the query text, no parameter substitution at the client. Record-id
    /// values use the canonical <c>{ "tb", "id" }</c> Thing form so SurrealDB types them
    /// correctly when comparing against schema-typed columns.
    /// </summary>
    private async Task<HttpResponseMessage> SendRpcQueryAsync(string sql, CancellationToken ct)
    {
        await EnsureSignedInAsync(force: false, ct);

        var body = BuildRpcQueryBody(sql);

        var response = await SendRpcRequestAsync(body, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await EnsureSignedInAsync(force: true, ct);
            response = await SendRpcRequestAsync(body, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new SurrealException("SurrealDB unauthorized after re-auth retry.");
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new SurrealException($"SurrealDB /rpc failed: {(int)response.StatusCode} {errBody}");
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendRpcRequestAsync(string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "rpc")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        ApplySqlHeaders(request);
        ApplyAuthHeader(request);

        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex)
        {
            throw new SurrealException($"SurrealDB request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            var inner = ex.InnerException?.Message;
            var detail = string.IsNullOrWhiteSpace(inner) ? ex.Message : $"{ex.Message} | inner: {inner}";
            throw new SurrealException($"HTTP request failed: {detail}");
        }
    }

    /// <summary>
    /// Render the JSON-RPC envelope for SurrealDB's <c>query</c> method. Bindings are
    /// inlined into the SQL via <see cref="SurrealFormatter"/> rather than passed as
    /// JSON-RPC vars — SurrealDB's JSON binder treats record-shaped objects (and strings
    /// that happen to look like record ids) as generic Object/Strand values rather than
    /// Things, so a query like <c>WHERE id = $p0</c> bound with a record never matches.
    /// SurrealQL literal syntax (<c>table:value</c>) is parsed at the query level and
    /// preserves type. <c>params[1]</c> stays as an empty object because SurrealDB's
    /// <c>query</c> method signature requires the slot.
    /// </summary>
    private static string BuildRpcQueryBody(string sql)
    {
        var payload = new RpcRequest("disruptor", "query", [sql, EmptyVars]);
        return JsonSerializer.Serialize(payload, RpcSerializerOptions);
    }

    private static readonly Dictionary<string, object?> EmptyVars = new(StringComparer.Ordinal);

    /// <summary>JSON-RPC 2.0 envelope shape SurrealDB's <c>/rpc</c> endpoint accepts.</summary>
    private readonly record struct RpcRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object?[] Params);

    private static readonly JsonSerializerOptions RpcSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Pull the <c>result</c> field out of SurrealDB's RPC response envelope:
    /// <c>{ "id": "...", "result": [statements...] }</c> on success, or
    /// <c>{ "id": "...", "error": { ... } }</c> on failure.
    /// </summary>
    private static JsonElement ExtractRpcResult(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errorElem))
        {
            var errMessage = errorElem.ValueKind == JsonValueKind.Object && errorElem.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : errorElem.GetRawText();
            throw new SurrealException($"SurrealDB RPC error: {errMessage}");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var resultElem))
        {
            throw new SurrealProtocolException(
                $"Expected a JSON-RPC envelope with a 'result' or 'error' field; got {root.ValueKind}.");
        }

        return resultElem.Clone();
    }

    private async Task EnsureSignedInAsync(bool force, CancellationToken ct)
    {
        if (!force && hasAuthenticatedSession)
        {
            return;
        }

        await signInGate.WaitAsync(ct);
        try
        {
            if (!force && hasAuthenticatedSession)
            {
                return;
            }

            hasAuthenticatedSession = false;
            bearerToken = null;

            using var content = JsonContent.Create(new { user = config.User, pass = config.Password });
            using var request = new HttpRequestMessage(HttpMethod.Post, "signin") { Content = content };
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new SurrealException($"SurrealDB signin failed: {(int)response.StatusCode} {body}");
            }

            bearerToken = TryParseToken(body);
            hasAuthenticatedSession = true;
        }
        finally { signInGate.Release(); }
    }

    private void ApplySqlHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("surreal-ns", config.Namespace);
        request.Headers.TryAddWithoutValidation("surreal-db", config.Database);
        if (!request.Headers.Accept.Any(h => string.Equals(h.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    private void ApplyAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        else
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.User}:{config.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    private static string? TryParseToken(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            return trimmed[1..^1];
        }

        try
        {
            using var json = JsonDocument.Parse(trimmed);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString();
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String)
            {
                return tok.GetString();
            }
        }
        catch (JsonException) { }
        return trimmed;
    }

    private static void ValidateSqlStatuses(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var statement in response.EnumerateArray())
        {
            if (statement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!statement.TryGetProperty("status", out var statusNode))
            {
                continue;
            }

            var status = statusNode.GetString();
            if (!string.Equals(status, "ERR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var details = statement.TryGetProperty("result", out var result)
                ? result.GetRawText()
                : statement.GetRawText();

            throw new SurrealException($"SurrealDB statement failed: {details}");
        }
    }
}