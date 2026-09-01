using DeveloperBrowser.Core.History;
using Microsoft.EntityFrameworkCore;

namespace DeveloperBrowser.Infrastructure.History;

public sealed class SqliteBrowsingHistoryService(IDbContextFactory<Persistence.DeveloperBrowserDbContext> factory) : IBrowsingHistoryService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task RecordAsync(string title, string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        await using var db = await OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var item = await db.Database.SqlQueryRaw<HistoryRow>("SELECT \"Id\", \"Title\", \"Url\", \"LastVisitedAt\", \"VisitCount\" FROM \"BrowsingHistory\" WHERE \"Url\" = {0} LIMIT 1", uri.AbsoluteUri).FirstOrDefaultAsync(cancellationToken);
        if (item is null)
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"BrowsingHistory\" (\"Id\", \"Title\", \"Url\", \"LastVisitedAt\", \"VisitCount\") VALUES ({0}, {1}, {2}, {3}, {4})", [Guid.NewGuid().ToString(), string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim(), uri.AbsoluteUri, now.ToString("O"), 1], cancellationToken);
        else
            await db.Database.ExecuteSqlRawAsync("UPDATE \"BrowsingHistory\" SET \"Title\" = {0}, \"LastVisitedAt\" = {1}, \"VisitCount\" = {2} WHERE \"Id\" = {3}", [string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim(), now.ToString("O"), item.VisitCount + 1, item.Id], cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"BrowsingHistory\" WHERE \"LastVisitedAt\" < {0}", [now.Subtract(Retention).ToString("O")], cancellationToken);
    }

    public async Task<IReadOnlyList<BrowsingHistoryItem>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        var cutoff = DateTimeOffset.UtcNow.Subtract(Retention).ToString("O");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"BrowsingHistory\" WHERE \"LastVisitedAt\" < {0}", [cutoff], cancellationToken);
        var rows = await db.Database.SqlQueryRaw<HistoryRow>("SELECT \"Id\", \"Title\", \"Url\", \"LastVisitedAt\", \"VisitCount\" FROM \"BrowsingHistory\" ORDER BY \"LastVisitedAt\" DESC").ToListAsync(cancellationToken);
        return rows.Select(row => new BrowsingHistoryItem(Guid.Parse(row.Id), row.Title, row.Url, DateTimeOffset.Parse(row.LastVisitedAt), row.VisitCount)).ToList();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"BrowsingHistory\"", cancellationToken);
    }

    private async Task<Persistence.DeveloperBrowserDbContext> OpenAsync(CancellationToken ct)
    {
        var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"BrowsingHistory\" (\"Id\" TEXT NOT NULL PRIMARY KEY, \"Title\" TEXT NOT NULL, \"Url\" TEXT NOT NULL, \"LastVisitedAt\" TEXT NOT NULL, \"VisitCount\" INTEGER NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_BrowsingHistory_Url\" ON \"BrowsingHistory\" (\"Url\")", ct);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_BrowsingHistory_LastVisitedAt\" ON \"BrowsingHistory\" (\"LastVisitedAt\")", ct);
        return db;
    }

    private sealed class HistoryRow { public string Id { get; set; } = string.Empty; public string Title { get; set; } = string.Empty; public string Url { get; set; } = string.Empty; public string LastVisitedAt { get; set; } = string.Empty; public int VisitCount { get; set; } }
}
