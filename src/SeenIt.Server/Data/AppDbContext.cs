using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SeenIt.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<MovieMeta> Movies => Set<MovieMeta>();
    public DbSet<ShowMeta> Shows => Set<ShowMeta>();
    public DbSet<EpisodeMeta> Episodes => Set<EpisodeMeta>();
    public DbSet<WatchEvent> WatchEvents => Set<WatchEvent>();
    public DbSet<WatchlistItem> Watchlist => Set<WatchlistItem>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<UserList> Lists => Set<UserList>();
    public DbSet<UserListItem> ListItems => Set<UserListItem>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<JellyfinEvent> JellyfinEvents => Set<JellyfinEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.JellyfinToken).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(64);
        });

        b.Entity<MovieMeta>(e =>
        {
            e.HasKey(m => m.TmdbId);
            e.Property(m => m.TmdbId).ValueGeneratedNever();
        });

        b.Entity<ShowMeta>(e =>
        {
            e.HasKey(s => s.TmdbId);
            e.Property(s => s.TmdbId).ValueGeneratedNever();
            e.Ignore(s => s.IsOngoing);
        });

        b.Entity<EpisodeMeta>(e =>
        {
            e.HasKey(x => new { x.ShowTmdbId, x.SeasonNumber, x.EpisodeNumber });
            e.HasIndex(x => x.AirDate);
        });

        b.Entity<WatchEvent>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Type, x.TmdbId });
            e.HasIndex(x => new { x.UserId, x.WatchedAt });
        });

        b.Entity<WatchlistItem>(e => e.HasIndex(x => new { x.UserId, x.Type, x.TmdbId }).IsUnique());

        b.Entity<Rating>(e =>
            e.HasIndex(x => new { x.UserId, x.Type, x.TmdbId, x.SeasonNumber, x.EpisodeNumber })
             .IsUnique()
             .AreNullsDistinct(false));

        b.Entity<UserList>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserListItem>(e => e.HasIndex(x => new { x.ListId, x.Type, x.TmdbId }).IsUnique());

        b.Entity<ImportJob>(e => e.HasIndex(x => new { x.UserId, x.CreatedAt }));
        b.Entity<JellyfinEvent>(e => e.HasIndex(x => new { x.UserId, x.ReceivedAt }));

        // Per-user rows go when the user does.
        foreach (var t in new[] { typeof(WatchEvent), typeof(WatchlistItem), typeof(Rating), typeof(ImportJob), typeof(JellyfinEvent) })
            b.Entity(t).HasOne(typeof(AppUser)).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade);
    }
}
