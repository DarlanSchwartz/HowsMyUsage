using System.Text.Json;

namespace Usage;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (WidgetSettings.IsSystemDrive(AppContext.BaseDirectory))
        {
            MessageBox.Show("Move Usage to a drive other than C: before starting.", "Usage");
            return;
        }
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
        using var gemini = JsonDocument.Parse("{\"userStatus\":{\"clientModelConfigs\":[{\"label\":\"Gemini Pro\",\"quotaInfo\":{\"remainingFraction\":0.42}},{\"label\":\"Gemini Flash\",\"quotaInfo\":{}},{\"label\":\"Claude\",\"quotaInfo\":{\"remainingFraction\":0}}]}}");
        var parsed = Providers.ParseAntigravity(gemini.RootElement);
        if (parsed.Quotas.Count != 1 || parsed.Remaining != 42) throw new Exception("Antigravity parser failed");
        using var claudeFixture = JsonDocument.Parse("{\"five_hour\":{\"utilization\":20},\"seven_day\":{\"utilization\":65},\"extra_usage\":{\"utilization\":99}}");
        var claudeParsed = ClaudeUsageService.Parse(claudeFixture.RootElement);
        if (claudeParsed.Quotas.Count != 1 || claudeParsed.Remaining != 35) throw new Exception("Claude must show only the weekly quota");
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
        using var claude = new ClaudeUsageService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var results = await Task.WhenAll(Safe(Providers.Codex, cts.Token), Safe(Providers.Antigravity, cts.Token), Safe(claude.Read, cts.Token));
        Directory.CreateDirectory("artifacts");
        await File.WriteAllTextAsync("artifacts/check.json", JsonSerializer.Serialize(new { parsers = "passed", codex = results[0], antigravity = results[1], claude = results[2] }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<Snapshot> Safe(Func<CancellationToken, Task<Snapshot>> fetch, CancellationToken token)
    {
        try { return await fetch(token); }
        catch (OperationCanceledException) { return new([], "Request timed out. Try refreshing."); }
        catch (Exception e) { return new([], e is InvalidOperationException ? e.Message : "Could not fetch usage. Check that the app is installed and signed in."); }
    }
}
