using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Infrastructure.Bookmarks;
using DeveloperBrowser.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Isolated in-memory databases exercise the real migration and repository.
foreach (var legacy in new[] { false, true })
{
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    var originalFolder = Guid.NewGuid();
    var originalBookmark = Guid.NewGuid();
    if (legacy)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE BookmarkFolders (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, IsDefault INTEGER NOT NULL);
            CREATE UNIQUE INDEX IX_BookmarkFolders_Name ON BookmarkFolders(Name);
            CREATE TABLE Bookmarks (Id TEXT NOT NULL PRIMARY KEY, FolderId TEXT NOT NULL, Title TEXT NOT NULL, Url TEXT NOT NULL,
                Description TEXT NULL, Note TEXT NULL, FaviconUrl TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (FolderId) REFERENCES BookmarkFolders(Id));
            INSERT INTO BookmarkFolders VALUES ($folder, 'Default', $date, 1);
            INSERT INTO Bookmarks VALUES ($bookmark, $folder, 'Existing bookmark', 'https://example.com/old', NULL, 'Keep this note', NULL, $date, $date);
            """;
        command.Parameters.AddWithValue("$folder", originalFolder);
        command.Parameters.AddWithValue("$bookmark", originalBookmark);
        command.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }
    var factory = new TestFactory(new DbContextOptionsBuilder<DeveloperBrowserDbContext>().UseSqlite(connection).Options);
    var repository = new SqliteBookmarkRepository(factory);
    var service = new BookmarkService(repository);
    await service.InitializeAsync();
    await service.InitializeAsync();
    if (legacy)
    {
        var saved = (await service.GetBookmarksAsync()).Single();
        Check(saved.Id == originalBookmark && saved.FolderId == originalFolder && saved.Note == "Keep this note", "Migration preserves existing bookmarks");
    }
    var work = await service.CreateFolderAsync("Work");
    var personal = await service.CreateFolderAsync("Personal");
    var workDocs = await service.CreateFolderAsync("Docs", parentFolderId: work.Id);
    var personalDocs = await service.CreateFolderAsync("Docs", parentFolderId: personal.Id);
    var api = await service.CreateFolderAsync("API", parentFolderId: workDocs.Id);
    Check(workDocs.Id != personalDocs.Id, "Same name is allowed under different parents");
    Check((await service.CreateFolderAsync(" docs ", parentFolderId: work.Id)).Id == workDocs.Id, "Duplicate siblings reuse the existing folder");
    var folders = await service.GetFoldersAsync();
    Check(folders.Single(f => f.Id == api.Id).Path == "Work / Docs / API", "Full nested path");
    Check(folders.Single(f => f.Id == api.Id).Depth == 2, "Nested depth");
    Check(BookmarkFolderHierarchy.Descendants(folders, work.Id).SetEquals(new[] { work.Id, workDocs.Id, api.Id }), "Subtree excludes other parents");
    try
    {
        await service.CreateFolderAsync("Invalid", parentFolderId: Guid.NewGuid());
        throw new Exception("Missing parent was accepted");
    }
    catch (ArgumentException) { }
    try
    {
        await service.CreateFolderAsync("   ", parentFolderId: work.Id);
        throw new Exception("Empty folder name was accepted");
    }
    catch (ArgumentException) { }
    var bookmark = await service.SaveAsync(new(null, api.Id, "API reference", "https://example.com/api", null, "Retain notes", null));
    Check(bookmark.FolderId == api.Id, "Save into nested folder");
    var moved = await service.SaveAsync(new(bookmark.Id, personalDocs.Id, bookmark.Title, bookmark.Url, null, bookmark.Note, null));
    Check(moved.Id == bookmark.Id && moved.FolderId == personalDocs.Id && moved.Note == bookmark.Note, "Move preserves bookmark identity and notes");
    await service.InitializeAsync();
    Check((await service.GetFoldersAsync()).Single(f => f.Id == api.Id).ParentFolderId == workDocs.Id, "Hierarchy survives repeated initialization");
    Console.WriteLine(legacy ? "PASS: legacy migration and nested folder operations" : "PASS: new database and nested folder operations");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class TestFactory(DbContextOptions<DeveloperBrowserDbContext> options) : IDbContextFactory<DeveloperBrowserDbContext>
{
    public DeveloperBrowserDbContext CreateDbContext() => new(options);
}
