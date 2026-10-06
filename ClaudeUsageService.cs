using System.Net;
using System.Text.Json;

namespace Usage;

sealed class ClaudeUsageService : IDisposable
{
    readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    DateTimeOffset retryAfter;
    (string SessionKey, string? Organization)? cachedSession;

    public static string? FindProfile()
    {
        var paths = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude") };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            paths.AddRange(Directory.EnumerateDirectories(packages, "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        return paths.Where(p => File.Exists(Path.Combine(p, "Network", "Cookies")))
            .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "Network", "Cookies"))).FirstOrDefault();
    }

    public async Task<Snapshot> Read(CancellationToken token)
    {
        try { return await ReadLive(token); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Security.Cryptography.CryptographicException or HttpRequestException)
        {
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
        var session = cachedSession ??= await Task.Run(() => ClaudeDesktopSession.Read(folder), token);
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
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            cachedSession = null;
            throw new InvalidOperationException($"Claude rejected the usage request (HTTP {(int)response.StatusCode}). Open Claude Desktop and check your sign-in.");
        }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Claude usage unavailable (HTTP {(int)response.StatusCode}).");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    public static Snapshot Parse(JsonElement root)
    {
        List<Quota> quotas = [];
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "Limit" : "Limit";
                if (name == "weekly_all") Add(item, "percent", "Weekly · all models");
            }
        }
        if (quotas.Count == 0 && root.TryGetProperty("seven_day", out var window) && window.ValueKind == JsonValueKind.Object)
            Add(window, "utilization", "Weekly · all models");
        return new(quotas, quotas.Count == 0 ? "Claude did not return the weekly limit." : null, DateTimeOffset.UtcNow);

        void Add(JsonElement window, string key, string name)
        {
            if (!window.TryGetProperty(key, out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0 || percent > 100) return;
            DateTimeOffset? reset = window.TryGetProperty("resets_at", out var r) && DateTimeOffset.TryParse(r.ToString(), out var time) ? time : null;
            quotas.Add(new(name, 100 - percent, reset));
        }
    }

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
        if (usage.TryGetProperty("sd", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var percent) && double.IsFinite(percent) && percent >= 0 && percent <= 100)
            quotas.Add(new("Weekly · all models", 100 - percent, null));
        DateTimeOffset? measured = null;
        try { measured = DateTimeOffset.FromUnixTimeMilliseconds(stamp); } catch (ArgumentOutOfRangeException) { }
        return new(quotas, error, measured);
    }

    public void Dispose() => client.Dispose();
}
