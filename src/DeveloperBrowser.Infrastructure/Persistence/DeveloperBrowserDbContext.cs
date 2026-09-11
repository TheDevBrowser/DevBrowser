using Microsoft.EntityFrameworkCore;
namespace DeveloperBrowser.Infrastructure.Persistence;
public sealed class DeveloperBrowserDbContext(DbContextOptions<DeveloperBrowserDbContext> options) : DbContext(options)
{
    public DbSet<RestHistoryEntry> RestHistory => Set<RestHistoryEntry>();
    public DbSet<BookmarkEntity> Bookmarks => Set<BookmarkEntity>();
    public DbSet<BookmarkFolderEntity> BookmarkFolders => Set<BookmarkFolderEntity>();
    public DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    public DbSet<CollectionFolderEntity> CollectionFolders => Set<CollectionFolderEntity>();
    public DbSet<SavedRequestEntity> SavedRequests => Set<SavedRequestEntity>();
    public DbSet<EnvironmentEntity> Environments => Set<EnvironmentEntity>();
    public DbSet<EnvironmentVariableEntity> EnvironmentVariables => Set<EnvironmentVariableEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RestHistoryEntry>(entity => { entity.HasKey(item => item.Id); entity.Property(item => item.Method).HasMaxLength(10).IsRequired(); entity.Property(item => item.Url).IsRequired(); });
        modelBuilder.Entity<BookmarkFolderEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(80).IsRequired();
            entity.HasIndex(item => new { item.ParentFolderId, item.Name }).IsUnique();
            entity.HasOne<BookmarkFolderEntity>().WithMany().HasForeignKey(item => item.ParentFolderId).OnDelete(DeleteBehavior.Restrict);
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
        modelBuilder.Entity<CollectionEntity>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(160).IsRequired(); });
        modelBuilder.Entity<CollectionFolderEntity>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(160).IsRequired(); entity.HasIndex(x => x.CollectionId); });
        modelBuilder.Entity<SavedRequestEntity>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(240).IsRequired(); entity.Property(x => x.Method).HasMaxLength(10).IsRequired(); entity.Property(x => x.Url).IsRequired(); entity.HasIndex(x => x.CollectionId); entity.HasIndex(x => x.FolderId); });
        modelBuilder.Entity<EnvironmentEntity>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(160).IsRequired(); });
        modelBuilder.Entity<EnvironmentVariableEntity>(entity => { entity.HasKey(x => x.Id); entity.Property(x => x.Key).HasMaxLength(160).IsRequired(); entity.HasIndex(x => new { x.EnvironmentId, x.Key }).IsUnique(); });
    }
}
public sealed class RestHistoryEntry { public Guid Id { get; init; } = Guid.NewGuid(); public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow; public required string Method { get; init; } public required string Url { get; init; } public int? StatusCode { get; init; } }
public sealed class BookmarkFolderEntity { public Guid? ParentFolderId { get; set; } public Guid Id { get; set; } = Guid.NewGuid(); public required string Name { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public bool IsDefault { get; set; } }
public sealed class BookmarkEntity { public Guid Id { get; set; } = Guid.NewGuid(); public Guid FolderId { get; set; } public required string Title { get; set; } public required string Url { get; set; } public string? Description { get; set; } public string? Note { get; set; } public string? FaviconUrl { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class CollectionEntity { public Guid Id { get; set; } = Guid.NewGuid(); public required string Name { get; set; } public string? Description { get; set; } public int SortOrder { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class CollectionFolderEntity { public Guid Id { get; set; } = Guid.NewGuid(); public Guid CollectionId { get; set; } public Guid? ParentFolderId { get; set; } public required string Name { get; set; } public int SortOrder { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class SavedRequestEntity { public Guid Id { get; set; } = Guid.NewGuid(); public Guid CollectionId { get; set; } public Guid? FolderId { get; set; } public required string Name { get; set; } public string? Description { get; set; } public required string Method { get; set; } public required string Url { get; set; } public string ParametersJson { get; set; } = "[]"; public string HeadersJson { get; set; } = "[]"; public string Body { get; set; } = string.Empty; public string ContentType { get; set; } = "application/json"; public string AuthType { get; set; } = "None"; public string? AuthToken { get; set; } public string? AuthUsername { get; set; } public string? AuthPassword { get; set; } public int SortOrder { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class EnvironmentEntity { public Guid Id { get; set; } = Guid.NewGuid(); public required string Name { get; set; } public string? Description { get; set; } public string Color { get; set; } = "#4ADE80"; public bool IsActive { get; set; } public int SortOrder { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class EnvironmentVariableEntity { public Guid Id { get; set; } = Guid.NewGuid(); public Guid EnvironmentId { get; set; } public required string Key { get; set; } public string? Value { get; set; } public string? Description { get; set; } public bool IsSecret { get; set; } public bool IsEnabled { get; set; } = true; public string? SecretStoreKey { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
