using Microsoft.EntityFrameworkCore;
namespace DeveloperBrowser.Infrastructure.Persistence;
public sealed class DeveloperBrowserDbContext(DbContextOptions<DeveloperBrowserDbContext> options) : DbContext(options)
{
    public DbSet<RestHistoryEntry> RestHistory => Set<RestHistoryEntry>();
    public DbSet<BookmarkEntity> Bookmarks => Set<BookmarkEntity>();
    public DbSet<BookmarkFolderEntity> BookmarkFolders => Set<BookmarkFolderEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RestHistoryEntry>(entity => { entity.HasKey(item => item.Id); entity.Property(item => item.Method).HasMaxLength(10).IsRequired(); entity.Property(item => item.Url).IsRequired(); });
        modelBuilder.Entity<BookmarkFolderEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(80).IsRequired();
            entity.HasIndex(item => item.Name).IsUnique();
        });
        modelBuilder.Entity<BookmarkEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Url).IsRequired();
            entity.HasIndex(item => item.Url).IsUnique();
            entity.HasIndex(item => item.FolderId);
            entity.HasOne<BookmarkFolderEntity>().WithMany().HasForeignKey(item => item.FolderId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
public sealed class RestHistoryEntry { public Guid Id { get; init; } = Guid.NewGuid(); public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow; public required string Method { get; init; } public required string Url { get; init; } public int? StatusCode { get; init; } }
public sealed class BookmarkFolderEntity { public Guid Id { get; set; } = Guid.NewGuid(); public required string Name { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public bool IsDefault { get; set; } }
public sealed class BookmarkEntity { public Guid Id { get; set; } = Guid.NewGuid(); public Guid FolderId { get; set; } public required string Title { get; set; } public required string Url { get; set; } public string? Description { get; set; } public string? Note { get; set; } public string? FaviconUrl { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
