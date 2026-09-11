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
        var all = new BookmarkFolderChoice(null, "All bookmarks", _allBookmarks.Count, "All bookmarks");
        var nodes = _folders.ToDictionary(folder => folder.Id, folder =>
        {
            var ids = BookmarkFolderHierarchy.Descendants(_folders, folder.Id);
            return new BookmarkFolderChoice(folder.Id, folder.Name, _allBookmarks.Count(b => ids.Contains(b.FolderId)), folder.Path);
        });
        var roots = new List<BookmarkFolderChoice> { all };
        foreach (var folder in _folders)
        {
            var node = nodes[folder.Id];
            if (folder.ParentFolderId is { } parent && nodes.TryGetValue(parent, out var parentNode))
                parentNode.Children.Add(node);
            else roots.Add(node);
        }
        var selected = _selectedFolderId is { } id && nodes.TryGetValue(id, out var match) ? match : all;
        selected.IsSelected = true;
        FolderList.ItemsSource = roots;
        ApplyFilter();
    }

    private void FolderList_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedFolderId = (e.NewValue as BookmarkFolderChoice)?.Id;
        if (NewFolderLocationText is not null)
            NewFolderLocationText.Text = _selectedFolderId is { } id ? $"Inside: {_folders.FirstOrDefault(f => f.Id == id)?.Path}" : "At top level";
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        var folders = _folders.ToDictionary(folder => folder.Id, folder => folder.Path);
        var included = _selectedFolderId is { } id ? BookmarkFolderHierarchy.Descendants(_folders, id) : null;
        var filtered = _allBookmarks.Where(bookmark =>
            (included is null || included.Contains(bookmark.FolderId)) &&
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

    private async void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await _service.CreateFolderAsync(NewFolderNameBox.Text, parentFolderId: _selectedFolderId);
            _selectedFolderId = folder.Id;
            NewFolderNameBox.Clear();
            NewFolderErrorText.Text = string.Empty;
            await RefreshAsync();
        }
        catch (ArgumentException exception) { NewFolderErrorText.Text = exception.Message; }
    }

    private sealed record BookmarkFolderChoice(Guid? Id, string Name, int Count, string Path)
    {
        public List<BookmarkFolderChoice> Children { get; } = [];
        public bool IsSelected { get; set; }
    }
}
