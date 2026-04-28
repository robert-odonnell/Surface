using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Surface.Runtime;

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

    public SurrealConfig Config => config;

    public void Dispose() => signInGate.Dispose();

    public ValueTask DisposeAsync()
    {
        signInGate.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<SurrealResultSet> SqlAsync(string surrealQl, IReadOnlyDictionary<string, object?>? vars = null, CancellationToken cancellationToken = default)
    {
        var normalized = await SendAndNormalizeAsync(surrealQl, vars, cancellationToken);
        return new SurrealResultSet(normalized);
    }

    public Task<SurrealResultSet> SqlAsync(string surrealQl, object bindings, CancellationToken cancellationToken = default)
        => SqlAsync(surrealQl, BindingsFromObject(bindings), cancellationToken);

    public async Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default)
    {
        var normalized = await SendAndNormalizeAsync(sql, BindingsFromVars(vars), ct);
        return WrapElementAsDocument(normalized);
    }


    private async Task<JsonElement> SendAndNormalizeAsync(string surrealQl, IReadOnlyDictionary<string, object?>? vars, CancellationToken cancellationToken)
    {
        var letCount = vars?.Count ?? 0;
        var query = BuildLetPrefix(vars) + surrealQl;

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await SendSqlAsync(query, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(payload);
                var normalized = NormalizeStatementResults(doc.RootElement, letCount);
                ValidateSqlStatuses(normalized);
                return normalized;
            }
            catch (SurrealException ex) when (attempt < maxAttempts && ex.Retryable)
            {
                //await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }

        throw new SurrealException("SurrealDB query failed after retries.", retryable: false);
    }

    private static IReadOnlyDictionary<string, object?>? BindingsFromVars(object? vars) => vars switch
    {
        null => null,
        IReadOnlyDictionary<string, object?> d => d,
        _ => BindingsFromObject(vars)
    };

    private static JsonDocument WrapElementAsDocument(JsonElement element)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(element);
        return JsonDocument.Parse(bytes);
    }

    private static IReadOnlyDictionary<string, object?> BindingsFromObject(object bindings)
    {
        if (bindings is IReadOnlyDictionary<string, object?> dict)
        {
            return dict;
        }

        var props = bindings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var result = new Dictionary<string, object?>(props.Length, StringComparer.Ordinal);
        foreach (var p in props)
        {
            result[p.Name] = p.GetValue(bindings);
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendSqlAsync(string query, CancellationToken ct)
    {
        await EnsureSignedInAsync(force: false, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "sql")
        {
            Content = new StringContent(query, Encoding.UTF8, "text/plain")
        };
        ApplySqlHeaders(request);
        ApplyAuthHeader(request);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex)
        {
            throw new SurrealException($"SurrealDB request timed out: {ex.Message}", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            var inner = ex.InnerException?.Message;
            var detail = string.IsNullOrWhiteSpace(inner) ? ex.Message : $"{ex.Message} | inner: {inner}";
            throw new SurrealException($"HTTP request failed: {detail}", retryable: true);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await EnsureSignedInAsync(force: true, ct);

            using var retry = new HttpRequestMessage(HttpMethod.Post, "sql")
            {
                Content = new StringContent(query, Encoding.UTF8, "text/plain")
            };
            ApplySqlHeaders(retry);
            ApplyAuthHeader(retry);
            response = await httpClient.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new SurrealException("SurrealDB unauthorized after re-auth retry.", retryable: false);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new SurrealException($"SurrealDB /sql failed: {(int)response.StatusCode} {body}", retryable: false);
        }

        return response;
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
                throw new SurrealException($"SurrealDB signin failed: {(int)response.StatusCode} {body}", retryable: false);
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

    public static string BuildLetPrefix(IReadOnlyDictionary<string, object?>? vars)
    {
        if (vars is null || vars.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var kv in vars.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append("LET $").Append(kv.Key).Append(" = ")
              .Append(RenderValue(kv.Value)).Append(";\n");
        }
        return sb.ToString();
    }

    private static string RenderValue(object? value)
    {
        if (value is null)
        {
            return "NONE";
        }

        return value switch
        {
            bool b => b ? "true" : "false",
            int or long or short or byte or uint or ulong or ushort or sbyte => value.ToString()!,
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string s => SurrealFormatter.StringLiteral(s),
            RecordId rid => SurrealFormatter.RecordId(rid),
            Guid g => SurrealFormatter.StringLiteral(g.ToString()),
            Ulid u => SurrealFormatter.StringLiteral(u.ToString()),
            DateTime dt => SurrealFormatter.StringLiteral(dt.ToUniversalTime().ToString("O")),
            DateTimeOffset dto => SurrealFormatter.StringLiteral(dto.ToString("O")),
            Enum e => SurrealFormatter.StringLiteral(e.ToString()),
            IEntity v => SurrealFormatter.RecordId(v.Id),
            System.Collections.IEnumerable e => $"[{string.Join(", ", e.Cast<object?>().Select(RenderValue))}]",
            _ => JsonSerializer.Serialize(value, SurrealJson.SerializerOptions)
        };
    }

    private static JsonElement NormalizeStatementResults(JsonElement root, int letCount)
    {
        var statements = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            statements.AddRange(root.EnumerateArray().Select(i => i.Clone()));
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            statements.Add(root.Clone());
        }
        else
        {
            return root.Clone();
        }

        if (letCount > 0 && statements.Count > letCount)
        {
            statements = statements.Skip(letCount).ToList();
        }
        else if (letCount >= statements.Count)
        {
            statements.Clear();
        }

        using var filtered = JsonDocument.Parse(JsonSerializer.Serialize(statements));
        return filtered.RootElement.Clone();
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

            var retryable = details.Contains("Transaction conflict", StringComparison.OrdinalIgnoreCase)
                         || details.Contains("Resource busy", StringComparison.OrdinalIgnoreCase)
                         || details.Contains("can be retried", StringComparison.OrdinalIgnoreCase);

            throw new SurrealException($"SurrealDB statement failed: {details}", retryable);
        }
    }
}

public sealed class SurrealException(string message, bool retryable) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
