using System.Windows;
using System.Windows.Controls;

namespace DeveloperBrowser.App;

public partial class KeyValueDataViewer : UserControl
{
    public KeyValueDataViewer() => InitializeComponent();

    public void SetItems(IEnumerable<KeyValuePair<string, string>>? items, string emptyMessage = "No values available")
    {
        var rows = items?.Select(item => new KeyValueRow(item.Key, item.Value)).ToList() ?? [];
        ItemsList.ItemsSource = rows;
        ItemsList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = emptyMessage;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record KeyValueRow(string Name, string Value);
}
