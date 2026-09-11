using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DeveloperBrowser.Infrastructure.Bookmarks;

public sealed class SqliteBookmarkRepository(IDbContextFactory<DeveloperBrowserDbContext> factory) : IBookmarkRepository
{
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "BookmarkFolders" (
              "Id" TEXT NOT NULL CONSTRAINT "PK_BookmarkFolders" PRIMARY KEY,
              "Name" TEXT NOT NULL,
              "CreatedAt" TEXT NOT NULL,
              "IsDefault" INTEGER NOT NULL,
              "ParentFolderId" TEXT NULL REFERENCES "BookmarkFolders" ("Id") ON DELETE RESTRICT
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Bookmarks" (
              "Id" TEXT NOT NULL CONSTRAINT "PK_Bookmarks" PRIMARY KEY,
              "FolderId" TEXT NOT NULL,
              "Title" TEXT NOT NULL,
              "Url" TEXT NOT NULL,
              "Description" TEXT NULL,
              "Note" TEXT NULL,
              "FaviconUrl" TEXT NULL,
              "CreatedAt" TEXT NOT NULL,
              "UpdatedAt" TEXT NOT NULL,
              CONSTRAINT "FK_Bookmarks_BookmarkFolders_FolderId" FOREIGN KEY ("FolderId") REFERENCES "BookmarkFolders" ("Id") ON DELETE RESTRICT
            );
            """, cancellationToken);
        // Upgrade the existing flat schema without replacing folders or bookmarks.
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var hasParent = false;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "PRAGMA table_info('BookmarkFolders')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetString(1) == "ParentFolderId") hasParent = true;
        }
        if (!hasParent)
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "BookmarkFolders" ADD COLUMN "ParentFolderId" TEXT NULL REFERENCES "BookmarkFolders" ("Id") ON DELETE RESTRICT;""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""DROP INDEX IF EXISTS "IX_BookmarkFolders_Name";""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookmarkFolders_Parent_Name" ON "BookmarkFolders" ("ParentFolderId", "Name" COLLATE NOCASE) WHERE "ParentFolderId" IS NOT NULL;""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookmarkFolders_Root_Name" ON "BookmarkFolders" ("Name" COLLATE NOCASE) WHERE "ParentFolderId" IS NULL;""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_Bookmarks_Url" ON "Bookmarks" ("Url");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_Bookmarks_FolderId" ON "Bookmarks" ("FolderId");""", cancellationToken);
        if (!await db.BookmarkFolders.AnyAsync(folder => folder.IsDefault, cancellationToken))
        {
            db.BookmarkFolders.Add(new BookmarkFolderEntity { Name = "Default", IsDefault = true });
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BookmarkFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BookmarkFolders.AsNoTracking().OrderByDescending(folder => folder.IsDefault).ThenBy(folder => folder.Name)
            .Select(folder => new BookmarkFolder(folder.Id, folder.Name, folder.CreatedAt, folder.IsDefault, folder.ParentFolderId)).ToListAsync(cancellationToken);
    }

    public async Task<BookmarkFolder> AddFolderAsync(string name, CancellationToken cancellationToken = default, Guid? parentFolderId = null)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = new BookmarkFolderEntity { Name = name, IsDefault = false, ParentFolderId = parentFolderId };
        db.BookmarkFolders.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new BookmarkFolder(entity.Id, entity.Name, entity.CreatedAt, entity.IsDefault, entity.ParentFolderId);
    }

    public async Task<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // SQLite cannot translate DateTimeOffset sorting. Materialize the small local
        // bookmark collection first, then order it consistently in memory.
        var bookmarks = await db.Bookmarks.AsNoTracking().ToListAsync(cancellationToken);
        return bookmarks.OrderByDescending(bookmark => bookmark.UpdatedAt)
            .Select(ToDomain)
            .ToList();
    }

    public async Task<BookmarkItem?> FindByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Bookmarks.AsNoTracking().FirstOrDefaultAsync(bookmark => bookmark.Url == url, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<BookmarkItem> SaveAsync(BookmarkItem bookmark, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Bookmarks.FirstOrDefaultAsync(item => item.Id == bookmark.Id, cancellationToken)
                     ?? await db.Bookmarks.FirstOrDefaultAsync(item => item.Url == bookmark.Url, cancellationToken);
        if (entity is null)
        {
            entity = ToEntity(bookmark);
            db.Bookmarks.Add(entity);
        }
        else
        {
            entity.FolderId = bookmark.FolderId; entity.Title = bookmark.Title; entity.Url = bookmark.Url;
            entity.Description = bookmark.Description; entity.Note = bookmark.Note; entity.FaviconUrl = bookmark.FaviconUrl;
            entity.UpdatedAt = bookmark.UpdatedAt;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToDomain(entity);
    }

    public async Task DeleteAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Bookmarks.FindAsync([bookmarkId], cancellationToken);
        if (entity is null) return;
        db.Bookmarks.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static BookmarkItem ToDomain(BookmarkEntity item) => new(item.Id, item.FolderId, item.Title, item.Url, item.Description, item.Note, item.FaviconUrl, item.CreatedAt, item.UpdatedAt);
    private static BookmarkEntity ToEntity(BookmarkItem item) => new() { Id = item.Id, FolderId = item.FolderId, Title = item.Title, Url = item.Url, Description = item.Description, Note = item.Note, FaviconUrl = item.FaviconUrl, CreatedAt = item.CreatedAt, UpdatedAt = item.UpdatedAt };
}
