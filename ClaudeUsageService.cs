using System.Net;
using System.Text.Json;
using System.Security.Cryptography;

namespace Usage;

sealed class ClaudeUsageService : IDisposable
{
    readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    DateTimeOffset retryAfter;
    (string SessionKey, string? Organization)? cachedSession;
    string? sessionIdentity;
    sealed record SavedSession(string Identity, string SessionKey, string? Organization);
    static string ConnectionPath => Path.Combine(AppContext.BaseDirectory, "claude-session.bin");
    static string LogPath => Path.Combine(AppContext.BaseDirectory, "claude.log");

    // Diagnostics only: stage, status and short response snippets. Never session values.
    static void Log(string message)
    {
        try
        {
            var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath).TakeLast(300).ToList() : [];
            lines.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");
            File.WriteAllLines(LogPath, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string? FindProfile()
    {
        var paths = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude") };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            paths.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        return SelectProfile(paths);
    }

    internal static string? SelectProfile(IEnumerable<string> paths) => paths.Where(Directory.Exists)
        .Select(folder => new { Folder = folder, Files = new[] { ClaudeDesktopSession.CookiePath(folder),
            Path.Combine(folder, "config.json"), Path.Combine(folder, "plan-usage-history.json") }
            .Where(path => path != null && File.Exists(path)).ToArray() })
        .Where(profile => profile.Files.Length > 0)
        .OrderByDescending(profile => profile.Files.Max(path => File.GetLastWriteTimeUtc(path!)))
        .Select(profile => profile.Folder).FirstOrDefault();

    public async Task<Snapshot> Read(CancellationToken token)
    {
        try
        {
            var live = await ReadLive(token);
            Log($"live ok: {string.Join(", ", live.Quotas.Select(q => $"{q.Name}={q.Remaining:0}"))}{(live.Error is { } err ? " | " + err : "")}");
            return live;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or CryptographicException or HttpRequestException)
        {
            Log($"live failed ({e.GetType().Name}): {e.Message} | saved connection: {(File.Exists(ConnectionPath) ? "yes" : "no")}, in memory: {(cachedSession != null ? "yes" : "no")}");
            var folder = FindProfile();
            if (folder == null) return new([], e.Message);
            var path = Path.Combine(folder, "plan-usage-history.json");
            if (!File.Exists(path)) return new([], e.Message);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            return ParseCached(document.RootElement, e.Message);
        }
    }

    async Task<Snapshot> ReadLive(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow < retryAfter) return new([], "Claude rate limited requests. Retrying after a short pause.");
        var folder = FindProfile();
        if (folder == null) return new([], "Sign in to Claude Desktop first.");
        var identity = ReadIdentity(folder);
        if (identity == null || identity != sessionIdentity) { cachedSession = null; sessionIdentity = identity; }
        var session = cachedSession ??= await Task.Run(() => ReadConnection(folder, identity), token);
        var organization = session.Organization;
        if (organization == null)
        {
            using var organizations = await Fetch("/api/organizations", session.SessionKey, token);
            var list = organizations.RootElement;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 1 ||
                !list[0].TryGetProperty("uuid", out var id) || !Guid.TryParse(id.GetString(), out var guid))
                return new([], "Select your active organization in Claude Desktop first.");
            organization = guid.ToString();
        }
        using var usage = await Fetch($"/api/organizations/{organization}/usage?skip_spend=1", session.SessionKey, token);
        return Parse(usage.RootElement);
    }

