using System.Globalization;
using System.Text;

namespace SeenIt.Server.Services;

/// <summary>
/// Which build is running. scripts/deploy.sh passes these in as Docker build args; a local
/// `dotnet run` reports "dev".
/// </summary>
public static class AppInfo
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>1.&lt;number of commits&gt;, so it only ever goes up.</summary>
    public static string Version { get; } = Env("SEENIT_VERSION") ?? "dev";
    public static string Commit { get; } = Env("SEENIT_COMMIT") ?? "dev";
    public static string? Subject { get; } = Env("SEENIT_SUBJECT_B64") is { } b64 ? TryBase64(b64) : null;
    public static DateTimeOffset? BuiltAt { get; } =
        DateTimeOffset.TryParse(Env("SEENIT_BUILT_AT"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static bool IsDev => Commit == "dev";

    /// <summary>NuGet packages with newer versions, from `dotnet list package --outdated` at build time.</summary>
    public static IReadOnlyList<SeenIt.Contracts.AdminPackage> OutdatedPackages { get; } = ReadOutdated();

    private static List<SeenIt.Contracts.AdminPackage> ReadOutdated()
    {
        var list = new List<SeenIt.Contracts.AdminPackage>();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "outdated-*.json"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("projects", out var projects)) continue;
                foreach (var p in projects.EnumerateArray())
                {
                    var project = Path.GetFileNameWithoutExtension(p.GetProperty("path").GetString() ?? "?");
                    if (!p.TryGetProperty("frameworks", out var fws)) continue;
                    foreach (var fw in fws.EnumerateArray())
                        if (fw.TryGetProperty("topLevelPackages", out var pkgs))
                            foreach (var pkg in pkgs.EnumerateArray())
                                list.Add(new(project, pkg.GetProperty("id").GetString() ?? "?",
                                    pkg.TryGetProperty("resolvedVersion", out var r) ? r.GetString() ?? "?" : "?",
                                    pkg.TryGetProperty("latestVersion", out var l) ? l.GetString() ?? "?" : "?"));
                }
            }
            catch (Exception) { /* a malformed report shouldn't stop the app */ }
        }
        return list.DistinctBy(p => (p.Package, p.Current, p.Latest)).OrderBy(p => p.Package).ToList();
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && v != "unknown" ? v.Trim() : null;

    private static string? TryBase64(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)).Trim(); }
        catch (FormatException) { return null; }
    }
}
