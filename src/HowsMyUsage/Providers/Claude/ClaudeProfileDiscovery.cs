using System.Reflection;
using System.Text.Json;

namespace Usage;

static class ClaudeProfileDiscovery
{
    public static IEnumerable<string> Candidates() => Candidates(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static IEnumerable<string> Candidates(string roaming, string local)
    {
        foreach (var name in new[] { "Claude", "Claude Desktop", "claude-desktop", "Claude-3p" })
        {
            yield return Path.Combine(roaming, name);
            yield return Path.Combine(local, name);
        }
        var dataRoot = Path.Combine(local, "Claude-Data");
        yield return dataRoot;
        if (Directory.Exists(dataRoot))
            foreach (var profile in Directory.EnumerateDirectories(dataRoot)) yield return profile;
        var packages = Path.Combine(local, "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.EnumerateDirectories(packages, "*Claude*"))
        {
            yield return Path.Combine(package, "LocalCache", "Roaming", "Claude");
            yield return Path.Combine(package, "LocalCache", "Local", "Claude");
            yield return Path.Combine(package, "LocalState", "Claude");
        }
    }

    public static string? Find(string? configuredDirectory = null) => Select(
        string.IsNullOrWhiteSpace(configuredDirectory) ? Candidates() : [configuredDirectory]);

    internal static string? Select(IEnumerable<string> paths) => paths.Where(Directory.Exists)
        .Select(folder => new { Folder = folder, Files = new[] { ClaudeDesktopSession.CookiePath(folder),
            Path.Combine(folder, "config.json"), Path.Combine(folder, "Local State"), Path.Combine(folder, "plan-usage-history.json") }
            .Where(path => path != null && File.Exists(path)).ToArray() })
        .Where(profile => profile.Files.Length > 0)
        .OrderByDescending(profile => profile.Files.Any(IsSessionData))
        .ThenByDescending(profile => profile.Files.Where(IsSessionData).DefaultIfEmpty(profile.Files[0])
            .Max(path => File.GetLastWriteTimeUtc(path!)))
        .Select(profile => profile.Folder).FirstOrDefault();

    static bool IsSessionData(string? path) => Path.GetFileName(path) is "Cookies" or "plan-usage-history.json";

    public static string DiagnosticReport(string? configuredDirectory, int? lastHttpStatus)
    {
        var folder = Find(configuredDirectory);
        var cookie = folder == null ? null : ClaudeDesktopSession.CookiePath(folder);
        string access = "not-found";
        if (cookie != null)
        {
            try
            {
                using var file = new FileStream(cookie, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                access = "readable";
            }
            catch (IOException) { access = "locked-or-unreadable"; }
            catch (UnauthorizedAccessException) { access = "access-denied"; }
        }
        // Deliberately excludes paths, account IDs, cookies, tokens and response bodies.
        return JsonSerializer.Serialize(new
        {
            version = typeof(ClaudeProfileDiscovery).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            windows = Environment.OSVersion.Version.ToString(), customFolder = !string.IsNullOrWhiteSpace(configuredDirectory),
            profileFound = folder != null, profilesDetected = Candidates().Count(path => Select([path]) != null),
            cookieLayout = folder == null || cookie == null ? null : Path.GetRelativePath(folder, cookie), cookieAccess = access,
            localState = folder != null && File.Exists(Path.Combine(folder, "Local State")),
            config = folder != null && File.Exists(Path.Combine(folder, "config.json")),
            usageHistory = folder != null && File.Exists(Path.Combine(folder, "plan-usage-history.json")),
            savedConnection = File.Exists(Path.Combine(AppContext.BaseDirectory, "claude-session.bin")), lastHttpStatus
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