    async Task<JsonDocument> Fetch(string path, string session, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai" + path);
        request.Headers.Add("Cookie", "sessionKey=" + session);
        request.Headers.Add("Accept", "application/json");
        request.Headers.UserAgent.ParseAdd("UsageWidget/1.0");
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(5);
            if (response.Headers.RetryAfter?.Date is { } date && date > retryAfter) retryAfter = date;
            if (response.Headers.RetryAfter?.Delta is { } delta && DateTimeOffset.UtcNow + delta > retryAfter) retryAfter = DateTimeOffset.UtcNow + delta;
            throw new InvalidOperationException("Claude rate limited requests. Retrying after a short pause.");
        }
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = new string(body.Take(160).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            bool cloudflare = response.Headers.Contains("cf-mitigated") || body.Contains("challenge-platform") || body.Contains("Just a moment");
            Log($"GET {path.Split('?')[0]} -> HTTP {(int)response.StatusCode}{(cloudflare ? " (Cloudflare challenge)" : "")}: {snippet}");
            // Only a 401 proves the session is dead. A 403 is often Cloudflare or a transient block;
            // deleting the saved session then forces a cookie read, which fails while Desktop is open.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                cachedSession = null;
                ForgetConnection();
                throw new InvalidOperationException("Claude rejected the saved sign-in (HTTP 401). Exit Claude Desktop briefly, then refresh Usage to reconnect.");
            }
            throw new InvalidOperationException(cloudflare
                ? $"Claude's web protection blocked the usage request (HTTP {(int)response.StatusCode}). Keeping the saved sign-in and retrying."
                : $"Claude usage unavailable (HTTP {(int)response.StatusCode}). Keeping the saved sign-in and retrying.");
        }
        return JsonDocument.Parse(body);
    }

    static string? ReadIdentity(string folder)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
            var account = config.RootElement.ValueKind == JsonValueKind.Object ? String(config.RootElement, "lastKnownAccountUuid") : null;
            return string.IsNullOrWhiteSpace(account) ? null : folder + "|" + account;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static (string SessionKey, string? Organization) ReadConnection(string folder, string? identity)
    {
        // Older Desktop versions may omit the account marker. Read their actual
        // cookies, but never reuse or persist an unscoped saved connection.
        if (identity == null) return ClaudeDesktopSession.Read(folder);
        try
        {
            var clear = ClaudeDesktopSession.Unprotect(File.ReadAllBytes(ConnectionPath));
            try
            {
                var saved = JsonSerializer.Deserialize<SavedSession>(clear);
                if (saved?.Identity == identity && !string.IsNullOrWhiteSpace(saved.SessionKey))
                    return (saved.SessionKey, saved.Organization);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or CryptographicException) { }
        var session = ClaudeDesktopSession.Read(folder);
        Log("read sign-in from Claude Desktop cookies; saving connection");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedSession(identity, session.SessionKey, session.Organization));
        try
        {
            var encrypted = ClaudeDesktopSession.Protect(bytes);
            try
            {
                File.WriteAllBytes(ConnectionPath + ".tmp", encrypted);
                File.Move(ConnectionPath + ".tmp", ConnectionPath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return session;
    }

    static void ForgetConnection()
    {
        try { File.Delete(ConnectionPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static Snapshot Parse(JsonElement root)
    {
        List<Quota> quotas = [];
        if (root.ValueKind != JsonValueKind.Object) return new([], "Claude returned an invalid usage response.");
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? kind = String(item, "kind"), group = String(item, "group");
                bool primary = kind == "weekly_all";
                string? name = primary ? "Weekly - all models" : kind == "session" ? "Session - 5 hours" : null;
                if (name == null && item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in new[] { "model", "surface" })
                        if (scope.TryGetProperty(field, out var product) && product.ValueKind == JsonValueKind.Object &&
                            String(product, "display_name") is { } display)
                        { name = (group == "weekly" ? "Weekly - " : "") + display; break; }
                }
                if (name != null) Add(item, "percent", name, primary);
            }
        }
        // Legacy fields supplement missing entries, without replacing the weekly main indicator.
        Legacy("seven_day", "Weekly - all models", true);
        Legacy("five_hour", "Session - 5 hours", false);
        Legacy("seven_day_fable", "Weekly - Fable", false);
        Legacy("seven_day_opus", "Weekly - Opus", false);
        Legacy("seven_day_sonnet", "Weekly - Sonnet", false);
        return new(quotas, quotas.Any(q => q.IncludeInSummary) ? null : "Claude did not return the weekly limit.", DateTimeOffset.UtcNow);

        void Legacy(string field, string label, bool primary)
        {
            if ((primary && quotas.Any(q => q.IncludeInSummary)) ||
                quotas.Any(q => q.Name == label || (field == "seven_day_fable" && q.Name.Contains("Fable", StringComparison.OrdinalIgnoreCase)))) return;
            if (root.TryGetProperty(field, out var window) && window.ValueKind == JsonValueKind.Object)
                Add(window, "utilization", label, primary);
        }
        void Add(JsonElement window, string key, string name, bool primary)
        {
            if (!window.TryGetProperty(key, out var used) || used.ValueKind != JsonValueKind.Number ||
                !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0 || percent > 100) return;
            DateTimeOffset? reset = window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(r.GetString(), out var time) ? time : null;
            quotas.Add(new(name, 100 - percent, reset, primary));
        }
    }

    static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static Snapshot ParseCached(JsonElement root, string error)
    {
        if (!root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return new([], error);
        JsonElement? latest = null;
        long stamp = 0;
        foreach (var sample in samples.EnumerateArray())
            if (sample.ValueKind == JsonValueKind.Object && sample.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var n) && n >= stamp)
            { stamp = n; latest = sample; }
        if (latest is not { } item || !item.TryGetProperty("u", out var usage) || usage.ValueKind != JsonValueKind.Object) return new([], error);
        List<Quota> quotas = [];
        foreach (var (key, label, primary) in new[]
        {
            ("sd", "Weekly - all models", true), ("fh", "Session - 5 hours", false),
            ("so", "Weekly - Opus", false), ("sn", "Weekly - Sonnet", false)
        })
            if (usage.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var percent) && double.IsFinite(percent) && percent >= 0 && percent <= 100)
                quotas.Add(new(label, 100 - percent, null, primary));
        DateTimeOffset? measured = null;
        try { measured = DateTimeOffset.FromUnixTimeMilliseconds(stamp); } catch (ArgumentOutOfRangeException) { }
        return new(quotas, error, measured);
    }

    public void Dispose() => client.Dispose();
}
