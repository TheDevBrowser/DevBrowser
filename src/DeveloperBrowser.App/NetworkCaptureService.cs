using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace DeveloperBrowser.App;

/// <summary>Captures Chromium network events without coupling capture to the WPF inspector.</summary>
public sealed class NetworkCaptureService
{
    private const int MaxRequests = 500;
    private readonly Dictionary<string, CapturedNetworkRequest> _inFlight = new();
    private readonly Dictionary<string, CoreWebView2> _webviewsByRequestId = new();
    private readonly HashSet<CoreWebView2> _attachedWebViews = [];
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _receivers = [];
    private long _requestSequence;
    private CoreWebView2? _activeWebView;

    public bool PreserveLog { get; set; }

    public ObservableCollection<CapturedNetworkRequest> Requests { get; } = [];

    public async Task AttachAsync(CoreWebView2 webView)
    {
        if (!_attachedWebViews.Add(webView)) return;
        await webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        Subscribe(webView, "Network.requestWillBeSent", OnRequestWillBeSent);
        Subscribe(webView, "Network.requestWillBeSentExtraInfo", OnRequestWillBeSentExtraInfo);
        Subscribe(webView, "Network.responseReceived", OnResponseReceived);
        Subscribe(webView, "Network.responseReceivedExtraInfo", OnResponseReceivedExtraInfo);
        Subscribe(webView, "Network.loadingFinished", OnLoadingFinished);
        Subscribe(webView, "Network.loadingFailed", OnLoadingFailed);
        webView.NavigationStarting += (_, args) =>
        {
            if (!PreserveLog && !args.IsRedirected && ReferenceEquals(webView, _activeWebView)) Clear(includePinned: false);
        };
    }

    public void SetActiveWebView(CoreWebView2? webView)
    {
        if (ReferenceEquals(_activeWebView, webView)) return;
        _activeWebView = webView;
        if (!PreserveLog) Clear(includePinned: false);
    }

    public void Clear(bool includePinned = true)
    {
        if (includePinned)
        {
            Requests.Clear();
            _inFlight.Clear();
            _webviewsByRequestId.Clear();
            return;
        }

        var removedIds = Requests.Where(request => !request.IsPinned).Select(request => request.RequestId).ToHashSet();
        for (var index = Requests.Count - 1; index >= 0; index--)
            if (!Requests[index].IsPinned) Requests.RemoveAt(index);
        foreach (var requestId in removedIds) _webviewsByRequestId.Remove(requestId);
        foreach (var protocolId in _inFlight.Where(pair => !pair.Value.IsPinned).Select(pair => pair.Key).ToArray())
            _inFlight.Remove(protocolId);
    }

