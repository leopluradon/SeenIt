using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

public class InviteService(AppDbContext db, IConfiguration config)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    public const int MaxOpenPerUser = 10;

    /// <summary>Sign-ups need an invite unless SeenIt:OpenRegistration is true.</summary>
    public bool OpenRegistration => config.GetValue("SeenIt:OpenRegistration", false);

    public async Task<List<InviteDto>> ListAsync(Guid userId)
    {
        var rows = await db.Invites.Where(i => i.CreatedById == userId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i, UsedBy = db.Users.Where(u => u.Id == i.UsedById).Select(u => u.UserName).FirstOrDefault() })
            .Take(50).ToListAsync();
        return rows.Select(r => new InviteDto(r.i.Code, r.i.CreatedAt, r.i.ExpiresAt, r.UsedBy, r.i.UsedAt)).ToList();
    }

    public async Task<InviteDto> CreateAsync(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var open = await db.Invites.CountAsync(i => i.CreatedById == userId && i.UsedAt == null && i.ExpiresAt > now);
        if (open >= MaxOpenPerUser)
            throw new TrackingException($"You already have {MaxOpenPerUser} unused invites. Delete one or wait for them to be used.");

        var invite = new Invite
        {
            // 16 characters from a 32-letter alphabet without look-alikes: 80 bits, easy to read out.
            Code = RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 16),
            CreatedById = userId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync();
        return new InviteDto(invite.Code, invite.CreatedAt, invite.ExpiresAt, null, null);
    }

    /// <summary>Deletes an unused invite. Used ones stay as a record of who invited whom.</summary>
    public async Task<bool> DeleteAsync(Guid userId, string code) =>
        await db.Invites.Where(i => i.Code == code && i.CreatedById == userId && i.UsedAt == null).ExecuteDeleteAsync() > 0;

    public async Task<InviteCheck> CheckAsync(string code)
    {
        var now = DateTimeOffset.UtcNow;
        var by = await db.Invites.Where(i => i.Code == Normalize(code) && i.UsedAt == null && i.ExpiresAt > now)
            .Select(i => db.Users.Where(u => u.Id == i.CreatedById).Select(u => u.DisplayName ?? u.UserName).FirstOrDefault())
            .FirstOrDefaultAsync();
        return by is null ? new InviteCheck(false, null) : new InviteCheck(true, by);
    }

    /// <summary>Marks the invite used before the account exists, so two sign-ups can't share one. Returns the inviter.</summary>
    public async Task<Guid?> ClaimAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = Normalize(code);
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.Invites.Where(i => i.Code == c && i.UsedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, now));
        if (claimed == 0) return null;
        return await db.Invites.Where(i => i.Code == c).Select(i => (Guid?)i.CreatedById).FirstAsync();
    }

    /// <summary>Gives the invite back if the account couldn't be created (e.g. the username was taken).</summary>
    public Task ReleaseAsync(string code) =>
        db.Invites.Where(i => i.Code == Normalize(code) && i.UsedById == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, (DateTimeOffset?)null));

    public async Task CompleteAsync(string code, Guid inviterId, Guid newUserId)
    {
        await db.Invites.Where(i => i.Code == Normalize(code))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedById, newUserId));
        db.Follows.AddRange(
            new Follow { FollowerId = inviterId, FolloweeId = newUserId },
            new Follow { FollowerId = newUserId, FolloweeId = inviterId });
        await db.SaveChangesAsync();
    }

    private static string Normalize(string code) => code.Trim().ToLowerInvariant();
}
