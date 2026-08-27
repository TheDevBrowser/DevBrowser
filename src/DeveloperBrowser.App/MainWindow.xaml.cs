using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeveloperBrowser.Core.Browser;
using Microsoft.Web.WebView2.Wpf;

namespace DeveloperBrowser.App;

public partial class MainWindow : Window
{
    private readonly List<BrowserTab> _tabs = [];
    private readonly NetworkCaptureService _networkCapture = new();
    private BrowserTab? _activeTab;

    public MainWindow()
    {
        InitializeComponent();
        NetworkInspector.Initialize(_networkCapture);
        NetworkInspector.CloseRequested += (_, _) => HideNetworkInspector();
        NetworkInspector.OpenInRestClientRequested += (_, request) =>
        {
            RestClientView.OpenCapturedRequest(request);
            ShowRestWorkspace_Click(this, new RoutedEventArgs());
        };
        Loaded += async (_, _) => await CreateTabAsync("https://www.bing.com");
    }

    private async Task CreateTabAsync(string? address = null)
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
            if (_activeTab == tab) AddressBar.Text = browser.Source.AbsoluteUri;
        };

        SelectTab(tab);
        await browser.EnsureCoreWebView2Async();
        await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BrowserInstrumentation.ConsoleForwarderScript);
        await _networkCapture.AttachAsync(browser.CoreWebView2);
        browser.Source = ToAddress(address ?? "https://www.bing.com");
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
            Foreground = (Brush)FindResource("MutedTextBrush"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 15,
            Width = 24,
            Height = 28,
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
    }

    private void ShowRestWorkspace_Click(object sender, RoutedEventArgs e)
    {
        BrowserWorkspace.Visibility = Visibility.Collapsed;
        RestWorkspace.Visibility = Visibility.Visible;
        HideNetworkInspector();
    }

    private void ToggleNetworkInspector_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkInspectorHost.Visibility == Visibility.Visible) HideNetworkInspector();
        else ShowNetworkInspector();
    }

    private void ShowNetworkInspector()
    {
        NetworkDrawerRow.Height = new GridLength(340);
        NetworkInspectorHost.Visibility = Visibility.Visible;
        NetworkInspectorTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(340, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HideNetworkInspector()
    {
        if (NetworkInspectorHost.Visibility != Visibility.Visible) return;
        var animation = new DoubleAnimation(NetworkInspectorTranslate.Y, 340, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        animation.Completed += (_, _) =>
        {
            NetworkInspectorHost.Visibility = Visibility.Collapsed;
            NetworkDrawerRow.Height = new GridLength(0);
        };
        NetworkInspectorTranslate.BeginAnimation(TranslateTransform.YProperty, animation);
    }

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

    private void Navigate()
    {
        if (_activeTab is null) return;
        var input = AddressBar.Text.Trim();
        if (!string.IsNullOrWhiteSpace(input)) _activeTab.Browser.Source = ToAddress(input);
    }

    private static Uri ToAddress(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var address) && !string.IsNullOrWhiteSpace(address.Host)) return address;
        return new Uri($"https://{input}", UriKind.Absolute);
    }

    private sealed class BrowserTab(WebView2 browser, TextBlock title)
    {
        public WebView2 Browser { get; } = browser;
        public TextBlock Title { get; } = title;
        public Border Header { get; set; } = null!;
    }
}