    public async Task EnsureResponseBodyAsync(CapturedNetworkRequest request)
    {
        if (request.ResponseBody is not null || !_webviewsByRequestId.TryGetValue(request.RequestId, out var webView)) return;
        try
        {
            var result = await webView.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = request.ProtocolRequestId ?? request.RequestId }));
            using var document = JsonDocument.Parse(result);
            var body = document.RootElement.GetProperty("body").GetString() ?? string.Empty;
            var encoded = document.RootElement.TryGetProperty("base64Encoded", out var encodedElement) && encodedElement.GetBoolean();
            request.ResponseBody = DecodeResponseBody(body, request.ResponseContentType, encoded);
        }
        catch (Exception)
        {
            request.ResponseBody = "(Response body is unavailable for this request.)";
        }
    }

    private void Subscribe(CoreWebView2 webView, string eventName, Action<CoreWebView2, CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler)
    {
        var receiver = webView.GetDevToolsProtocolEventReceiver(eventName);
        receiver.DevToolsProtocolEventReceived += (_, args) => handler(webView, args);
        _receivers.Add(receiver);
    }

    private void OnRequestWillBeSent(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var request = root.GetProperty("request");
        var url = request.GetProperty("url").GetString() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        var protocolRequestId = root.GetProperty("requestId").GetString() ?? Guid.NewGuid().ToString("N");
        var startedAt = root.TryGetProperty("timestamp", out var timestamp) ? timestamp.GetDouble() : 0;
        if (_inFlight.TryGetValue(protocolRequestId, out var redirectSource) && root.TryGetProperty("redirectResponse", out var redirectResponse))
        {
            ApplyResponseMetadata(redirectSource, redirectResponse, startedAt);
            redirectSource.SetFinished(startedAt);
        }
        var requestId = _inFlight.ContainsKey(protocolRequestId) ? $"{protocolRequestId}:{++_requestSequence}" : protocolRequestId;
        var captured = new CapturedNetworkRequest
        {
            RequestId = requestId,
            ProtocolRequestId = protocolRequestId,
            Method = request.GetProperty("method").GetString() ?? "GET",
            Url = url,
            ResourceType = root.TryGetProperty("type", out var type) ? type.GetString() ?? "Other" : "Other",
            StartedAt = startedAt,
            PageUrl = root.TryGetProperty("documentURL", out var documentUrl) ? documentUrl.GetString() : null,
            InitiatorType = InitiatorType(root),
            InitiatorUrl = InitiatorUrl(root),
            RequestHeaders = Headers(request.GetProperty("headers")),
            RequestBody = request.TryGetProperty("postData", out var postData) ? postData.GetString() : null
        };
        _inFlight[protocolRequestId] = captured;
        _webviewsByRequestId[requestId] = webView;
        AddRequest(captured);
    }

    private void OnRequestWillBeSentExtraInfo(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var captured) && root.TryGetProperty("headers", out var headers))
            captured.MergeRequestHeaders(Headers(headers));
    }

    private void OnResponseReceived(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_inFlight.TryGetValue(requestId, out var captured)) return;
        var response = root.GetProperty("response");
        var observedAt = root.TryGetProperty("timestamp", out var timestamp) ? timestamp.GetDouble() : (double?)null;
        ApplyResponseMetadata(captured, response, observedAt);
    }

    private void OnResponseReceivedExtraInfo(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var captured) && root.TryGetProperty("headers", out var headers))
            captured.MergeResponseHeaders(Headers(headers));
    }

    private void OnLoadingFinished(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var captured) && root.TryGetProperty("timestamp", out var timestamp)) captured.SetFinished(timestamp.GetDouble());
    }

    private void OnLoadingFailed(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var captured))
        {
            var blockedReason = root.TryGetProperty("blockedReason", out var blocked) ? blocked.GetString() : null;
            string? corsError = null;
            if (root.TryGetProperty("corsErrorStatus", out var corsStatus))
            {
                var error = corsStatus.TryGetProperty("corsError", out var cors) ? cors.GetString() : null;
                var parameter = corsStatus.TryGetProperty("failedParameter", out var failedParameter) ? failedParameter.GetString() : null;
                corsError = string.IsNullOrWhiteSpace(parameter) ? error : $"{error} ({parameter})";
            }
            captured.SetFailed(root.TryGetProperty("errorText", out var errorText) ? errorText.GetString() ?? "Failed" : "Failed", root.TryGetProperty("timestamp", out var timestamp) ? timestamp.GetDouble() : captured.StartedAt, blockedReason, corsError);
        }
    }

    private void AddRequest(CapturedNetworkRequest request)
    {
        Requests.Add(request);
        while (Requests.Count > MaxRequests)
        {
            var removable = Requests.Select((candidate, index) => (candidate, index)).FirstOrDefault(item => !item.candidate.IsPinned);
            if (removable.candidate is null) break;
            _webviewsByRequestId.Remove(removable.candidate.RequestId);
            Requests.RemoveAt(removable.index);
        }
    }

    private static Dictionary<string, string> Headers(JsonElement headers) => headers.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private static void ApplyResponseMetadata(CapturedNetworkRequest request, JsonElement response, double? observedAt)
    {
        var timing = response.TryGetProperty("timing", out var timingElement) ? ParseTiming(timingElement) : null;
        double? responseHeadersAt = observedAt;
        if (response.TryGetProperty("timing", out timingElement))
        {
            var requestTime = TimingPoint(timingElement, "requestTime");
            var headersEnd = TimingPoint(timingElement, "receiveHeadersEnd");
            var headersStart = TimingPoint(timingElement, "receiveHeadersStart");
            var relativeHeaders = headersEnd ?? headersStart;
            if (requestTime is not null && relativeHeaders is not null) responseHeadersAt = requestTime + relativeHeaders / 1000d;
        }

        var cacheSource = response.TryGetProperty("fromServiceWorker", out var worker) && worker.GetBoolean() ? "Service worker" :
            response.TryGetProperty("fromDiskCache", out var disk) && disk.GetBoolean() ? "Disk cache" :
            response.TryGetProperty("fromPrefetchCache", out var prefetch) && prefetch.GetBoolean() ? "Prefetch cache" : "Network";
        request.SetResponse(
            response.TryGetProperty("status", out var status) ? (int)status.GetDouble() : 0,
            response.TryGetProperty("headers", out var headers) ? Headers(headers) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            timing,
            responseHeadersAt,
            response.TryGetProperty("protocol", out var protocol) ? protocol.GetString() : null,
            response.TryGetProperty("remoteIPAddress", out var address) ? address.GetString() : null,
            response.TryGetProperty("remotePort", out var port) && port.ValueKind == JsonValueKind.Number ? port.GetInt32() : null,
            cacheSource,
            response.TryGetProperty("connectionReused", out var reused) && reused.GetBoolean());
    }

    private static NetworkTimingBreakdown ParseTiming(JsonElement timing)
    {
        var dnsStart = TimingPoint(timing, "dnsStart");
        var dnsEnd = TimingPoint(timing, "dnsEnd");
        var connectStart = TimingPoint(timing, "connectStart");
        var connectEnd = TimingPoint(timing, "connectEnd");
        var sslStart = TimingPoint(timing, "sslStart");
        var sslEnd = TimingPoint(timing, "sslEnd");
        var sendStart = TimingPoint(timing, "sendStart");
        var sendEnd = TimingPoint(timing, "sendEnd");
        var headersStart = TimingPoint(timing, "receiveHeadersStart");
        return new NetworkTimingBreakdown(
            FirstAvailable(dnsStart, connectStart, sendStart),
            PhaseDuration(dnsStart, dnsEnd),
            PhaseDuration(connectStart, connectEnd),
            PhaseDuration(sslStart, sslEnd),
            PhaseDuration(sendStart, sendEnd),
            sendEnd is not null && headersStart is not null ? Math.Max(0, headersStart.Value - sendEnd.Value) : null);
    }

    private static double? TimingPoint(JsonElement timing, string name) =>
        timing.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() >= 0 ? value.GetDouble() : null;

    private static double? PhaseDuration(double? start, double? end) => start is not null && end >= start ? end - start : null;

    private static double? FirstAvailable(params double?[] values)
    {
        foreach (var value in values) if (value is not null) return value;
        return null;
    }

    private static string DecodeResponseBody(string body, string? contentType, bool encoded)
    {
        if (!encoded || string.IsNullOrEmpty(body)) return body;
        try
        {
            var bytes = Convert.FromBase64String(body);
            return IsTextualContent(contentType) ? Encoding.UTF8.GetString(bytes) : "(Binary response body)";
        }
        catch (FormatException)
        {
            // Keep the browser-provided text available when an endpoint incorrectly marks it as Base64.
            return body;
        }
    }

    private static bool IsTextualContent(string? contentType)
    {
        var type = contentType ?? string.Empty;
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("html", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static string? InitiatorType(JsonElement root) => root.TryGetProperty("initiator", out var initiator) && initiator.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static string? InitiatorUrl(JsonElement root)
    {
        if (!root.TryGetProperty("initiator", out var initiator)) return null;
        if (initiator.TryGetProperty("url", out var directUrl)) return directUrl.GetString();
        if (!initiator.TryGetProperty("stack", out var stack) || !stack.TryGetProperty("callFrames", out var frames)) return null;
        foreach (var frame in frames.EnumerateArray())
            if (frame.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString())) return url.GetString();
        return null;
    }
}
