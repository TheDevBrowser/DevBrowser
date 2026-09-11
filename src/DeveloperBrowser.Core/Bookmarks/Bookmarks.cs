namespace DeveloperBrowser.Core.Bookmarks;

public sealed record BookmarkFolder(Guid Id, string Name, DateTimeOffset CreatedAt, bool IsDefault, Guid? ParentFolderId = null)
{
    public string Path { get; init; } = Name;
    public int Depth { get; init; }
}

public sealed record BookmarkItem(
    Guid Id,
    Guid FolderId,
    string Title,
    string Url,
    string? Description,
    string? Note,
    string? FaviconUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BookmarkDraft(
    Guid? Id,
    Guid? FolderId,
    string Title,
    string Url,
    string? Description,
    string? Note,
    string? FaviconUrl);

public interface IBookmarkRepository
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BookmarkFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<BookmarkFolder> AddFolderAsync(string name, CancellationToken cancellationToken = default, Guid? parentFolderId = null);
    Task<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default);
    Task<BookmarkItem?> FindByUrlAsync(string url, CancellationToken cancellationToken = default);
    Task<BookmarkItem> SaveAsync(BookmarkItem bookmark, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid bookmarkId, CancellationToken cancellationToken = default);
}

public interface IBookmarkService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BookmarkFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<BookmarkFolder> CreateFolderAsync(string name, CancellationToken cancellationToken = default, Guid? parentFolderId = null);
    Task<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default);
    Task<BookmarkItem?> FindByUrlAsync(string url, CancellationToken cancellationToken = default);
    Task<BookmarkItem> SaveAsync(BookmarkDraft draft, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid bookmarkId, CancellationToken cancellationToken = default);
}

public sealed class BookmarkService(IBookmarkRepository repository) : IBookmarkService
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await repository.EnsureCreatedAsync(cancellationToken);
        var folders = await repository.GetFoldersAsync(cancellationToken);
        if (!folders.Any(folder => folder.IsDefault || folder.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)))
            await repository.AddFolderAsync("Default", cancellationToken);
    }

    public async Task<IReadOnlyList<BookmarkFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        BookmarkFolderHierarchy.Order(await repository.GetFoldersAsync(cancellationToken));
    public async Task<BookmarkFolder> CreateFolderAsync(string name, CancellationToken cancellationToken = default, Guid? parentFolderId = null)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Folder name is required.", nameof(name));
        var existing = await repository.GetFoldersAsync(cancellationToken);
        if (parentFolderId is { } parent && !existing.Any(folder => folder.Id == parent))
            throw new ArgumentException("The parent folder no longer exists.", nameof(parentFolderId));
        var match = existing.FirstOrDefault(folder => folder.ParentFolderId == parentFolderId && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match ?? await repository.AddFolderAsync(name, cancellationToken, parentFolderId);
    }
    public Task<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default) => repository.GetBookmarksAsync(cancellationToken);
    public Task<BookmarkItem?> FindByUrlAsync(string url, CancellationToken cancellationToken = default) => repository.FindByUrlAsync(url, cancellationToken);

    public async Task<BookmarkItem> SaveAsync(BookmarkDraft draft, CancellationToken cancellationToken = default)
    {
        var url = draft.Url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new ArgumentException("A complete bookmark URL is required.", nameof(draft));
        var folders = await repository.GetFoldersAsync(cancellationToken);
        var defaultFolder = folders.FirstOrDefault(folder => folder.IsDefault || folder.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("The Default folder is unavailable.");
        var folderId = draft.FolderId is { } selected && folders.Any(folder => folder.Id == selected) ? selected : defaultFolder.Id;
        var existing = draft.Id is null ? await repository.FindByUrlAsync(url, cancellationToken) : null;
        var now = DateTimeOffset.UtcNow;
        var bookmark = new BookmarkItem(
            draft.Id ?? existing?.Id ?? Guid.NewGuid(),
            folderId,
            string.IsNullOrWhiteSpace(draft.Title) ? url : draft.Title.Trim(),
            url,
            EmptyToNull(draft.Description),
            EmptyToNull(draft.Note),
            EmptyToNull(draft.FaviconUrl),
            existing?.CreatedAt ?? now,
            now);
        return await repository.SaveAsync(bookmark, cancellationToken);
    }

    public Task DeleteAsync(Guid bookmarkId, CancellationToken cancellationToken = default) => repository.DeleteAsync(bookmarkId, cancellationToken);
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
