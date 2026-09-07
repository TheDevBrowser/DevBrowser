using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.IO;
using DeveloperBrowser.Core.Browser;
using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Core.Collections;
using DeveloperBrowser.Core.History;
using DeveloperBrowser.Core.Updates;
using Microsoft.Web.WebView2.Wpf;
using DeveloperBrowser.App.Diagnostics;
using System.Diagnostics;

namespace DeveloperBrowser.App;

public partial class MainWindow : Window
{
    private readonly List<BrowserTab> _tabs = [];
    private readonly NetworkCaptureService _networkCapture = new();
    private readonly HarCaptureService _harCapture = new();
    private readonly IBookmarkService _bookmarks;
    private readonly IBrowsingHistoryService _history;
    private readonly PageMetadataService _pageMetadata;
    private readonly BookmarkManagerView _bookmarkManager;
    private readonly BrowsingHistoryView _historyView;
    private readonly IAppUpdateService _appUpdates;
    private readonly CrashReportingService _crashReporting;
    private readonly string _logDirectory;
    private CancellationTokenSource? _updateChecksCancellation;
    private BrowserTab? _activeTab;
    private double _networkDrawerHeight = 340;
    private double _networkSideWidth = 440;
    private NetworkInspectorDock _networkDock = NetworkInspectorDock.Bottom;
    private BookmarkItem? _editingBookmark;
    private FooterWorkspace _footerWorkspace = FooterWorkspace.Browser;

