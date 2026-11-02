namespace SeenIt.Server.Services;

/// <summary>Who may open the admin page: usernames in SeenIt:Admins (comma-separated).</summary>
public class AdminService(IConfiguration config)
{
    private readonly HashSet<string> _admins = (config["SeenIt:Admins"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Admins => _admins;
    public bool IsAdmin(string? userName) => userName is not null && _admins.Contains(userName);
}
