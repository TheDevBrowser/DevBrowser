namespace DeveloperBrowser.Core.Bookmarks;

public static class BookmarkFolderHierarchy
{
    public static IReadOnlyList<BookmarkFolder> Order(IReadOnlyList<BookmarkFolder> folders)
    {
        var result = new List<BookmarkFolder>();
        var visited = new HashSet<Guid>();
        var ids = folders.Select(folder => folder.Id).ToHashSet();
        var children = folders.Where(folder => folder.ParentFolderId.HasValue)
            .ToLookup(folder => folder.ParentFolderId!.Value);
        void Visit(BookmarkFolder folder, string? parentPath, int depth)
        {
            if (!visited.Add(folder.Id)) return;
            var path = parentPath is null ? folder.Name : $"{parentPath} / {folder.Name}";
            result.Add(folder with { Path = path, Depth = depth });
            foreach (var child in children[folder.Id].OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                Visit(child, path, depth + 1);
        }
        foreach (var root in folders.Where(f => f.ParentFolderId is null || !ids.Contains(f.ParentFolderId.Value))
                     .OrderByDescending(f => f.IsDefault).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            Visit(root, null, 0);
        // Keep malformed legacy data visible without looping on cycles.
        foreach (var folder in folders) Visit(folder, null, 0);
        return result;
    }

    public static HashSet<Guid> Descendants(IReadOnlyList<BookmarkFolder> folders, Guid root)
    {
        var result = new HashSet<Guid>();
        var children = folders.Where(f => f.ParentFolderId.HasValue).ToLookup(f => f.ParentFolderId!.Value);
        var pending = new Stack<Guid>();
        pending.Push(root);
        while (pending.TryPop(out var id))
        {
            if (!result.Add(id)) continue;
            foreach (var child in children[id]) pending.Push(child.Id);
        }
        return result;
    }
}
