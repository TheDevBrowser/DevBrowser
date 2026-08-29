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

    public event EventHandler<CapturedNetworkRequest>? OpenInRestClientRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<NetworkInspectorDock>? DockRequested;

    public NetworkInspectorView() => InitializeComponent();

    public void Initialize(NetworkCaptureService capture)
    {
        _capture = capture;
        DataContext = capture;
        _requestsView = CollectionViewSource.GetDefaultView(capture.Requests);
        _requestsView.Filter = MatchesFilter;
        capture.Requests.CollectionChanged += Requests_CollectionChanged;
        UpdateRequestCount();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not CapturedNetworkRequest request) return false;
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(search) && !request.Url.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
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
    }

    private void Request_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CapturedNetworkRequest.IsFailed) or nameof(CapturedNetworkRequest.StatusCode) or nameof(CapturedNetworkRequest.DurationMs)) RefreshFilter();
    }
    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();
    private void RefreshFilter() { _requestsView?.Refresh(); UpdateRequestCount(); }
    private void UpdateRequestCount()
    {
        if (RequestCountText is null) return;
        RequestCountText.Text = _requestsView is null ? "Capture enabled" : $"{_requestsView.Cast<object>().Count():N0} requests";
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { _capture?.Clear(); ClearDetail(); }
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
        DetailUrlText.Text = request.Url;
        DetailStatusText.Text = request.StatusText;
        DetailStatusBadge.Background = new SolidColorBrush(request.IsFailed ? Color.FromRgb(128, 56, 64) : Color.FromRgb(29, 100, 70));
        DetailTimingText.Text = $"{request.ResourceType} · {request.DurationText}";
        RequestHeadersBox.Text = FormatHeaders(request.RequestHeaders);
        ResponseHeadersBox.Text = FormatHeaders(request.ResponseHeaders);
        QueryBox.Text = string.Join(Environment.NewLine, request.QueryParameters().Select(pair => $"{pair.Key}: {pair.Value}"));
        RequestBodyBox.Text = request.RequestBody ?? "(No request body)";
        ResponseDataViewer.SetContent(request.ResponseBody ?? "Loading response body…", request.ResponseContentType);
        CookiesBox.Text = string.IsNullOrEmpty(request.Cookies()) ? "(No cookies available)" : request.Cookies();
        TimingBox.Text = $"Started: {request.StartedAt:0.000}s{Environment.NewLine}Duration: {request.DurationText}{Environment.NewLine}Status: {request.StatusText}{(string.IsNullOrEmpty(request.FailureReason) ? string.Empty : $"{Environment.NewLine}Failure: {request.FailureReason}")}";
        OpenInRestButton.IsEnabled = true;
        JwtInspectButton.IsEnabled = TryGetAuthorizationHeader(request, out var authorizationHeader) && JwtTokenInspector.IsBearerJwt(authorizationHeader);
        ClearJwtDetail();
        PolicyAnalysisBox.Text = BuildPolicyReport(request).ToDisplayText();
    }

    private void ClearDetail()
    {
        DetailMethodText.Text = "Select a request"; DetailUrlText.Text = string.Empty; DetailStatusText.Text = "No request selected"; DetailTimingText.Text = string.Empty;
        RequestHeadersBox.Text = ResponseHeadersBox.Text = QueryBox.Text = RequestBodyBox.Text = CookiesBox.Text = TimingBox.Text = string.Empty;
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

    private static string FormatHeaders(IReadOnlyDictionary<string, string> headers) =>
        headers.Count == 0
            ? "(No headers available)"
            : string.Join(Environment.NewLine, headers.Select(header =>
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    ? $"{header.Key}: {RedactAuthorization(header.Value)}"
                    : $"{header.Key}: {header.Value}"));

    private static string RedactAuthorization(string value) =>
        JwtTokenInspector.IsBearerJwt(value) ? "Bearer •••• (JWT detected)" :
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? "Bearer ••••" :
        "••••";

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
