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
    public DbSet<ShowState> ShowStates => Set<ShowState>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<ShowRewatch> Rewatches => Set<ShowRewatch>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<CollectionMeta> Collections => Set<CollectionMeta>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<LetterboxdSync> LetterboxdSyncs => Set<LetterboxdSync>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.JellyfinToken).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(64);
            e.Property(u => u.Region).HasMaxLength(2);
        });

        b.Entity<MovieMeta>(e =>
        {
            e.HasKey(m => m.TmdbId);
            e.Property(m => m.TmdbId).ValueGeneratedNever();
            e.OwnsMany(m => m.Cast, o => o.ToJson());
            e.OwnsMany(m => m.Directors, o => o.ToJson());
        });

        b.Entity<ShowMeta>(e =>
        {
            e.HasKey(s => s.TmdbId);
            e.Property(s => s.TmdbId).ValueGeneratedNever();
            e.Ignore(s => s.IsOngoing);
            e.OwnsMany(s => s.Cast, o => o.ToJson());
            e.OwnsMany(s => s.Creators, o => o.ToJson());
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
            e.HasIndex(x => x.ImportJobId);
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

        b.Entity<UserListItem>(e =>
        {
            e.HasIndex(x => new { x.ListId, x.Type, x.TmdbId }).IsUnique();
            e.Property(x => x.Note).HasMaxLength(500);
        });

        b.Entity<ImportJob>(e => e.HasIndex(x => new { x.UserId, x.CreatedAt }));
        b.Entity<JellyfinEvent>(e => e.HasIndex(x => new { x.UserId, x.ReceivedAt }));

        b.Entity<ShowState>(e => e.HasKey(x => new { x.UserId, x.ShowTmdbId }));
        b.Entity<CheckIn>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasIndex(x => x.EndsAt);
        });
        b.Entity<Follow>(e =>
        {
            e.HasKey(x => new { x.FollowerId, x.FolloweeId });
            e.HasIndex(x => x.FolloweeId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.FollowerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.FolloweeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShowRewatch>(e => e.HasIndex(x => new { x.UserId, x.ShowTmdbId }));
        b.Entity<Favorite>(e => e.HasKey(x => new { x.UserId, x.Type, x.TmdbId }));
        b.Entity<CollectionMeta>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.OwnsMany(x => x.Parts, o => o.ToJson());
        });
        b.Entity<MovieMeta>().HasIndex(x => x.CollectionId);
        b.Entity<Goal>(e => e.HasIndex(x => new { x.UserId, x.Year, x.Kind }).IsUnique());
        b.Entity<LetterboxdSync>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.LastResult).HasMaxLength(300);
        });
        b.Entity<ActivityEntry>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserId, x.At });
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.Detail).HasMaxLength(500);
        });
        b.Entity<Deployment>(e =>
        {
            e.Property(x => x.Version).HasMaxLength(64);
            e.Property(x => x.Commit).HasMaxLength(64);
            e.Property(x => x.Subject).HasMaxLength(300);
        });
        b.Entity<Invite>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasIndex(x => x.CreatedById);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UsedById).OnDelete(DeleteBehavior.SetNull);
        });

        // Per-user rows go when the user does.
        foreach (var t in new[] { typeof(WatchEvent), typeof(WatchlistItem), typeof(Rating), typeof(ImportJob), typeof(JellyfinEvent), typeof(ShowState), typeof(CheckIn), typeof(ShowRewatch), typeof(Favorite), typeof(Goal), typeof(LetterboxdSync) })
            b.Entity(t).HasOne(typeof(AppUser)).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade);
    }
}
