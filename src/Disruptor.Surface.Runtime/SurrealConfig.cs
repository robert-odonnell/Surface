namespace Disruptor.Surface.Runtime;

public sealed record SurrealConfig(
    Uri Url,
    string Namespace,
    string Database,
    string User,
    string Password,
    TimeSpan Timeout)
{
    public static SurrealConfig Default() => new(
        new Uri("http://127.0.0.1:8000"),
        Namespace: "main",
        Database: "main",
        User: "root",
        Password: "root",
        Timeout: TimeSpan.FromSeconds(180));

    public static SurrealConfig FromConnectionString(string? raw)
    {
        var cfg = Default();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return cfg;
        }

        Uri? url = null;
        var ns = cfg.Namespace;
        var db = cfg.Database;
        var user = cfg.User;
        var pass = cfg.Password;
        var timeout = cfg.Timeout;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();

            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Url", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var parsed))
                {
                    url = parsed;
                }
            }
            else if (key.Equals("Namespace", StringComparison.OrdinalIgnoreCase) || key.Equals("Ns", StringComparison.OrdinalIgnoreCase))
            {
                ns = value;
            }
            else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("Db", StringComparison.OrdinalIgnoreCase))
            {
                db = value;
            }
            else if (key.Equals("Username", StringComparison.OrdinalIgnoreCase) || key.Equals("User", StringComparison.OrdinalIgnoreCase))
            {
                user = value;
            }
            else if (key.Equals("Password", StringComparison.OrdinalIgnoreCase) || key.Equals("Pass", StringComparison.OrdinalIgnoreCase))
            {
                pass = value;
            }
            else if (key.Equals("TimeoutSeconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var s) && s > 0)
            {
                timeout = TimeSpan.FromSeconds(s);
            }
        }

        return new SurrealConfig(url ?? cfg.Url, ns, db, user, pass, timeout);
    }
}
