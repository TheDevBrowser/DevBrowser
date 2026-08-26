using Microsoft.EntityFrameworkCore;
namespace DeveloperBrowser.Infrastructure.Persistence;
public sealed class DeveloperBrowserDbContext(DbContextOptions<DeveloperBrowserDbContext> options) : DbContext(options)
{
    public DbSet<RestHistoryEntry> RestHistory => Set<RestHistoryEntry>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<RestHistoryEntry>(entity => { entity.HasKey(item => item.Id); entity.Property(item => item.Method).HasMaxLength(10).IsRequired(); entity.Property(item => item.Url).IsRequired(); });
}
public sealed class RestHistoryEntry { public Guid Id { get; init; } = Guid.NewGuid(); public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow; public required string Method { get; init; } public required string Url { get; init; } public int? StatusCode { get; init; } }