    public MainWindow(IBookmarkService bookmarks, IBrowsingHistoryService history, PageMetadataService pageMetadata, ICollectionService collections, IEnvironmentService environments, IVariableResolver variables, ICollectionImportExportService collectionImportExport, IAppUpdateService appUpdates, CrashReportingService crashReporting, string dataDirectory)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindowStyle.ApplyModernDarkChrome(this);
        _bookmarks = bookmarks;
        _history = history;
        _pageMetadata = pageMetadata;
        _appUpdates = appUpdates;
        _crashReporting = crashReporting;
        _logDirectory = Path.Combine(dataDirectory, "Logs");
        _appUpdates.StatusChanged += AppUpdates_StatusChanged;
        RestClientView.Configure(collections, environments, variables, collectionImportExport);
        _bookmarkManager = new BookmarkManagerView(_bookmarks);
        _bookmarkManager.OpenRequested += async (_, bookmark) => await OpenBookmarkAsync(bookmark.Url, false);
        _bookmarkManager.OpenNewTabRequested += async (_, bookmark) => await OpenBookmarkAsync(bookmark.Url, true);
        _bookmarkManager.EditRequested += async (_, bookmark) => await ShowBookmarkFlyoutAsync(bookmark);
        _bookmarkManager.Deleted += async (_, _) => { await UpdateBookmarkStateAsync(); };
        BookmarkManagerHost.Content = _bookmarkManager;
        _historyView = new BrowsingHistoryView(_history);
        _historyView.OpenRequested += async (_, item) => await OpenBookmarkAsync(item.Url, false);
        BrowsingHistoryHost.Content = _historyView;
        NetworkInspector.Initialize(_networkCapture);
        NetworkInspector.SetDock(_networkDock);
        HarInspector.Initialize(_harCapture, () =>
        {
            if (_activeTab?.Browser.CoreWebView2 is not { } webView) return null;
            var host = _activeTab.Browser.Source?.Host ?? "New tab";
            var title = string.IsNullOrWhiteSpace(_activeTab.Title.Text) ? host : _activeTab.Title.Text;
            return new HarCaptureTarget(webView, $"{title} — {host}");
        });
        _harCapture.Changed += (_, _) => UpdateFooterStatus();
        HarInspector.OpenInRestClientRequested += (_, request) =>
        {
            RestClientView.OpenCapturedRequest(request);
            ShowRestWorkspace_Click(this, new RoutedEventArgs());
        };
        _networkCapture.Requests.CollectionChanged += NetworkRequests_CollectionChanged;
        RestClientView.FooterStatusChanged += (_, status) =>
        {
            if (_footerWorkspace == FooterWorkspace.Rest) UpdateFooterStatus(status);
        };
        NetworkInspector.CloseRequested += (_, _) => HideNetworkInspector();
        NetworkInspector.DockRequested += (_, dock) => SetNetworkDock(dock);
        NetworkInspector.OpenInRestClientRequested += (_, request) =>
        {
            RestClientView.OpenCapturedRequest(request);
            ShowRestWorkspace_Click(this, new RoutedEventArgs());
        };
        StorageInspector.JwtInspectionRequested += (_, token) =>
        {
            ShowBrowserWorkspace_Click(this, new RoutedEventArgs());
            NetworkInspector.InspectJwtToken(token);
            ShowNetworkInspector();
        };
        Loaded += async (_, _) =>
        {
            await CreateTabAsync("https://www.google.com");
            await _bookmarkManager.RefreshAsync();
            await StartUpdateChecksAsync();
        };
        Closed += (_, _) =>
        {
            _updateChecksCancellation?.Cancel();
            _updateChecksCancellation?.Dispose();
            _appUpdates.StatusChanged -= AppUpdates_StatusChanged;
        };
        UpdateFooterStatus();
    }

    private async Task StartUpdateChecksAsync()
    {
        await _appUpdates.CheckForUpdatesAsync();
        _updateChecksCancellation = new CancellationTokenSource();
        _ = RunPeriodicUpdateChecksAsync(_updateChecksCancellation.Token);
    }

    private async Task RunPeriodicUpdateChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await _appUpdates.CheckForUpdatesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
    }

    private void AppUpdates_StatusChanged(object? sender, AppUpdateStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppUpdates_StatusChanged(sender, status));
            return;
        }

        FooterUpdateButton.Visibility = status.State is AppUpdateState.Available or AppUpdateState.Applying
            ? Visibility.Visible
            : Visibility.Collapsed;
        FooterUpdateButton.IsEnabled = status.State == AppUpdateState.Available;
        FooterUpdateButtonText.Text = status.State == AppUpdateState.Applying ? "Updating…" : "Relaunch to update";
        FooterUpdateButton.ToolTip = status.Message;
    }

    private async void FooterUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RestClientView.HasUnsavedRequestChanges && MessageBox.Show(
                "You have unsaved REST request edits. Relaunching will close DevBrowser to apply the update. Continue?",
                "Update DevBrowser",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _appUpdates.RelaunchToUpdateAsync();
    }

    private async Task<BrowserTab> CreateTabAsync(string? address = null, bool navigate = true)
    {
        var browser = new WebView2();
        var title = new TextBlock
        {
            Text = "New tab",
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var tab = new BrowserTab(browser, title);
        tab.Header = CreateTabHeader(tab);
        _tabs.Add(tab);
        TabStrip.Children.Add(tab.Header);

        browser.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess || browser.Source is null) return;
            title.Text = browser.Source.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            if (_activeTab == tab)
            {
                AddressBar.Text = browser.Source.AbsoluteUri;
                _ = UpdateBookmarkStateAsync();
                if (StorageWorkspace.Visibility == Visibility.Visible) _ = StorageInspector.SetBrowserAsync(browser);
            }
            _ = _history.RecordAsync(title.Text, browser.Source.AbsoluteUri);
        };

        SelectTab(tab);
        await browser.EnsureCoreWebView2Async();
        browser.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(browser.CoreWebView2.DocumentTitle)) title.Text = browser.CoreWebView2.DocumentTitle;
        };
        browser.CoreWebView2.NewWindowRequested += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var popupTab = await CreateTabAsync(navigate: false);
                args.NewWindow = popupTab.Browser.CoreWebView2;
                args.Handled = true;
                SelectTab(popupTab);
            }
            catch
            {
                // Keep a failed popup request inside DevBrowser rather than letting Chromium open a separate window.
                args.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BrowserInstrumentation.ConsoleForwarderScript);
        await _networkCapture.AttachAsync(browser.CoreWebView2);
        if (navigate) browser.Source = ToAddress(address ?? "https://www.google.com");
        return tab;
    }

    private Border CreateTabHeader(BrowserTab tab)
    {
        var header = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Margin = new Thickness(0, 7, 6, 0),
            Padding = new Thickness(11, 0, 6, 0),
            Height = 35,
            Cursor = Cursors.Hand
        };
        var layout = new StackPanel { Orientation = Orientation.Horizontal };
        layout.Children.Add(new TextBlock { Text = "●", Foreground = new SolidColorBrush(Color.FromRgb(97, 208, 149)), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        layout.Children.Add(tab.Title);
        var close = new Button
        {
            Content = "×",
            Style = (Style)FindResource("TabCloseButton"),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Close tab"
        };
        close.Click += (_, e) => { e.Handled = true; CloseTab(tab); };
        layout.Children.Add(close);
        header.Child = layout;
        header.MouseLeftButtonUp += (_, _) => SelectTab(tab);
        return header;
    }

    private void SelectTab(BrowserTab tab)
    {
        _activeTab = tab;
        BrowserHost.Content = tab.Browser;
        foreach (var item in _tabs)
            item.Header.Background = item == tab ? (Brush)FindResource("SurfaceBrush") : Brushes.Transparent;
        if (tab.Browser.Source is not null) AddressBar.Text = tab.Browser.Source.AbsoluteUri;
        _ = UpdateBookmarkStateAsync();
        if (StorageWorkspace.Visibility == Visibility.Visible) _ = StorageInspector.SetBrowserAsync(tab.Browser);
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.Header);
        tab.Browser.Dispose();

        if (_tabs.Count == 0)
        {
            _ = CreateTabAsync();
            return;
        }

        if (_activeTab == tab)
            SelectTab(_tabs[Math.Clamp(index - 1, 0, _tabs.Count - 1)]);
    }

    private void AddTab_Click(object sender, RoutedEventArgs e) => _ = CreateTabAsync();

    private void ShowBrowserWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Visible;
        RestWorkspace.Visibility = Visibility.Collapsed;
        BookmarksWorkspace.Visibility = Visibility.Collapsed;
        StorageWorkspace.Visibility = Visibility.Collapsed;
        HarWorkspace.Visibility = Visibility.Collapsed;
        HistoryWorkspace.Visibility = Visibility.Collapsed;
        _footerWorkspace = FooterWorkspace.Browser;
        UpdateFooterStatus();
    }

    private void ShowRestWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Collapsed;
        RestWorkspace.Visibility = Visibility.Visible;
        BookmarksWorkspace.Visibility = Visibility.Collapsed;
        StorageWorkspace.Visibility = Visibility.Collapsed;
        HarWorkspace.Visibility = Visibility.Collapsed;
        HistoryWorkspace.Visibility = Visibility.Collapsed;
        HideNetworkInspector();
        _footerWorkspace = FooterWorkspace.Rest;
        UpdateFooterStatus();
    }

    private async void ShowBookmarksWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Collapsed;
        RestWorkspace.Visibility = Visibility.Collapsed;
        BookmarksWorkspace.Visibility = Visibility.Visible;
        StorageWorkspace.Visibility = Visibility.Collapsed;
        HarWorkspace.Visibility = Visibility.Collapsed;
        HistoryWorkspace.Visibility = Visibility.Collapsed;
        HideNetworkInspector();
        _footerWorkspace = FooterWorkspace.Bookmarks;
        UpdateFooterStatus();
        await _bookmarkManager.RefreshAsync();
    }

    private async void ShowStorageWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Collapsed;
        RestWorkspace.Visibility = Visibility.Collapsed;
        BookmarksWorkspace.Visibility = Visibility.Collapsed;
        StorageWorkspace.Visibility = Visibility.Visible;
        HarWorkspace.Visibility = Visibility.Collapsed;
        HistoryWorkspace.Visibility = Visibility.Collapsed;
        HideNetworkInspector();
        _footerWorkspace = FooterWorkspace.Storage;
        UpdateFooterStatus();
        await StorageInspector.SetBrowserAsync(_activeTab?.Browser);
    }

    private void ShowHarWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Collapsed;
        RestWorkspace.Visibility = Visibility.Collapsed;
        BookmarksWorkspace.Visibility = Visibility.Collapsed;
        StorageWorkspace.Visibility = Visibility.Collapsed;
        HarWorkspace.Visibility = Visibility.Visible;
        HistoryWorkspace.Visibility = Visibility.Collapsed;
        HarInspector.RefreshActiveTab();
        HideNetworkInspector();
        _footerWorkspace = FooterWorkspace.Har;
        UpdateFooterStatus();
    }

    private async void ShowHistoryWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Collapsed;
        RestWorkspace.Visibility = Visibility.Collapsed;
        BookmarksWorkspace.Visibility = Visibility.Collapsed;
        StorageWorkspace.Visibility = Visibility.Collapsed;
        HarWorkspace.Visibility = Visibility.Collapsed;
        HistoryWorkspace.Visibility = Visibility.Visible;
        HideNetworkInspector();
        _footerWorkspace = FooterWorkspace.History;
        UpdateFooterStatus();
        await _historyView.RefreshAsync();
    }

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, Style = (Style)FindResource("MoreMenuStyle") };
        var bookmarks = new MenuItem { Header = "Bookmarks", Style = (Style)FindResource("MoreMenuItemStyle") };
        bookmarks.Click += (_, _) => ShowBookmarksWorkspace_Click(this, new RoutedEventArgs());
        var history = new MenuItem { Header = "History", Style = (Style)FindResource("MoreMenuItemStyle") };
        history.Click += (_, _) => ShowHistoryWorkspace_Click(this, new RoutedEventArgs());
        var crashReports = new MenuItem { Header = "Diagnostics settings…", Style = (Style)FindResource("MoreMenuItemStyle") };
        crashReports.Click += (_, _) =>
        {
            var dialog = new CrashReportingConsentDialog(_crashReporting.RecordedChoice) { Owner = this };
            if (dialog.ShowDialog() == true) _crashReporting.SetEnabled(dialog.ReportingEnabled);
        };
        var openLogs = new MenuItem { Header = "Open log folder", Style = (Style)FindResource("MoreMenuItemStyle") };
        openLogs.Click += (_, _) => Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        var about = new MenuItem { Header = "About DevBrowser", Style = (Style)FindResource("MoreMenuItemStyle") };
        about.Click += (_, _) => MessageBox.Show($"DevBrowser\nA developer-focused browser with built-in network, HAR, storage, and REST tooling.\n\nVersion {GetDisplayVersion()}", "About DevBrowser", MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(bookmarks); menu.Items.Add(history); menu.Items.Add(new Separator { Style = (Style)FindResource("MoreMenuSeparatorStyle") }); menu.Items.Add(crashReports); menu.Items.Add(openLogs); menu.Items.Add(new Separator { Style = (Style)FindResource("MoreMenuSeparatorStyle") }); menu.Items.Add(about);
        menu.IsOpen = true;
    }

    private static string GetDisplayVersion()
    {
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return string.IsNullOrWhiteSpace(version) ? "development build" : version.Split('+')[0];
    }

    private void ToggleNetworkInspector_Click(object sender, RoutedEventArgs e)
    {
        if (StorageWorkspace.Visibility == Visibility.Visible || HarWorkspace.Visibility == Visibility.Visible) ShowBrowserWorkspace_Click(this, new RoutedEventArgs());
        if (IsNetworkInspectorVisible) HideNetworkInspector();
        else ShowNetworkInspector();
    }

    private void ShowNetworkInspector()
    {
        if (_networkDock == NetworkInspectorDock.Right)
        {
            MoveNetworkInspectorTo(NetworkInspectorDock.Right);
            NetworkSideSplitterColumn.Width = new GridLength(8);
            NetworkSideColumn.Width = new GridLength(_networkSideWidth);
            Grid.SetColumnSpan(BrowserWorkspace, 1);
            NetworkSideSplitter.Width = 8;
            NetworkSideSplitter.Visibility = Visibility.Visible;
            NetworkInspectorSideHost.Visibility = Visibility.Visible;
            UpdateFooterStatus();
            return;
        }

        MoveNetworkInspectorTo(NetworkInspectorDock.Bottom);
        NetworkResizeRow.Height = new GridLength(8);
        NetworkDrawerRow.MinHeight = 180;
        NetworkDrawerRow.Height = new GridLength(_networkDrawerHeight);
        NetworkDrawerSplitter.Visibility = Visibility.Visible;
        NetworkInspectorHost.Visibility = Visibility.Visible;
        UpdateLayout();
        var height = Math.Max(180, NetworkDrawerRow.ActualHeight);
        NetworkInspectorTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(height, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        UpdateFooterStatus();
    }

    private void HideNetworkInspector()
    {
        if (NetworkInspectorSideHost.Visibility == Visibility.Visible)
        {
            if (NetworkSideColumn.ActualWidth >= 320) _networkSideWidth = NetworkSideColumn.ActualWidth;
            NetworkInspectorSideHost.Visibility = Visibility.Collapsed;
            NetworkSideSplitter.Visibility = Visibility.Collapsed;
            NetworkSideSplitterColumn.Width = new GridLength(0);
            NetworkSideColumn.Width = new GridLength(0);
            Grid.SetColumnSpan(BrowserWorkspace, 3);
            UpdateFooterStatus();
            return;
        }

        if (NetworkInspectorHost.Visibility != Visibility.Visible) return;
        if (NetworkDrawerRow.ActualHeight >= 180) _networkDrawerHeight = NetworkDrawerRow.ActualHeight;
        var animation = new DoubleAnimation(NetworkInspectorTranslate.Y, Math.Max(180, NetworkDrawerRow.ActualHeight), TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        animation.Completed += (_, _) =>
        {
            NetworkInspectorHost.Visibility = Visibility.Collapsed;
            NetworkDrawerSplitter.Visibility = Visibility.Collapsed;
            NetworkResizeRow.Height = new GridLength(0);
            NetworkDrawerRow.MinHeight = 0;
            NetworkDrawerRow.Height = new GridLength(0);
            UpdateFooterStatus();
        };
        NetworkInspectorTranslate.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void SetNetworkDock(NetworkInspectorDock dock)
    {
        if (_networkDock == dock) return;
        var wasVisible = IsNetworkInspectorVisible;
        if (wasVisible) HideNetworkInspectorImmediately();
        _networkDock = dock;
        NetworkInspector.SetDock(dock);
        if (wasVisible) ShowNetworkInspector();
        else UpdateFooterStatus();
    }

    private void MoveNetworkInspectorTo(NetworkInspectorDock dock)
    {
        if (dock == NetworkInspectorDock.Bottom)
        {
            if (NetworkInspectorSideContent.Content is not null) NetworkInspectorSideContent.Content = null;
            if (!ReferenceEquals(NetworkInspectorBottomContent.Content, NetworkInspector)) NetworkInspectorBottomContent.Content = NetworkInspector;
        }
        else
        {
            if (NetworkInspectorBottomContent.Content is not null) NetworkInspectorBottomContent.Content = null;
            if (!ReferenceEquals(NetworkInspectorSideContent.Content, NetworkInspector)) NetworkInspectorSideContent.Content = NetworkInspector;
        }
    }

    private void HideNetworkInspectorImmediately()
    {
        NetworkInspectorTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (NetworkDrawerRow.ActualHeight >= 180) _networkDrawerHeight = NetworkDrawerRow.ActualHeight;
        if (NetworkSideColumn.ActualWidth >= 320) _networkSideWidth = NetworkSideColumn.ActualWidth;
        NetworkInspectorHost.Visibility = Visibility.Collapsed;
        NetworkDrawerSplitter.Visibility = Visibility.Collapsed;
        NetworkResizeRow.Height = new GridLength(0);
        NetworkDrawerRow.MinHeight = 0;
        NetworkDrawerRow.Height = new GridLength(0);
        NetworkInspectorSideHost.Visibility = Visibility.Collapsed;
        NetworkSideSplitter.Visibility = Visibility.Collapsed;
        NetworkSideSplitterColumn.Width = new GridLength(0);
        NetworkSideColumn.Width = new GridLength(0);
        Grid.SetColumnSpan(BrowserWorkspace, 3);
    }

    private bool IsNetworkInspectorVisible => NetworkInspectorHost.Visibility == Visibility.Visible || NetworkInspectorSideHost.Visibility == Visibility.Visible;

    private void NetworkRequests_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (CapturedNetworkRequest request in e.NewItems) request.PropertyChanged += NetworkRequest_PropertyChanged;
        if (e.OldItems is not null)
            foreach (CapturedNetworkRequest request in e.OldItems) request.PropertyChanged -= NetworkRequest_PropertyChanged;
        UpdateFooterStatus();
    }

    private void NetworkRequest_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CapturedNetworkRequest.IsFailed) or nameof(CapturedNetworkRequest.StatusCode) or nameof(CapturedNetworkRequest.DurationMs))
            UpdateFooterStatus();
    }

    private void UpdateFooterStatus(RestClientFooterStatus? restStatus = null)
    {
        if (FooterPrimaryText is null) return;
        if (_footerWorkspace == FooterWorkspace.Rest)
        {
            if (restStatus is null)
            {
                SetFooter("REST client", "Ready to send a request", "Press Enter in the URL field to send", Color.FromRgb(91, 214, 255));
                return;
            }

            var color = restStatus.IsFailure ? Color.FromRgb(238, 103, 103) : restStatus.IsBusy ? Color.FromRgb(91, 214, 255) : Color.FromRgb(97, 208, 149);
            SetFooter(restStatus.Primary, restStatus.Detail, restStatus.IsBusy ? "You can continue editing this request" : "Result is saved in the active request tab", color);
            return;
        }

        if (_footerWorkspace == FooterWorkspace.Bookmarks)
        {
            SetFooter("Bookmarks library", "Stored locally on this device", "Search, organise, or open a saved page", Color.FromRgb(180, 192, 210));
            return;
        }

        if (_footerWorkspace == FooterWorkspace.Storage)
        {
            SetFooter("Storage inspector", "Current origin state — no request history mixed in", "Refresh after making changes in the page", Color.FromRgb(180, 192, 210));
            return;
        }

        if (_footerWorkspace == FooterWorkspace.Har)
        {
            var state = _harCapture.IsCapturing ? "Recording active browser tab" : _harCapture.Entries.Count == 0 ? "Ready to capture or open a HAR file" : $"{_harCapture.Entries.Count:N0} HAR request{(_harCapture.Entries.Count == 1 ? string.Empty : "s")}";
            SetFooter("HAR inspector", state, _harCapture.IsCapturing ? "Stop & Inspect when you have reproduced the issue" : "Open requests in the REST client without executing them", _harCapture.IsCapturing ? Color.FromRgb(238, 103, 103) : Color.FromRgb(91, 214, 255));
            return;
        }

        if (_footerWorkspace == FooterWorkspace.History)
        {
            SetFooter("Browsing history", "Keeps the last 30 days locally", "Use the menu to clear it at any time", Color.FromRgb(180, 192, 210));
            return;
        }

        if (IsNetworkInspectorVisible)
        {
            var dockHint = _networkDock == NetworkInspectorDock.Bottom ? "Drag the divider above to resize" : "Drag the divider to the left to resize";
            SetFooter($"Network inspector docked {_networkDock.ToString().ToLowerInvariant()}", "Live capture continues in the background", dockHint, Color.FromRgb(91, 214, 255));
            return;
        }

        var total = _networkCapture.Requests.Count;
        var failures = _networkCapture.Requests.Count(request => request.IsFailed);
        var detail = total == 0 ? "Waiting for page activity" : $"{total:N0} captured request{(total == 1 ? string.Empty : "s")}";
        var hint = failures == 0 ? "Open Network inspector for details" : $"{failures:N0} failed request{(failures == 1 ? string.Empty : "s")} detected";
        SetFooter("Network capture on", detail, hint, failures == 0 ? Color.FromRgb(97, 208, 149) : Color.FromRgb(238, 103, 103));
    }

    private void SetFooter(string primary, string detail, string hint, Color color)
    {
        FooterPrimaryText.Text = primary;
        FooterDetailText.Text = detail;
        FooterHintText.Text = hint;
        FooterStatusIndicator.Fill = new SolidColorBrush(color);
    }

    private enum FooterWorkspace { Browser, Rest, Bookmarks, Storage, Har, History }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.Browser.CoreWebView2?.CanGoBack == true)
            _activeTab.Browser.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.Browser.CoreWebView2?.CanGoForward == true)
            _activeTab.Browser.CoreWebView2.GoForward();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _activeTab?.Browser.CoreWebView2?.Reload();
    }

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Navigate();
        e.Handled = true;
    }

    private void AddressBar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
        e.Handled = true;
    }

    private async void Bookmark_Click(object sender, RoutedEventArgs e) => await ShowBookmarkFlyoutAsync(null);

    private async Task ShowBookmarkFlyoutAsync(BookmarkItem? bookmark)
    {
        if (_activeTab?.Browser.Source is null) return;
        var url = _activeTab.Browser.Source.AbsoluteUri;
        _editingBookmark = bookmark ?? await _bookmarks.FindByUrlAsync(url);
        BookmarkFaviconUrl = _editingBookmark?.FaviconUrl;
        var folders = await _bookmarks.GetFoldersAsync();
        BookmarkFolderBox.ItemsSource = folders;
        BookmarkFolderBox.SelectedItem = folders.FirstOrDefault(folder => folder.Id == _editingBookmark?.FolderId)
                                        ?? folders.FirstOrDefault(folder => folder.IsDefault);
        BookmarkTitleBox.Text = _editingBookmark?.Title ?? _activeTab.Title.Text;
        BookmarkUrlText.Text = _editingBookmark?.Url ?? url;
        BookmarkDescriptionBox.Text = _editingBookmark?.Description ?? string.Empty;
        BookmarkNoteBox.Text = _editingBookmark?.Note ?? string.Empty;
        BookmarkNotePanel.Visibility = string.IsNullOrWhiteSpace(_editingBookmark?.Note) ? Visibility.Collapsed : Visibility.Visible;
        BookmarkNewFolderPanel.Visibility = Visibility.Collapsed;
        BookmarkRemoveButton.Visibility = _editingBookmark is null ? Visibility.Collapsed : Visibility.Visible;
        BookmarkSaveButton.Content = _editingBookmark is null ? "Save bookmark" : "Save changes";
        BookmarkHintText.Text = _editingBookmark is null ? "Capturing a lightweight page preview…" : "Editing saved bookmark";
        BookmarkFlyout.IsOpen = true;

        if (_editingBookmark is not null) return;
        var capturedUrl = url;
        var metadata = await _pageMetadata.ExtractAsync(_activeTab.Browser, capturedUrl);
        if (!BookmarkFlyout.IsOpen || !BookmarkUrlText.Text.Equals(capturedUrl, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrWhiteSpace(metadata.Title)) BookmarkTitleBox.Text = metadata.Title;
        if (!string.IsNullOrWhiteSpace(metadata.Description)) BookmarkDescriptionBox.Text = metadata.Description;
        BookmarkFaviconUrl = metadata.FaviconUrl;
        BookmarkHintText.Text = "Ready to save locally";
    }

    private string? BookmarkFaviconUrl { get; set; }

    private async void BookmarkSave_Click(object sender, RoutedEventArgs e)
    {
        var folder = BookmarkFolderBox.SelectedItem as BookmarkFolder;
        var saved = await _bookmarks.SaveAsync(new BookmarkDraft(
            _editingBookmark?.Id,
            folder?.Id,
            BookmarkTitleBox.Text,
            BookmarkUrlText.Text,
            BookmarkDescriptionBox.Text,
            BookmarkNoteBox.Text,
            _editingBookmark?.FaviconUrl ?? BookmarkFaviconUrl));
        _editingBookmark = saved;
        BookmarkHintText.Text = "Saved locally";
        BookmarkFlyout.IsOpen = false;
        await _bookmarkManager.RefreshAsync();
        await UpdateBookmarkStateAsync();
    }

    private void BookmarkCancel_Click(object sender, RoutedEventArgs e) => BookmarkFlyout.IsOpen = false;
    private async void BookmarkViewAll_Click(object sender, RoutedEventArgs e)
    {
        BookmarkFlyout.IsOpen = false;
        ShowBookmarksWorkspace_Click(this, new RoutedEventArgs());
        await _bookmarkManager.RefreshAsync();
    }
    private void BookmarkAddNote_Click(object sender, RoutedEventArgs e) => BookmarkNotePanel.Visibility = Visibility.Visible;
    private void BookmarkCreateFolderToggle_Click(object sender, RoutedEventArgs e) => BookmarkNewFolderPanel.Visibility = Visibility.Visible;

    private async void BookmarkCreateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await _bookmarks.CreateFolderAsync(BookmarkNewFolderBox.Text);
            BookmarkFolderBox.ItemsSource = await _bookmarks.GetFoldersAsync();
            BookmarkFolderBox.SelectedItem = (BookmarkFolderBox.ItemsSource as IEnumerable<BookmarkFolder>)?.FirstOrDefault(item => item.Id == folder.Id);
            BookmarkNewFolderBox.Text = string.Empty;
            BookmarkNewFolderPanel.Visibility = Visibility.Collapsed;
            BookmarkHintText.Text = $"Folder “{folder.Name}” created";
        }
        catch (ArgumentException)
        {
            BookmarkHintText.Text = "Enter a folder name";
        }
    }

    private async void BookmarkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_editingBookmark is null) return;
        await _bookmarks.DeleteAsync(_editingBookmark.Id);
        BookmarkFlyout.IsOpen = false;
        await _bookmarkManager.RefreshAsync();
        await UpdateBookmarkStateAsync();
    }

    private void BookmarkFlyout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { BookmarkFlyout.IsOpen = false; e.Handled = true; }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { BookmarkSave_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private async Task UpdateBookmarkStateAsync()
    {
        if (_activeTab?.Browser.Source is null) return;
        var bookmarked = await _bookmarks.FindByUrlAsync(_activeTab.Browser.Source.AbsoluteUri) is not null;
        BookmarkButton.Foreground = bookmarked ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedTextBrush");
        BookmarkButton.ToolTip = bookmarked ? "Edit bookmark" : "Save bookmark";
    }

    private async Task OpenBookmarkAsync(string url, bool newTab)
    {
        ShowBrowserWorkspace_Click(this, new RoutedEventArgs());
        if (newTab) await CreateTabAsync(url);
        else if (_activeTab is not null) _activeTab.Browser.Source = ToAddress(url);
    }

    private void Navigate()
    {
        if (_activeTab is null) return;
        var input = AddressBar.Text.Trim();
        if (!string.IsNullOrWhiteSpace(input)) _activeTab.Browser.Source = ToAddress(input);
    }

    private static Uri ToAddress(string input)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var address) &&
            (address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(address.Host))
            return address;

        if (LooksLikeAddress(input))
        {
            var scheme = input.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
            return new Uri($"{scheme}://{input}", UriKind.Absolute);
        }

        return new Uri($"https://www.google.com/search?q={Uri.EscapeDataString(input)}", UriKind.Absolute);
    }

    private static bool LooksLikeAddress(string input) =>
        !input.Any(char.IsWhiteSpace) &&
        (input.Contains('.', StringComparison.Ordinal) ||
         input.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         input.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
         System.Net.IPAddress.TryParse(input, out _));

    private sealed class BrowserTab(WebView2 browser, TextBlock title)
    {
        public WebView2 Browser { get; } = browser;
        public TextBlock Title { get; } = title;
        public Border Header { get; set; } = null!;
    }
}
