using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DeveloperBrowser.Core.Security;
using DeveloperBrowser.Core.Networking;
using System.Text.RegularExpressions;

namespace DeveloperBrowser.App;

public partial class NetworkInspectorView : UserControl
{
    private NetworkCaptureService? _capture;
    private ICollectionView? _requestsView;
    private CapturedNetworkRequest? _selectedRequest;
    private NetworkProblemCategory _problemCategory = NetworkProblemCategory.All;
    private bool _followingRelatedRequest;
    private readonly List<ProblemCategoryItem> _problemCategories = [];
    private readonly KeyValueDataViewer _requestHeadersViewer = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly KeyValueDataViewer _responseHeadersViewer = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly KeyValueDataViewer _queryViewer = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly StructuredDataViewer _requestBodyViewer = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly KeyValueDataViewer _cookiesViewer = new() { Margin = new Thickness(0, 10, 0, 0) };

    public event EventHandler<CapturedNetworkRequest>? OpenInRestClientRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<NetworkInspectorDock>? DockRequested;

    public NetworkInspectorView()
    {
        InitializeComponent();
        ReplaceTabContent(RequestHeadersBox, _requestHeadersViewer);
        ReplaceTabContent(ResponseHeadersBox, _responseHeadersViewer);
        ReplaceTabContent(QueryBox, _queryViewer);
        ReplaceTabContent(RequestBodyBox, _requestBodyViewer);
        ReplaceTabContent(CookiesBox, _cookiesViewer);
        ProblemInbox.ItemsSource = _problemCategories;
        UpdateProblemInbox();
    }

