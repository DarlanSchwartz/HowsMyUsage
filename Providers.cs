using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Usage;

record Quota(string Name, double Remaining, DateTimeOffset? Reset);
record Snapshot(List<Quota> Quotas, string? Error = null, DateTimeOffset? MeasuredAt = null)
{
    public double? Remaining => Quotas.Count == 0 ? null : Quotas.Min(q => q.Remaining);
}

static class Providers
{
    public static string? CodexPath()
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
            .Select(p => Path.Combine(p, "codex.exe")).FirstOrDefault(File.Exists);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        return path ?? (Directory.Exists(folder) ? Directory.GetFiles(folder, "codex.exe", SearchOption.AllDirectories).FirstOrDefault() : null);
    }

    public static async Task<Snapshot> Codex(CancellationToken token)
    {
        var path = CodexPath();
        if (path == null) return new([], "Codex not found. Install it and sign in.");
        using var process = Process.Start(new ProcessStartInfo(path, "app-server")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var drain = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"usage_tray\",\"version\":\"1.0.0\"}}}");
            await RpcResult(process, 1, token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
            return ParseCodex(await RpcResult(process, 2, token));
        }
        finally { if (!process.HasExited) process.Kill(true); try { await drain; } catch (OperationCanceledException) { } }
    }

    static async Task<JsonElement> RpcResult(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var value) || !value.TryGetInt32(out var number) || number != id) continue;
            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex usage unavailable. Check your sign-in.");
            return root.GetProperty("result").Clone();
        }
        throw new InvalidOperationException("Codex closed the connection.");
    }

    public static Snapshot ParseCodex(JsonElement root)
    {
        List<Quota> quotas = [];
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
            foreach (var bucket in map.EnumerateObject()) Add(bucket.Value, bucket.Name);
        else if (root.TryGetProperty("rateLimits", out var single)) Add(single, "Codex");
        return new(quotas, quotas.Count == 0 ? "No limits available for this account." : null);

        void Add(JsonElement bucket, string name)
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("usedPercent", out var used) || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
                var duration = window.TryGetProperty("windowDurationMins", out var mins) && mins.TryGetInt32(out var m)
                    ? (m >= 1440 ? $"{m / 1440d:0.#} days" : $"{m / 60d:0.#} h") : key;
                DateTimeOffset? reset = window.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                quotas.Add(new($"{name} · {duration}", Math.Clamp(100 - percent, 0, 100), reset));
            }
        }
    }

    public static async Task<Snapshot> Antigravity(CancellationToken token)
    {
        // Discover only Antigravity language servers. CSRF tokens stay in memory.
        const string script = "@((Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*language_server*' -and ($_.ExecutablePath -match 'antigravity' -or $_.CommandLine -match 'antigravity') }) | ForEach-Object { $p=$_; $ports=@(Get-NetTCPConnection -State Listen -OwningProcess $p.ProcessId -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique); [pscustomobject]@{args=$p.CommandLine;ports=$ports} }) | ConvertTo-Json -Depth 4 -Compress";
        using var discovery = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output;
        try { output = await discovery.StandardOutput.ReadToEndAsync(token); await discovery.WaitForExitAsync(token); }
        finally { if (!discovery.HasExited) discovery.Kill(true); }
        if (string.IsNullOrWhiteSpace(output)) return new([], "Open Antigravity and sign in.");
        using var doc = JsonDocument.Parse(output);
        var servers = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        foreach (var server in servers)
        {
            var args = server.GetProperty("args").GetString() ?? "";
            var csrf = Regex.Match(args, "--csrf_token(?:=|\\s+)\"?([^\\s\"]+)").Groups[1].Value;
            if (csrf.Length == 0) continue;
            foreach (var port in server.GetProperty("ports").EnumerateArray())
            foreach (var scheme in new[] { "https", "http" })
            {
                // The self-signed certificate exception is limited to this loopback-only client.
                using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                    ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host == "127.0.0.1" };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                client.DefaultRequestHeaders.Add("X-Codeium-Csrf-Token", csrf);
                client.DefaultRequestHeaders.Add("Connect-Protocol-Version", "1");
                try
                {
                    using var response = await client.PostAsJsonAsync($"{scheme}://127.0.0.1:{port.GetInt32()}/exa.language_server_pb.LanguageServerService/GetUserStatus",
                        new { metadata = new { ideName = "antigravity", extensionName = "antigravity", locale = "pt-BR" } }, token);
                    if (!response.IsSuccessStatusCode) continue;
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    var snapshot = ParseAntigravity(body.RootElement);
                    if (snapshot.Quotas.Count > 0) return snapshot;
                }
                catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { token.ThrowIfCancellationRequested(); }
            }
        }
        return new([], "No Gemini limits available. Open an Antigravity conversation and refresh.");
    }

    public static Snapshot ParseAntigravity(JsonElement root)
    {
        List<Quota> quotas = [];
        Walk(root);
        return new(quotas);
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var item in node.EnumerateArray()) Walk(item); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("quotaInfo", out var quota))
            {
                var label = node.TryGetProperty("label", out var l) ? l.GetString() : node.TryGetProperty("modelId", out var m) ? m.ToString() : null;
                if (label?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true && quota.TryGetProperty("remainingFraction", out var f) && f.TryGetDouble(out var fraction) && double.IsFinite(fraction))
                {
                    DateTimeOffset? reset = quota.TryGetProperty("resetTime", out var r) && DateTimeOffset.TryParse(r.ToString(), out var time) ? time : null;
                    quotas.Add(new(label, Math.Clamp(fraction * 100, 0, 100), reset));
                }
            }
            foreach (var property in node.EnumerateObject()) Walk(property.Value);
        }
    }
}
