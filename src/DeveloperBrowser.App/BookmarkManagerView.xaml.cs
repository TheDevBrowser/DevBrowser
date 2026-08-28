using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeveloperBrowser.Core.Bookmarks;

namespace DeveloperBrowser.App;

public partial class BookmarkManagerView : UserControl
{
    private readonly IBookmarkService _service;
    private IReadOnlyList<BookmarkItem> _allBookmarks = [];
    private IReadOnlyList<BookmarkFolder> _folders = [];
    private Guid? _selectedFolderId;

    public event EventHandler<BookmarkItem>? OpenRequested;
    public event EventHandler<BookmarkItem>? OpenNewTabRequested;
    public event EventHandler<BookmarkItem>? EditRequested;
    public event EventHandler? Deleted;

    public BookmarkManagerView(IBookmarkService service)
    {
        _service = service;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        _folders = await _service.GetFoldersAsync();
        _allBookmarks = await _service.GetBookmarksAsync();
        var folderChoices = new List<BookmarkFolderChoice> { new(null, "All bookmarks", _allBookmarks.Count) };
        folderChoices.AddRange(_folders.Select(folder => new BookmarkFolderChoice(folder.Id, folder.Name, _allBookmarks.Count(bookmark => bookmark.FolderId == folder.Id))));
        FolderList.ItemsSource = folderChoices;
        FolderList.SelectedItem = folderChoices.FirstOrDefault(choice => choice.Id == _selectedFolderId) ?? folderChoices.FirstOrDefault();
        ApplyFilter();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFolderId = (FolderList.SelectedItem as BookmarkFolderChoice)?.Id;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        var folders = _folders.ToDictionary(folder => folder.Id, folder => folder.Name);
        var filtered = _allBookmarks.Where(bookmark =>
            (_selectedFolderId is null || bookmark.FolderId == _selectedFolderId) &&
            (string.IsNullOrWhiteSpace(search) ||
             bookmark.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             bookmark.Url.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (bookmark.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (bookmark.Note?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (folders.GetValueOrDefault(bookmark.FolderId)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToArray();
        BookmarkList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BookmarkList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarkList.SelectedItem is BookmarkItem bookmark) OpenRequested?.Invoke(this, bookmark);
    }
    private void BookmarkList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (e.OriginalSource as FrameworkElement)?.DataContext is BookmarkItem bookmark)
        {
            OpenNewTabRequested?.Invoke(this, bookmark);
            e.Handled = true;
        }
    }
    private void Open_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is BookmarkItem bookmark) OpenRequested?.Invoke(this, bookmark); }
    private void OpenNewTab_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is BookmarkItem bookmark) OpenNewTabRequested?.Invoke(this, bookmark); }
    private void Edit_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is BookmarkItem bookmark) EditRequested?.Invoke(this, bookmark); }
    private void Copy_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is BookmarkItem bookmark) Clipboard.SetText(bookmark.Url); }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BookmarkItem bookmark) return;
        await _service.DeleteAsync(bookmark.Id);
        await RefreshAsync();
        Deleted?.Invoke(this, EventArgs.Empty);
    }

    private sealed record BookmarkFolderChoice(Guid? Id, string Name, int Count);
}