    public void Initialize(NetworkCaptureService capture)
    {
        _capture = capture;
        DataContext = capture;
        _requestsView = CollectionViewSource.GetDefaultView(capture.Requests);
        _requestsView.Filter = MatchesFilter;
        capture.Requests.CollectionChanged += Requests_CollectionChanged;
        RequestList.ItemsSource = _requestsView;
        UpdateRequestCount();
        UpdateProblemInbox();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not CapturedNetworkRequest request) return false;
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(search) && !request.Url.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        if (_problemCategory != NetworkProblemCategory.All && !NetworkInsightAnalyzer.Categories(request, _capture?.Requests.ToArray() ?? []).Contains(_problemCategory)) return false;
        return FilterBox?.SelectedIndex switch
        {
            1 => request.ResourceType is "XHR" or "Fetch",
            2 => request.ResourceType == "Document",
            3 => request.ResourceType == "Script",
            4 => request.ResourceType == "Stylesheet",
            5 => request.ResourceType == "Image",
            6 => request.IsFailed,
            _ => true
        };
    }

    private void Requests_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (CapturedNetworkRequest request in e.NewItems) request.PropertyChanged += Request_PropertyChanged;
        if (e.OldItems is not null)
            foreach (CapturedNetworkRequest request in e.OldItems) request.PropertyChanged -= Request_PropertyChanged;
        UpdateRequestCount();
        UpdateProblemInbox();
        TrafficEmptyText.Visibility = _capture?.Requests.Count > 0 && _requestsView?.Cast<object>().Any() == false ? Visibility.Visible : _capture?.Requests.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Request_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CapturedNetworkRequest.IsFailed) or nameof(CapturedNetworkRequest.StatusCode) or nameof(CapturedNetworkRequest.DurationMs) or nameof(CapturedNetworkRequest.ResponseContentType) or nameof(CapturedNetworkRequest.ResponseBody) or nameof(CapturedNetworkRequest.Timing))
        {
            RefreshFilter();
            if (_selectedRequest is not null && ReferenceEquals(sender, _selectedRequest)) PopulateDiagnosis(_selectedRequest);
        }
    }
    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();
    private void RefreshFilter() { _requestsView?.Refresh(); UpdateRequestCount(); UpdateProblemInbox(); }
    private void UpdateRequestCount()
    {
        if (RequestCountText is null) return;
        var visible = _requestsView?.Cast<object>().Count() ?? 0;
        var total = _capture?.Requests.Count ?? 0;
        RequestCountText.Text = visible == total ? $"{total:N0} requests" : $"{visible:N0} of {total:N0}";
        TrafficEmptyText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { _capture?.Clear(); ClearDetail(); }
    private void PreserveLogBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_capture is not null) _capture.PreserveLog = PreserveLogBox.IsChecked == true;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void DockBottom_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke(this, NetworkInspectorDock.Bottom);
    private void DockRight_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke(this, NetworkInspectorDock.Right);

    public void SetDock(NetworkInspectorDock dock)
    {
        DockBottomButton.Visibility = dock == NetworkInspectorDock.Bottom ? Visibility.Collapsed : Visibility.Visible;
        DockRightButton.Visibility = dock == NetworkInspectorDock.Right ? Visibility.Collapsed : Visibility.Visible;

        InspectorContentGrid.ColumnDefinitions.Clear();
        InspectorContentGrid.RowDefinitions.Clear();
        if (dock == NetworkInspectorDock.Bottom)
        {
            InspectorContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
            InspectorContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            InspectorContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
            Grid.SetRow(RequestListPanel, 0); Grid.SetColumn(RequestListPanel, 0);
            Grid.SetRow(InspectorDetailSplitter, 0); Grid.SetColumn(InspectorDetailSplitter, 1);
            Grid.SetRow(RequestDetailPanel, 0); Grid.SetColumn(RequestDetailPanel, 2);
            InspectorDetailSplitter.ResizeDirection = GridResizeDirection.Columns;
            InspectorDetailSplitter.Width = 10; InspectorDetailSplitter.Height = double.NaN;
        }
        else
        {
            InspectorContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52, GridUnitType.Star), MinHeight = 160 });
            InspectorContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            InspectorContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48, GridUnitType.Star), MinHeight = 160 });
            Grid.SetRow(RequestListPanel, 0); Grid.SetColumn(RequestListPanel, 0);
            Grid.SetRow(InspectorDetailSplitter, 1); Grid.SetColumn(InspectorDetailSplitter, 0);
            Grid.SetRow(RequestDetailPanel, 2); Grid.SetColumn(RequestDetailPanel, 0);
            InspectorDetailSplitter.ResizeDirection = GridResizeDirection.Rows;
            InspectorDetailSplitter.Width = double.NaN; InspectorDetailSplitter.Height = 8;
        }
    }

    private async void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRequest = RequestList.SelectedItem as CapturedNetworkRequest;
        if (_selectedRequest is null) { ClearDetail(); return; }
        PopulateDetail(_selectedRequest);
        await UpdatePolicyAnalysisAsync(_selectedRequest);
        if (_capture is not null)
        {
            await _capture.EnsureResponseBodyAsync(_selectedRequest);
            if (_selectedRequest == RequestList.SelectedItem) ResponseDataViewer.SetContent(_selectedRequest.ResponseBody ?? "(Response body is not available yet.)", _selectedRequest.ResponseContentType);
        }
    }

    private void PopulateDetail(CapturedNetworkRequest request)
    {
        DetailMethodText.Text = request.Method;
        DetailMethodText.Foreground = HttpMethodPalette.Foreground(request.Method);
        DetailMethodBadge.Background = HttpMethodPalette.Background(request.Method);
        DetailUrlText.Text = request.Url;
        DetailStatusText.Text = request.StatusText;
        DetailStatusBadge.Background = new SolidColorBrush(request.IsFailed ? Color.FromRgb(128, 56, 64) : Color.FromRgb(29, 100, 70));
        DetailTimingText.Text = $"{request.ResourceType} · {request.DurationText}";
        PopulateDiagnosis(request);
        _requestHeadersViewer.SetItems(HttpInspectorFormatting.Headers(request.RequestHeaders), "No request headers");
        _responseHeadersViewer.SetItems(HttpInspectorFormatting.Headers(request.ResponseHeaders), "No response headers");
        _queryViewer.SetItems(request.QueryParameters(), "No query parameters");
        _requestBodyViewer.SetContent(request.RequestBody ?? "(No request body)", request.RequestContentType);
        ResponseDataViewer.SetContent(request.ResponseBody ?? "Loading response body…", request.ResponseContentType);
        _cookiesViewer.SetItems(HttpInspectorFormatting.Cookies(request.RequestHeaders, request.ResponseHeaders), "No cookies available");
        TimingBox.Text = BuildTimingReport(request);
        OpenInRestButton.IsEnabled = true;
        JwtInspectButton.IsEnabled = TryGetAuthorizationHeader(request, out var authorizationHeader) && JwtTokenInspector.IsBearerJwt(authorizationHeader);
        ClearJwtDetail();
        PolicyAnalysisBox.Text = BuildPolicyReport(request).ToDisplayText();
    }

    private void PopulateDiagnosis(CapturedNetworkRequest request)
    {
        var all = _capture?.Requests.ToArray() ?? [];
        var findings = NetworkInsightAnalyzer.Findings(request, all);
        FindingsList.ItemsSource = findings.Select(FindingItem.From).ToList();
        StoryList.ItemsSource = NetworkInsightAnalyzer.Story(request, all).Select(StoryItem.From).ToList();
        var related = NetworkInsightAnalyzer.Related(request, all).Select(RelatedItem.From).ToList();
        RelatedRequestsList.ItemsSource = related;
        RelatedRequestsList.SelectedItem = null;
        NoRelatedText.Visibility = related.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RelatedHintText.Visibility = related.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiagnosisHeadlineText.Text = findings[0].Title;
        DiagnosisHeadlineText.Foreground = Accent(findings[0].Severity);
    }

    private void ClearDetail()
    {
        DetailMethodText.Text = "Select a request"; DetailUrlText.Text = string.Empty; DetailStatusText.Text = "No request selected"; DetailTimingText.Text = string.Empty;
        DetailMethodBadge.Background = new SolidColorBrush(Color.FromRgb(37, 53, 72));
        DiagnosisHeadlineText.Text = "Select traffic to begin diagnosis";
        DiagnosisHeadlineText.Foreground = (Brush)FindResource("AccentBrush");
        FindingsList.ItemsSource = null; StoryList.ItemsSource = null; RelatedRequestsList.ItemsSource = null;
        NoRelatedText.Visibility = Visibility.Visible; RelatedHintText.Visibility = Visibility.Collapsed;
        TimingBox.Text = string.Empty;
        _requestHeadersViewer.SetItems(null, "No request headers");
        _responseHeadersViewer.SetItems(null, "No response headers");
        _queryViewer.SetItems(null, "No query parameters");
        _requestBodyViewer.SetContent(null);
        _cookiesViewer.SetItems(null, "No cookies available");
        ResponseDataViewer.SetContent(null);
        PolicyAnalysisBox.Text = string.Empty;
        OpenInRestButton.IsEnabled = false;
        JwtInspectButton.IsEnabled = false;
        ClearJwtDetail();
    }

    private void OpenInRest_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRequest is not null) OpenInRestClientRequested?.Invoke(this, _selectedRequest);
    }

    private void ProblemInbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemInbox.SelectedItem is not ProblemCategoryItem selected) return;
        _problemCategory = selected.Category;
        ActiveProblemLabel.Text = selected.Category == NetworkProblemCategory.All ? "All captured activity" : selected.Label;
        _requestsView?.Refresh();
        UpdateRequestCount();
    }

    private void RelatedRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_followingRelatedRequest || RelatedRequestsList.SelectedItem is not RelatedItem related) return;
        _followingRelatedRequest = true;
        try
        {
            _problemCategory = NetworkProblemCategory.All;
            ProblemInbox.SelectedIndex = 0;
            _requestsView?.Refresh();
            RequestList.SelectedItem = related.Request;
            RequestList.ScrollIntoView(related.Request);
            DiagnosisTab.IsSelected = true;
        }
        finally { _followingRelatedRequest = false; }
    }

    private void UpdateProblemInbox()
    {
        // Filter selection events can fire while InitializeComponent is still building the visual tree.
        if (ProblemInbox is null) return;
        var requests = _capture?.Requests.ToArray() ?? [];
        var selected = _problemCategory;
        _problemCategories.Clear();
        foreach (var category in Enum.GetValues<NetworkProblemCategory>())
        {
            var count = category == NetworkProblemCategory.All ? requests.Length : requests.Count(request => NetworkInsightAnalyzer.Categories(request, requests).Contains(category));
            _problemCategories.Add(new(category, CategoryLabel(category), count, CategoryAccent(category)));
        }
        ProblemInbox.Items.Refresh();
        ProblemInbox.SelectedItem = _problemCategories.FirstOrDefault(item => item.Category == selected) ?? _problemCategories[0];
        ActiveProblemLabel.Text = selected == NetworkProblemCategory.All ? "All captured activity" : CategoryLabel(selected);
    }

    private static string CategoryLabel(NetworkProblemCategory category) => category switch
    {
        NetworkProblemCategory.All => "All",
        NetworkProblemCategory.Content => "Unexpected content",
        _ => category.ToString()
    };

    private static Brush CategoryAccent(NetworkProblemCategory category) => category switch
    {
        NetworkProblemCategory.Broken => Frozen(239, 102, 102),
        NetworkProblemCategory.Suspicious => Frozen(245, 176, 65),
        NetworkProblemCategory.Slow => Frozen(238, 147, 75),
        NetworkProblemCategory.Authentication => Frozen(179, 139, 250),
        NetworkProblemCategory.Content => Frozen(255, 126, 182),
        NetworkProblemCategory.Cache => Frozen(84, 194, 205),
        NetworkProblemCategory.Healthy => Frozen(97, 208, 149),
        _ => Frozen(91, 214, 255)
    };

    private static Brush Accent(NetworkFindingSeverity severity) => severity switch
    {
        NetworkFindingSeverity.Error => Frozen(239, 102, 102),
        NetworkFindingSeverity.Warning => Frozen(245, 176, 65),
        NetworkFindingSeverity.Good => Frozen(97, 208, 149),
        _ => Frozen(91, 214, 255)
    };

    private static Brush Badge(NetworkFindingSeverity severity) => severity switch
    {
        NetworkFindingSeverity.Error => Frozen(65, 31, 40),
        NetworkFindingSeverity.Warning => Frozen(61, 47, 25),
        NetworkFindingSeverity.Good => Frozen(24, 55, 43),
        _ => Frozen(24, 48, 65)
    };

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record ProblemCategoryItem(NetworkProblemCategory Category, string Label, int Count, Brush Accent);
    private sealed record FindingItem(string Category, string Title, string Explanation, string NextStep, Brush Accent, Brush BadgeBackground)
    {
        public static FindingItem From(NetworkFinding finding) => new(NetworkInspectorView.CategoryLabel(finding.Category), finding.Title, finding.Explanation, finding.NextStep, NetworkInspectorView.Accent(finding.Severity), NetworkInspectorView.Badge(finding.Severity));
    }
    private sealed record StoryItem(string Stage, string Detail, Brush Accent)
    {
        public static StoryItem From(NetworkStoryStep step) => new(step.Stage, step.Detail, NetworkInspectorView.Accent(step.Severity));
    }
    private sealed record RelatedItem(CapturedNetworkRequest Request, string Relationship, string Name, string Summary, Brush Accent)
    {
        public static RelatedItem From(RelatedNetworkRequest related) => new(related.Request, related.Relationship, related.Request.DisplayName, related.Summary, NetworkInspectorView.CategoryAccent(NetworkInsightAnalyzer.PrimaryCategory(related.Request)));
    }

    private void InspectJwt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRequest is null || !TryGetAuthorizationHeader(_selectedRequest, out var authorizationHeader) ||
            !JwtTokenInspector.TryInspectBearerToken(authorizationHeader, out var inspection) || inspection is null)
        {
            ClearJwtDetail("The Bearer value is not a decodable JWT.");
            JwtDetailTab.IsSelected = true;
            return;
        }

        ShowJwtInspection(inspection);
    }

    /// <summary>Displays a decoded storage JWT in the existing inspector UI without sending it anywhere.</summary>
    public void InspectJwtToken(string token)
    {
        if (!JwtTokenInspector.TryInspectToken(token, out var inspection) || inspection is null)
        {
            ClearJwtDetail("The stored value is not a decodable JWT.");
            JwtDetailTab.IsSelected = true;
            return;
        }

        ShowJwtInspection(inspection);
    }

    private void ShowJwtInspection(JwtTokenInspection inspection)
    {
        JwtTimeStatusText.Text = inspection.TimeStatus switch
        {
            JwtTimeStatus.Expired => "Expired",
            JwtTimeStatus.NotValidYet => "Not valid yet",
            _ => "Valid time range"
        };
        JwtTimeStatusBadge.Background = new SolidColorBrush(inspection.TimeStatus switch
        {
            JwtTimeStatus.Expired => Color.FromRgb(128, 56, 64),
            JwtTimeStatus.NotValidYet => Color.FromRgb(109, 77, 35),
            _ => Color.FromRgb(29, 100, 70)
        });
        JwtSignatureText.Text = inspection.SignatureStatus;
        JwtIssuerText.Text = DisplayClaim(inspection.Issuer);
        JwtAudienceText.Text = DisplayClaim(inspection.Audience);
        JwtSubjectText.Text = DisplayClaim(inspection.Subject);
        JwtScopesText.Text = DisplayClaim(inspection.Scopes);
        JwtRolesText.Text = DisplayClaim(inspection.Roles);
        JwtIssuedAtText.Text = JwtTokenInspection.LocalTime(inspection.IssuedAt);
        JwtNotBeforeText.Text = JwtTokenInspection.LocalTime(inspection.NotBefore);
        JwtExpiresText.Text = JwtTokenInspection.LocalTime(inspection.ExpiresAt);
        JwtRemainingText.Text = inspection.TimeRemaining;
        JwtHeaderBox.Text = inspection.HeaderJson;
        JwtPayloadBox.Text = inspection.PayloadJson;
        JwtDetailTab.IsSelected = true;
    }

    private void ClearJwtDetail(string? status = null)
    {
        JwtTimeStatusText.Text = status ?? "JWT not inspected";
        JwtTimeStatusBadge.Background = new SolidColorBrush(Color.FromRgb(38, 50, 56));
        JwtSignatureText.Text = "Signature: not validated";
        JwtIssuerText.Text = JwtAudienceText.Text = JwtSubjectText.Text = JwtScopesText.Text = JwtRolesText.Text = "—";
        JwtIssuedAtText.Text = JwtNotBeforeText.Text = JwtExpiresText.Text = JwtRemainingText.Text = "—";
        JwtHeaderBox.Text = JwtPayloadBox.Text = string.Empty;
    }

    private static bool TryGetAuthorizationHeader(CapturedNetworkRequest request, out string? authorizationHeader) =>
        request.RequestHeaders.TryGetValue("Authorization", out authorizationHeader);

    private static string DisplayClaim(string? value) => string.IsNullOrWhiteSpace(value) ? "Not present" : value;

    private static string BuildTimingReport(CapturedNetworkRequest request)
    {
        var timing = request.Timing;
        var endpoint = string.IsNullOrWhiteSpace(request.RemoteAddress) ? "Not recorded" : request.RemoteAddress + (request.RemotePort is null ? string.Empty : $":{request.RemotePort}");
        var lines = new List<string>
        {
            "RECORDED BY CHROMIUM",
            $"Total:                 {request.DurationText}",
            $"Queued / blocked:      {TimingValue(timing.BlockedMs)}",
            $"DNS lookup:            {TimingValue(timing.DnsMs)}",
            $"Connection (incl. TLS):{TimingValue(timing.ConnectMs)}",
            $"TLS negotiation:       {TimingValue(timing.TlsMs)}",
            $"Request upload:        {TimingValue(timing.SendMs)}",
            $"Waiting for server:    {TimingValue(timing.WaitMs)}",
            $"Response download:     {TimingValue(timing.ReceiveMs)}",
            string.Empty,
            "CONNECTION",
            $"Protocol:              {request.Protocol}",
            $"Remote endpoint:       {endpoint}",
            $"Connection reused:     {(request.ConnectionReused ? "Yes" : "No / not recorded")}",
            $"Response source:       {request.CacheSource}",
            string.Empty,
            "Unavailable phases are shown as Not recorded; DevBrowser does not estimate them."
        };
        if (!string.IsNullOrEmpty(request.FailureReason)) lines.Add($"Failure: {request.FailureReason}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string TimingValue(double? value) => value is null ? "Not recorded" : $"{value.Value:0.##} ms";

    private static void ReplaceTabContent(FrameworkElement oldContent, FrameworkElement newContent)
    {
        var tab = (TabItem)oldContent.Parent;
        tab.Content = newContent;
    }

    private async Task UpdatePolicyAnalysisAsync(CapturedNetworkRequest request)
    {
        if (_capture is null) return;
        if (request.BlockedReason?.Contains("csp", StringComparison.OrdinalIgnoreCase) == true)
        {
            var pageDocument = _capture.Requests.LastOrDefault(candidate =>
                candidate.ResourceType.Equals("Document", StringComparison.OrdinalIgnoreCase) &&
                candidate.Url.Equals(request.PageUrl, StringComparison.OrdinalIgnoreCase));
            if (pageDocument is not null) await _capture.EnsureResponseBodyAsync(pageDocument);
        }
        if (request == _selectedRequest) PolicyAnalysisBox.Text = BuildPolicyReport(request).ToDisplayText();
    }

    private NetworkPolicyReport BuildPolicyReport(CapturedNetworkRequest request)
    {
        var captured = _capture?.Requests.ToArray() ?? [];
        var policies = GetPagePolicies(request, captured);
        var current = ToEvidence(request, policies);
        var all = captured.Select(candidate => ToEvidence(candidate, candidate == request ? policies : [])).ToArray();
        return NetworkPolicyAnalyzer.Analyze(current, all);
    }

    private static NetworkRequestEvidence ToEvidence(CapturedNetworkRequest request, IReadOnlyList<string> policies)
    {
        var credentials = request.RequestHeaders.Keys.Any(header => header.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            ? "Credentials may be included (Cookie or Authorization header observed; Fetch credentials mode is not directly exposed)."
            : "Not directly exposed by CDP; no Cookie or Authorization header observed.";
        return new NetworkRequestEvidence(
            request.RequestId, request.Url, request.Method, request.ResourceType, request.StartedAt, request.PageUrl,
            request.RequestHeaders, request.ResponseHeaders, request.StatusCode, request.IsFailed, request.FailureReason,
            request.BlockedReason, request.CorsError, credentials, policies);
    }

    private static IReadOnlyList<string> GetPagePolicies(CapturedNetworkRequest request, IEnumerable<CapturedNetworkRequest> all)
    {
        if (string.IsNullOrWhiteSpace(request.PageUrl)) return [];
        var pageDocuments = all.Where(candidate => candidate.ResourceType.Equals("Document", StringComparison.OrdinalIgnoreCase) &&
                                                   candidate.Url.Equals(request.PageUrl, StringComparison.OrdinalIgnoreCase));
        var policies = new List<string>();
        foreach (var document in pageDocuments)
        {
            foreach (var header in new[] { "Content-Security-Policy", "Content-Security-Policy-Report-Only" })
                if (document.ResponseHeaders.TryGetValue(header, out var policy) && !string.IsNullOrWhiteSpace(policy)) policies.Add(policy);
            if (!string.IsNullOrWhiteSpace(document.ResponseBody)) policies.AddRange(ExtractMetaPolicies(document.ResponseBody));
        }
        return policies;
    }

    private static IEnumerable<string> ExtractMetaPolicies(string html)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!Regex.IsMatch(tag.Value, @"http-equiv\s*=\s*(['""]?)content-security-policy\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var content = Regex.Match(tag.Value, @"content\s*=\s*(['""])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (content.Success && !string.IsNullOrWhiteSpace(content.Groups["value"].Value)) yield return content.Groups["value"].Value;
        }
    }
}

public enum NetworkInspectorDock { Bottom, Right }
