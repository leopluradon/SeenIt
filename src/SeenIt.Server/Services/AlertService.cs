using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Problems worth telling the admins about: failed or missing backups, failed off-site copies,
/// failed imports and bursts of errors. Each is emailed to the admins (when email is set up) at
/// most once per cool-down, and the recent ones are listed on the admin page.
/// </summary>
public class AlertService(IServiceScopeFactory scopes, EmailService email, AdminService admins, ILogger<AlertService> log)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRaised = new();
    private readonly ConcurrentQueue<AdminAlert> _recent = new();

    public IReadOnlyList<AdminAlert> Recent => _recent.Reverse().ToList();

    public async Task RaiseAsync(string key, string subject, string body, TimeSpan cooldown)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastRaised.TryGetValue(key, out var last) && now - last < cooldown) return;
        _lastRaised[key] = now;
        log.LogWarning("Alert: {Subject}: {Body}", subject, body);

        var emailed = false;
        if (email.Enabled)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var names = admins.Admins.ToList();
                var to = await db.Users.Where(u => names.Contains(u.UserName!) && u.Email != null).Select(u => u.Email!).ToListAsync();
                foreach (var address in to)
                    email.Queue(new EmailMessage(address, $"SeenIt: {subject}", body + "\n\nSee the admin page for details.",
                        $"<p>{WebUtility.HtmlEncode(body).Replace("\n", "<br>")}</p><p>See the admin page for details.</p>"));
                emailed = to.Count > 0;
            }
            catch (Exception ex) { log.LogWarning(ex, "Couldn't email alert {Subject}", subject); }
        }

        _recent.Enqueue(new AdminAlert(now, subject, body, emailed));
        while (_recent.Count > 50 && _recent.TryDequeue(out _)) { }
    }
}

/// <summary>What the backup container last reported (files it writes to the shared status folder).</summary>
public class BackupStatus(IConfiguration config)
{
    private string? Dir => config["SeenIt:StatusDir"] is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public bool Available => Dir is not null;

    /// <summary>When the file was last written (the event's time) and what it says.</summary>
    public (DateTimeOffset At, string Message)? Read(string name)
    {
        if (Dir is not { } dir) return null;
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return null;
        try { return (File.GetLastWriteTimeUtc(path), File.ReadAllText(path).Trim()); }
        catch (IOException) { return null; }
    }

    public List<AdminSetting> Health()
    {
        if (!Available) return [new("Backups", "status not reported (backup container not sharing /status)", false)];
        var now = DateTimeOffset.UtcNow;
        string Ago(DateTimeOffset at) => (now - at).TotalHours < 48 ? $"{(int)(now - at).TotalHours}h ago" : $"{(int)(now - at).TotalDays}d ago";
        AdminSetting Item(string label, string ok, string failed, TimeSpan maxAge)
        {
            var good = Read(ok);
            var bad = Read(failed);
            if (bad is { } b && (good is null || b.At > good.Value.At)) return new(label, $"FAILED {Ago(b.At)}: {b.Message}", false);
            if (good is not { } g) return new(label, "no successful run reported yet", false);
            return new(label, $"{Ago(g.At)} · {g.Message}", now - g.At <= maxAge);
        }
        var list = new List<AdminSetting> { Item("Nightly backup", "backup-ok", "backup-failed", BackupMaxAge) };
        if (Read("offsite-enabled") is not null) list.Add(Item("Off-site copy", "offsite-ok", "offsite-failed", OffsiteMaxAge));
        return list;
    }

    public static readonly TimeSpan BackupMaxAge = TimeSpan.FromHours(30);
    public static readonly TimeSpan OffsiteMaxAge = TimeSpan.FromDays(8);
}

/// <summary>Checks for alert-worthy problems every ten minutes.</summary>
public class AlertWorker(IServiceScopeFactory scopes, AlertService alerts, BackupStatus backups, RecentLogs logs, ILogger<AlertWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var importsSince = DateTimeOffset.UtcNow;
        // Give a fresh start (and the backup container) a moment before judging anything.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { importsSince = await CheckAsync(importsSince, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Alert check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<DateTimeOffset> CheckAsync(DateTimeOffset importsSince, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromHours(12);

        if (backups.Available)
        {
            var ok = backups.Read("backup-ok");
            var failed = backups.Read("backup-failed");
            if (failed is { } f && (ok is null || f.At > ok.Value.At))
                await alerts.RaiseAsync("backup-failed", "nightly backup failed", $"The database backup at {f.At:yyyy-MM-dd HH:mm} UTC failed: {f.Message}", cooldown);
            else if (ok is { } o && now - o.At > BackupStatus.BackupMaxAge)
                await alerts.RaiseAsync("backup-stale", "no recent backup", $"The last successful backup was {o.At:yyyy-MM-dd HH:mm} UTC. Is the seenit-backup container running?", cooldown);

            if (backups.Read("offsite-enabled") is not null)
            {
                var offOk = backups.Read("offsite-ok");
                var offFailed = backups.Read("offsite-failed");
                if (offFailed is { } of && (offOk is null || of.At > offOk.Value.At))
                    await alerts.RaiseAsync("offsite-failed", "off-site backup copy failed", $"Copying backups off-site failed at {of.At:yyyy-MM-dd HH:mm} UTC: {of.Message}", cooldown);
                else if (offOk is { } oo && now - oo.At > BackupStatus.OffsiteMaxAge)
                    await alerts.RaiseAsync("offsite-stale", "off-site backups are out of date", $"The last off-site copy was {oo.At:yyyy-MM-dd HH:mm} UTC.", cooldown);
            }
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var failedImports = await db.ImportJobs.Where(j => j.Status == ImportStatus.Failed && j.FinishedAt > importsSince)
                .Join(db.Users, j => j.UserId, u => u.Id, (j, u) => new { j.Id, j.Kind, j.FileName, j.Message, u.UserName }).ToListAsync(ct);
            foreach (var j in failedImports)
                await alerts.RaiseAsync($"import-{j.Id}", $"{j.Kind} import failed for {j.UserName}", $"{j.FileName}: {j.Message}", TimeSpan.MaxValue);
        }

        var errors = logs.Snapshot().Where(e => e.At > now.AddHours(-1) && e.Level is "Error" or "Critical").ToList();
        if (errors.Count >= 5)
            await alerts.RaiseAsync("errors", $"{errors.Count} errors in the last hour",
                string.Join("\n", errors.Take(5).Select(e => $"{e.At:HH:mm} {e.Category}: {e.Message.Split('\n')[0]}")), TimeSpan.FromHours(6));

        return now;
    }
}
