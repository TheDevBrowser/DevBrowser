# Bookmark folders

Bookmarks support nested folders. Existing folders remain at the top level when
the app upgrades its local database; their bookmarks and notes are preserved.

- In **Bookmarks**, select a folder and use **New folder** to create a subfolder.
  Select **All bookmarks** first to create a top-level folder.
- Expand or collapse branches in the folder tree. Selecting a folder includes
  bookmarks in its descendants; the count includes those bookmarks too.
- When saving a page, the folder picker shows full paths, such as
  **Work / Docs / API**. Use **+ Folder** to choose a parent and create a folder.
  Choose **Top level** to create a root folder.
- To move a bookmark, choose **Edit**, select its destination folder, and save.
- Folder names may repeat under different parents. Creating the same name under
  the same parent selects the existing folder.
- Search also matches folder paths.

## Verification

Run the isolated in-memory SQLite checks with:

```powershell
dotnet run --project tests/DeveloperBrowser.BookmarkChecks/DeveloperBrowser.BookmarkChecks.csproj
```

The checks cover fresh databases, upgrading the original flat schema, repeated
initialization, nested paths, sibling names, invalid parents, and saving/moving
bookmarks while preserving their identity and notes.
