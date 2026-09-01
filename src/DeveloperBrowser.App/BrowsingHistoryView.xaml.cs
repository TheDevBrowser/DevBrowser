using System.Windows;
using System.Windows.Controls;
using DeveloperBrowser.Core.History;

namespace DeveloperBrowser.App;

public partial class BrowsingHistoryView : UserControl
{
    private readonly IBrowsingHistoryService _history;
    public event EventHandler<BrowsingHistoryItem>? OpenRequested;

    public BrowsingHistoryView(IBrowsingHistoryService history)
    {
        _history = history;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        var items = await _history.GetRecentAsync();
        HistoryList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Clear all browsing history from this device?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _history.ClearAsync();
        await RefreshAsync();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not BrowsingHistoryItem item) return;
        OpenRequested?.Invoke(this, item);
        HistoryList.SelectedItem = null;
    }
}
