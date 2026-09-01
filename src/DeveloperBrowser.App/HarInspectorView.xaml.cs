using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DeveloperBrowser.Core.Security;
using Microsoft.Web.WebView2.Core;

namespace DeveloperBrowser.App;

public partial class HarInspectorView : UserControl
{
    private HarCaptureService? _capture;
    private Func<HarCaptureTarget?>? _activeWebView;
    private ICollectionView? _entriesView;
    private HarEntry? _selectedEntry;
    private readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public event EventHandler<CapturedNetworkRequest>? OpenInRestClientRequested;

    public HarInspectorView()
    {
        InitializeComponent();
        SearchBox.Style = (Style)FindResource("SearchInput");
        SearchBox.Tag = "Search URL…";
        _captureTimer.Tick += (_, _) => UpdateCaptureMetrics();
    }

    public void Initialize(HarCaptureService capture, Func<HarCaptureTarget?> activeWebView)
    {
        _capture = capture;
        _activeWebView = activeWebView;
        _entriesView = CollectionViewSource.GetDefaultView(capture.Entries);
        _entriesView.Filter = MatchesFilter;
        RequestList.ItemsSource = _entriesView;
        capture.Entries.CollectionChanged += EntriesChanged;
        capture.Changed += (_, _) => Dispatcher.Invoke(RefreshUi);
        ShowLanding();
    }

    /// <summary>Refreshes the visible capture-source label before recording begins.</summary>
    public void RefreshActiveTab()
    {
        var target = _activeWebView?.Invoke();
        ActiveTabText.Text = target?.Label ?? "No active browser tab";
    }

    private bool MatchesFilter(object item)
    {
        if (item is not HarEntry entry) return false;
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(search) && !entry.Url.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        if (ProblemsOnlyBox?.IsChecked == true && !entry.IsFailed) return false;
        var type = (TypeFilterBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        if (!MatchesType(entry, type)) return false;
        var status = (StatusFilterBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any status";
        if (status != "Any status" && (!int.TryParse(status[..1], out var family) || entry.Status / 100 != family)) return false;
        var method = (MethodFilterBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Any method";
        if (method != "Any method" && !entry.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) return false;
        var domain = DomainFilterBox?.SelectedItem as string;
        return string.IsNullOrWhiteSpace(domain) || domain == "All domains" || entry.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesType(HarEntry entry, string filter) => filter switch
    {
        "All" => true,
        "API / HTTP" => entry.ResourceType is "XHR" or "Fetch" || entry.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase),
        "JS" => entry.ResourceType is "Script" or "Javascript",
        "CSS" => entry.ResourceType is "Stylesheet",
        "Image" => entry.ResourceType is "Image",
        "Font" => entry.ResourceType is "Font",
        "Media" => entry.ResourceType is "Media",
        "Document" => entry.ResourceType is "Document",
        "WS" => entry.ResourceType is "WebSocket" or "WS",
        "Other" => entry.ResourceType is not ("XHR" or "Fetch" or "Script" or "Javascript" or "Stylesheet" or "Image" or "Font" or "Media" or "Document" or "WebSocket" or "WS"),
        _ => true
    };

    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        var target = _activeWebView?.Invoke();
        if (_capture is null || target is null) { LandingHintText.Text = "Open a browser tab before starting a HAR capture."; return; }
        try
        {
            await _capture.StartAsync(target.WebView);
            CapturePanel.Visibility = Visibility.Visible;
            LandingPanel.Visibility = Visibility.Collapsed;
            InspectorPanel.Visibility = Visibility.Collapsed;
            CaptureTabText.Text = "Capturing: " + target.Label + "  •  This capture remains bound to this tab if you switch tabs.";
            _captureTimer.Start();
            RefreshUi();
        }
        catch (Exception exception)
        {
            LandingHintText.Text = "Could not start capture: " + exception.Message;
        }
    }

    private async void StopCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null) return;
        await _capture.StopAsync();
        _captureTimer.Stop();
        CapturePanel.Visibility = Visibility.Collapsed;
        ShowInspector();
    }

