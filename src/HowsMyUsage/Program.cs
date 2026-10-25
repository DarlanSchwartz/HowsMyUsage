using System.Text.Json;

namespace Usage;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var temp = Path.Combine(AppContext.BaseDirectory, "temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        ApplicationConfiguration.Initialize();
        if (args.Contains("--check")) { Check().GetAwaiter().GetResult(); return; }
        using var mutex = new Mutex(true, "Usage.NativeTray", out var first);
        if (!first) return;
        if (args.Contains("--enable-startup")) UsageWidget.SetStartup(true);
        Application.Run(new UsageWidget());
    }

    static async Task Check()
    {
        using var codex = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":100}}}");
        if (Providers.ParseCodex(codex.RootElement).Remaining != 0) throw new Exception("Codex parser failed");
        using var gemini = JsonDocument.Parse("""
            {"response":{"groups":[{"buckets":[
                {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.97},
                {"bucketId":"gemini-5h","window":"5h","remainingFraction":1},
                {"bucketId":"3p-weekly","window":"weekly","remainingFraction":0.39},
                {"bucketId":"3p-5h","window":"5h","remainingFraction":1},
                {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":null}
            ]}]}}
            """);
        var parsed = Providers.ParseAntigravity(gemini.RootElement);
        if (parsed.Quotas.Count != 4 || parsed.Remaining != 97 ||
            !parsed.Quotas.Any(q => q.Name.StartsWith("Claude / GPT") && q.Name.EndsWith("Weekly") && q.Remaining == 39))
            throw new Exception("Antigravity must select Gemini weekly and retain both pools for details");
        using var sessionQuota = JsonDocument.Parse("""
            {"response":{"groups":[{"buckets":[{"bucketId":"gemini-5h","window":"5h","remainingFraction":1}]}]}}
            """);
        if (Providers.ParseAntigravity(sessionQuota.RootElement).Remaining != null)
            throw new Exception("Antigravity session must not replace a missing weekly quota");
        using var claudeFixture = JsonDocument.Parse("{\"five_hour\":{\"utilization\":20},\"seven_day\":{\"utilization\":65},\"extra_usage\":{\"utilization\":99}}");
        var claudeParsed = ClaudeUsageService.Parse(claudeFixture.RootElement);
        if (claudeParsed.Quotas.Count != 2 || claudeParsed.Remaining != 35) throw new Exception("Claude indicator must remain weekly while retaining session details");
        using var missing = JsonDocument.Parse("{\"five_hour\":{\"utilization\":null},\"seven_day\":null}");
        if (ClaudeUsageService.Parse(missing.RootElement).Remaining != null) throw new Exception("Missing Claude quota must stay unknown");
        using var current = JsonDocument.Parse("{\"limits\":[{\"kind\":\"session\",\"percent\":12},{\"kind\":\"weekly_all\",\"percent\":3}],\"five_hour\":{\"utilization\":90}}");
        if (ClaudeUsageService.Parse(current.RootElement).Remaining != 97) throw new Exception("Claude must select weekly_all, not the lower session quota");
        using var sessionOnly = JsonDocument.Parse("{\"limits\":[{\"kind\":\"session\",\"percent\":20}],\"five_hour\":{\"utilization\":20}}");
        if (ClaudeUsageService.Parse(sessionOnly.RootElement).Remaining != null) throw new Exception("Missing weekly quota must not fall back to session");
        using var cached = JsonDocument.Parse("{\"samples\":[{\"t\":1791079244560,\"u\":{\"fh\":1,\"sd\":0}}]}");
        var stale = ClaudeUsageService.ParseCached(cached.RootElement, "Session locked");
        if (stale.Remaining != 100 || stale.Error == null || stale.MeasuredAt?.ToUnixTimeMilliseconds() != 1791079244560) throw new Exception("Cached weekly quota must retain its original timestamp and failure");
        using var cachedSessionOnly = JsonDocument.Parse("{\"samples\":[{\"t\":1791079244560,\"u\":{\"fh\":1}}]}");
        if (ClaudeUsageService.ParseCached(cachedSessionOnly.RootElement, "Session locked").Remaining != null) throw new Exception("Cached session quota must not replace missing weekly quota");
        using var fable = JsonDocument.Parse("""
            {"limits":[{"kind":"weekly_all","group":"weekly","percent":40},
            {"kind":"session","group":"session","percent":80},
            {"kind":"weekly_model","group":"weekly","percent":25,"scope":{"model":{"display_name":"Fable 5"}}}],
            "five_hour":{"utilization":80},"seven_day":{"utilization":40}}
            """);
        var withFable = ClaudeUsageService.Parse(fable.RootElement);
        if (withFable.Remaining != 60 || withFable.Quotas.Count != 3 ||
            !withFable.Quotas.Any(q => q.Name == "Weekly - Fable 5" && q.Remaining == 75 && !q.IncludeInSummary))
            throw new Exception("Session and Fable must appear in details without changing weekly total");
        if (!stale.Quotas.Any(q => q.Name == "Session - 5 hours" && q.Remaining == 99))
            throw new Exception("Cached Claude session quota must appear in details");
        using var codexSession = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":40,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":10080}}}
            """);
        var codexDetails = Providers.ParseCodex(codexSession.RootElement);
        if (codexDetails.Quotas.Count != 2 || !codexDetails.Quotas.Any(q => q.Name.Contains("Session")))
            throw new Exception("Codex session quota must be included when returned");
        var protectedFixture = System.Text.Encoding.UTF8.GetBytes("connection-roundtrip-fixture");
        var encryptedFixture = ClaudeDesktopSession.Protect(protectedFixture);
        var restoredFixture = ClaudeDesktopSession.Unprotect(encryptedFixture);
        if (!restoredFixture.SequenceEqual(protectedFixture) || encryptedFixture.SequenceEqual(protectedFixture))
            throw new Exception("Claude connection protection failed");
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(restoredFixture);
        CheckClaudeProfileLayouts();
        using var claude = new ClaudeUsageService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var results = await Task.WhenAll(Safe(Providers.Codex, cts.Token), Safe(Providers.Antigravity, cts.Token), Safe(claude.Read, cts.Token));
        Directory.CreateDirectory("artifacts");
        await File.WriteAllTextAsync("artifacts/check.json", JsonSerializer.Serialize(new { parsers = "passed", codex = results[0], antigravity = results[1], claude = results[2] }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void CheckClaudeProfileLayouts()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "temp", "claude-profile-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var relative in new[] { "Network/Cookies", "Cookies", "Default/Network/Cookies", "Default/Cookies" })
            {
                var path = Path.GetFullPath(Path.Combine(folder, relative));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
                if (ClaudeDesktopSession.CookiePath(folder) != path || ClaudeProfileDiscovery.Select([folder]) != folder)
                    throw new Exception("Claude profile layout detection failed: " + relative);
                File.Delete(path);
            }
            File.WriteAllText(Path.Combine(folder, "plan-usage-history.json"), "{\"samples\":[]}");
            if (ClaudeProfileDiscovery.Select([folder]) != folder)
                throw new Exception("Claude history must be available even without a cookie database");
            var configOnly = Path.Combine(folder, "config-only");
            Directory.CreateDirectory(configOnly);
            File.WriteAllText(Path.Combine(configOnly, "config.json"), "{}");
            if (ClaudeProfileDiscovery.Select([configOnly, folder]) != folder)
                throw new Exception("A config-only folder must not hide session history");
            var local = Path.Combine(folder, "local");
            var modern = Path.Combine(local, "Claude-Data", "claude_profile");
            Directory.CreateDirectory(modern);
            File.WriteAllText(Path.Combine(modern, "plan-usage-history.json"), "{\"samples\":[]}");
            var candidates = ClaudeProfileDiscovery.Candidates(Path.Combine(folder, "roaming"), local).ToArray();
            if (!candidates.Contains(modern) || ClaudeProfileDiscovery.Select(candidates) != modern)
                throw new Exception("Claude-Data profiles must be discovered automatically");
        }
        finally { Directory.Delete(folder, true); }
    }

    public static async Task<Snapshot> Safe(Func<CancellationToken, Task<Snapshot>> fetch, CancellationToken token)
    {
        try { return await fetch(token); }
        catch (OperationCanceledException) { return new([], "Request timed out. Try refreshing."); }
        catch (Exception e) { return new([], e is InvalidOperationException ? e.Message : "Could not fetch usage. Check that the app is installed and signed in."); }
    }
}
