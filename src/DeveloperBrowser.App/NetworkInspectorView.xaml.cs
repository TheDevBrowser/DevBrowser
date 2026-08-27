using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DeveloperBrowser.Core.Security;

namespace DeveloperBrowser.App;

public partial class NetworkInspectorView : UserControl
{
    private NetworkCaptureService? _capture;
    private ICollectionView? _requestsView;
    private CapturedNetworkRequest? _selectedRequest;

    public event EventHandler<CapturedNetworkRequest>? OpenInRestClientRequested;
    public event EventHandler? CloseRequested;

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

    private async void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRequest = RequestList.SelectedItem as CapturedNetworkRequest;
        if (_selectedRequest is null) { ClearDetail(); return; }
        PopulateDetail(_selectedRequest);
        if (_capture is not null)
        {
            await _capture.EnsureResponseBodyAsync(_selectedRequest);
            if (_selectedRequest == RequestList.SelectedItem) ResponseBodyBox.Text = _selectedRequest.ResponseBody ?? "(Response body is not available yet.)";
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
        ResponseBodyBox.Text = request.ResponseBody ?? "Loading response body…";
        CookiesBox.Text = string.IsNullOrEmpty(request.Cookies()) ? "(No cookies available)" : request.Cookies();
        TimingBox.Text = $"Started: {request.StartedAt:0.000}s{Environment.NewLine}Duration: {request.DurationText}{Environment.NewLine}Status: {request.StatusText}{(string.IsNullOrEmpty(request.FailureReason) ? string.Empty : $"{Environment.NewLine}Failure: {request.FailureReason}")}";
        OpenInRestButton.IsEnabled = true;
        JwtInspectButton.IsEnabled = TryGetAuthorizationHeader(request, out var authorizationHeader) && JwtTokenInspector.IsBearerJwt(authorizationHeader);
        ClearJwtDetail();
    }

    private void ClearDetail()
    {
        DetailMethodText.Text = "Select a request"; DetailUrlText.Text = string.Empty; DetailStatusText.Text = "No request selected"; DetailTimingText.Text = string.Empty;
        RequestHeadersBox.Text = ResponseHeadersBox.Text = QueryBox.Text = RequestBodyBox.Text = ResponseBodyBox.Text = CookiesBox.Text = TimingBox.Text = string.Empty;
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
}