    private async void OpenHar_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "HAR files (*.har)|*.har|JSON files (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _capture.LoadAsync(dialog.FileName);
            _captureTimer.Stop();
            CapturePanel.Visibility = Visibility.Collapsed;
            ShowInspector();
        }
        catch (Exception exception)
        {
            if (InspectorPanel.Visibility == Visibility.Visible) MessageBox.Show(exception.Message, "Open HAR", MessageBoxButton.OK, MessageBoxImage.Warning);
            else LandingHintText.Text = exception.Message;
        }
    }

    private async void ExportHar_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null || _capture.Entries.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "HAR files (*.har)|*.har", FileName = "devbrowser-capture.har" };
        if (dialog.ShowDialog() != true) return;
        try { await _capture.SaveAsync(dialog.FileName); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Export HAR", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (HarEntry entry in e.NewItems) entry.PropertyChanged += EntryPropertyChanged;
        if (e.OldItems is not null) foreach (HarEntry entry in e.OldItems) entry.PropertyChanged -= EntryPropertyChanged;
        Dispatcher.Invoke(RefreshUi);
    }

    private void EntryPropertyChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.Invoke(RefreshUi);
    private void FilterChanged(object sender, RoutedEventArgs e) => RefreshFilter();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();

    private void RefreshUi()
    {
        UpdateCaptureMetrics();
        UpdateSummary();
        UpdateDomains();
        RefreshFilter();
    }

    private void RefreshFilter() => _entriesView?.Refresh();

    private void UpdateCaptureMetrics()
    {
        if (_capture is null) return;
        CaptureElapsedText.Text = _capture.IsCapturing ? (DateTimeOffset.UtcNow - _capture.StartedAt).ToString("mm\\:ss") : "00:00";
        CaptureRequestCountText.Text = _capture.Entries.Count.ToString("N0");
        CaptureFailedCountText.Text = _capture.Entries.Count(entry => entry.IsFailed).ToString("N0");
    }

    private void UpdateSummary()
    {
        if (_capture is null) return;
        var entries = _capture.Entries.ToArray();
        TotalText.Text = entries.Length.ToString("N0");
        SuccessfulText.Text = entries.Count(entry => entry.Status is >= 200 and < 400).ToString("N0");
        FailedText.Text = entries.Count(entry => entry.IsFailed).ToString("N0");
        RedirectText.Text = entries.Count(entry => entry.IsRedirect).ToString("N0");
        var size = entries.Sum(entry => entry.TransferSize);
        TransferredText.Text = size < 1024 * 1024 ? $"{size / 1024d:0.0} KB" : $"{size / 1024d / 1024d:0.0} MB";
        DurationText.Text = $"{entries.Sum(entry => entry.DurationMs) / 1000d:0.00} s";
        HarSourceText.Text = _capture.SourceName ?? "Captured session";
        ExportHarButton.IsEnabled = entries.Length > 0;
    }

    private void UpdateDomains()
    {
        if (_capture is null || DomainFilterBox is null) return;
        var current = DomainFilterBox.SelectedItem as string;
        var values = new[] { "All domains" }.Concat(_capture.Entries.Select(entry => entry.Domain).Where(domain => domain != "—").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(domain => domain)).ToList();
        DomainFilterBox.ItemsSource = values;
        DomainFilterBox.SelectedItem = values.Contains(current ?? string.Empty) ? current : values[0];
    }

    private void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedEntry = RequestList.SelectedItem as HarEntry;
        if (_selectedEntry is null) { ClearDetail(); return; }
        PopulateDetail(_selectedEntry);
    }

    private void PopulateDetail(HarEntry entry)
    {
        DetailTitleText.Text = $"{entry.Method}  {entry.StatusText}";
        DetailUrlText.Text = entry.Url;
        OverviewBox.Text = $"URL: {entry.Url}{Environment.NewLine}Method: {entry.Method}{Environment.NewLine}Status: {entry.StatusText}{Environment.NewLine}Type: {entry.ResourceType}{Environment.NewLine}Domain: {entry.Domain}{Environment.NewLine}MIME: {entry.ContentType}{Environment.NewLine}Transfer: {entry.SizeText}{Environment.NewLine}Duration: {entry.DurationText}{Environment.NewLine}Remote: {(entry.RemoteAddress ?? "—")}{(entry.RemotePort is null ? string.Empty : ":" + entry.RemotePort)}{(string.IsNullOrWhiteSpace(entry.FailureReason) ? string.Empty : Environment.NewLine + "Failure: " + entry.FailureReason)}";
        RequestBox.Text = $"{entry.Method} {entry.Url}{Environment.NewLine}{Environment.NewLine}Query parameters:{Environment.NewLine}{string.Join(Environment.NewLine, entry.QueryParameters().Select(pair => pair.Key + ": " + pair.Value))}{Environment.NewLine}{Environment.NewLine}Request body:{Environment.NewLine}{entry.RequestBody ?? "(No request body)"}";
        ResponseViewer.SetContent(entry.ResponseBody ?? "(Response body was not available in this HAR entry.)", entry.ContentType);
        RequestHeadersBox.Text = FormatHeaders(entry.RequestHeaders);
        ResponseHeadersBox.Text = FormatHeaders(entry.ResponseHeaders);
        CookiesBox.Text = string.IsNullOrWhiteSpace(entry.Cookies()) ? "(No cookies available)" : entry.Cookies();
        TimingBox.Text = $"Started: {entry.StartedAt.LocalDateTime:G}{Environment.NewLine}Duration: {entry.DurationText}{Environment.NewLine}Transferred: {entry.SizeText}{Environment.NewLine}Protocol: {entry.HttpVersion}";
        OpenRestButton.IsEnabled = true;
        JwtButton.IsEnabled = entry.RequestHeaders.TryGetValue("Authorization", out var auth) && JwtTokenInspector.IsBearerJwt(auth);
    }

    private void ClearDetail()
    {
        DetailTitleText.Text = "Select a request"; DetailUrlText.Text = string.Empty;
        OverviewBox.Text = RequestBox.Text = RequestHeadersBox.Text = ResponseHeadersBox.Text = CookiesBox.Text = TimingBox.Text = string.Empty;
        ResponseViewer.SetContent(null); OpenRestButton.IsEnabled = JwtButton.IsEnabled = false;
    }

    private void OpenRest_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is not null) OpenInRestClientRequested?.Invoke(this, _selectedEntry.ToCapturedRequest());
    }

    private void InspectJwt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null || !_selectedEntry.RequestHeaders.TryGetValue("Authorization", out var header) || !JwtTokenInspector.TryInspectBearerToken(header, out var inspection) || inspection is null) return;
        MessageBox.Show($"JWT decoded locally.\n\nIssuer: {inspection.Issuer ?? "Not present"}\nSubject: {inspection.Subject ?? "Not present"}\nExpires: {JwtTokenInspection.LocalTime(inspection.ExpiresAt)}\n\nSignature: not validated", "JWT inspection", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowLanding()
    {
        RefreshActiveTab();
        LandingPanel.Visibility = Visibility.Visible;
        CapturePanel.Visibility = Visibility.Collapsed;
        InspectorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowInspector()
    {
        LandingPanel.Visibility = Visibility.Collapsed;
        CapturePanel.Visibility = Visibility.Collapsed;
        InspectorPanel.Visibility = Visibility.Visible;
        ClearDetail();
        RefreshUi();
    }

    private static string FormatHeaders(IReadOnlyDictionary<string, string> headers) => headers.Count == 0 ? "(No headers available)" : string.Join(Environment.NewLine, headers.Select(header => header.Key + ": " + header.Value));
}

public sealed record HarCaptureTarget(CoreWebView2 WebView, string Label);
