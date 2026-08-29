using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeveloperBrowser.App.Storage;
using DeveloperBrowser.Core.Storage;
using Microsoft.Web.WebView2.Wpf;

namespace DeveloperBrowser.App;

public partial class StorageInspectorView : UserControl
{
    private readonly BrowserStorageService _storage = new();
    private readonly List<StorageItemView> _items = [];
    private BrowserStorageSnapshot _snapshot = BrowserStorageSnapshot.Empty("No inspectable origin");
    private WebView2? _browser;
    private StorageItemView? _editing;
    private int _refreshVersion;
    private bool _isViewReady;

    public event EventHandler<string>? JwtInspectionRequested;

    public StorageInspectorView()
    {
        InitializeComponent();
        _isViewReady = true;
    }

    public async Task SetBrowserAsync(WebView2? browser)
    {
        _browser = browser;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        EditPanel.Visibility = Visibility.Collapsed;
        StorageStatusText.Text = "Refreshing…";
        if (_browser is null)
        {
            ApplySnapshot(BrowserStorageSnapshot.Empty("Select a browser tab to inspect storage"));
            StorageStatusText.Text = string.Empty;
            return;
        }

        var browser = _browser;
        var snapshot = await _storage.ReadAsync(browser);
        if (version != _refreshVersion || !ReferenceEquals(_browser, browser)) return;
        ApplySnapshot(snapshot);
        StorageStatusText.Text = "Updated just now";
    }

    private void ApplySnapshot(BrowserStorageSnapshot snapshot)
    {
        _snapshot = snapshot;
        CurrentOriginText.Text = snapshot.Origin;
        _items.Clear();
        _items.AddRange(snapshot.AllItems.Select(item => new StorageItemView(item, StorageValueAnalyzer.Analyze(item.Key, item.Value))));
        foreach (var group in _items.Where(item => !string.IsNullOrWhiteSpace(item.Item.Value)).GroupBy(item => item.Item.Value).Where(group => group.Count() > 1))
            foreach (var item in group) item.DuplicateCount = group.Count();

        CookieCountText.Text = snapshot.Cookies.Count.ToString("N0");
        LocalCountText.Text = snapshot.LocalStorage.Count.ToString("N0");
        SessionCountText.Text = snapshot.SessionStorage.Count.ToString("N0");
        WarningCountText.Text = _items.Count(item => item.IsWarning).ToString("N0");
        DetectedText.Text = $"Detected {_items.Count(item => item.IsJwt):N0} JWT token{(_items.Count(item => item.IsJwt) == 1 ? string.Empty : "s")} • {_items.Count(item => item.IsJson):N0} JSON value{(_items.Count(item => item.IsJson) == 1 ? string.Empty : "s")}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // SelectionChanged can be raised while this control's XAML is still
        // constructing.  Wait until every named control is available.
        if (!_isViewReady) return;

        var cookies = Filter(_items.Where(item => item.Item.Kind == BrowserStorageKind.Cookie)).ToList();
        var local = Filter(_items.Where(item => item.Item.Kind == BrowserStorageKind.LocalStorage)).ToList();
        var session = Filter(_items.Where(item => item.Item.Kind == BrowserStorageKind.SessionStorage)).ToList();
        CookieList.ItemsSource = cookies;
        LocalStorageList.ItemsSource = local;
        SessionStorageList.ItemsSource = session;
        CookieEmptyText.Visibility = cookies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalEmptyText.Visibility = local.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionEmptyText.Visibility = session.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var allResults = Filter(_items).ToList();
        var auth = allResults.Where(item => item.IsAuthenticationRelated).Take(4).ToList();
        AuthenticationList.ItemsSource = auth;
        AuthenticationHeading.Visibility = auth.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var hasActiveFilter = !string.IsNullOrWhiteSpace(SearchBox.Text) || TypeFilterBox.SelectedIndex != 0;
        OverviewList.ItemsSource = (hasActiveFilter ? allResults : _items.Where(item => item.IsWarning || item.IsJwt || item.IsJson)).Take(6).ToList();
    }

    private IEnumerable<StorageItemView> Filter(IEnumerable<StorageItemView> source)
    {
        var search = SearchBox.Text.Trim();
        var filtered = source.Where(item => string.IsNullOrWhiteSpace(search) || item.Key.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Item.Value.Contains(search, StringComparison.OrdinalIgnoreCase));
        return TypeFilterBox.SelectedIndex switch
        {
            1 => filtered.Where(item => item.IsJwt),
            2 => filtered.Where(item => item.IsJson),
            3 => filtered.Where(item => item.IsWarning),
            _ => filtered
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void TypeFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void StorageTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == StorageTabs) EditPanel.Visibility = Visibility.Collapsed; }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StorageItemView item) Clipboard.SetText(item.Item.Value);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StorageItemView item) return;
        _editing = item;
        EditKeyText.Text = $"Edit {item.SourceLabel}: {item.Key}";
        EditValueBox.Text = item.Item.Value;
        EditPanel.Visibility = Visibility.Visible;
        EditValueBox.Focus();
    }

    private async void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _browser is null) return;
        try
        {
            await _storage.SetValueAsync(_browser, _editing.Item, EditValueBox.Text);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StorageStatusText.Text = $"Could not save value: {exception.GetType().Name}";
        }
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => EditPanel.Visibility = Visibility.Collapsed;

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StorageItemView item || _browser is null) return;
        try
        {
            await _storage.DeleteAsync(_browser, item.Item);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StorageStatusText.Text = $"Could not delete value: {exception.GetType().Name}";
        }
    }

    private void ViewJson_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StorageItemView item) return;
        ValueTab.Visibility = Visibility.Visible;
        StorageDataViewer.SetContent(item.Item.Value, "application/json");
        StorageTabs.SelectedItem = ValueTab;
    }

    private void InspectJwt_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is StorageItemView { IsJwt: true } item)
            JwtInspectionRequested?.Invoke(this, item.Item.Value);
    }

    private async void ClearCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_browser is null) return;
        var kind = StorageTabs.SelectedIndex switch
        {
            1 => BrowserStorageKind.Cookie,
            2 => BrowserStorageKind.LocalStorage,
            3 => BrowserStorageKind.SessionStorage,
            _ => (BrowserStorageKind?)null
        };
        if (kind is null)
        {
            StorageStatusText.Text = "Choose Cookies, Local Storage, or Session Storage to clear it.";
            return;
        }

        var label = kind.Value switch { BrowserStorageKind.Cookie => "cookies", BrowserStorageKind.LocalStorage => "Local Storage", _ => "Session Storage" };
        if (MessageBox.Show($"Clear all {label} for {_snapshot.Origin}? This cannot be undone.", "Clear storage", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _storage.ClearAsync(_browser, _snapshot, kind.Value);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StorageStatusText.Text = $"Could not clear {label}: {exception.GetType().Name}";
        }
    }
}
