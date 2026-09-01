namespace DeveloperBrowser.Core.History;

public sealed record BrowsingHistoryItem(Guid Id, string Title, string Url, DateTimeOffset LastVisitedAt, int VisitCount);

public interface IBrowsingHistoryService
{
    Task RecordAsync(string title, string url, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BrowsingHistoryItem>> GetRecentAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
