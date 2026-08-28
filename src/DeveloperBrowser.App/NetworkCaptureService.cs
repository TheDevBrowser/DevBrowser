using System.Collections.ObjectModel;
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
    }

    public void Clear()
    {
        Requests.Clear();
        _inFlight.Clear();
        _webviewsByRequestId.Clear();
    }

    public async Task EnsureResponseBodyAsync(CapturedNetworkRequest request)
    {
        if (request.ResponseBody is not null || !_webviewsByRequestId.TryGetValue(request.RequestId, out var webView)) return;
        try
        {
            var result = await webView.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = request.RequestId }));
            using var document = JsonDocument.Parse(result);
            var body = document.RootElement.GetProperty("body").GetString() ?? string.Empty;
            request.ResponseBody = document.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean() ? "(Binary response body)" : body;
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
        var requestId = root.GetProperty("requestId").GetString() ?? Guid.NewGuid().ToString("N");
        var captured = new CapturedNetworkRequest
        {
            RequestId = requestId,
            Method = request.GetProperty("method").GetString() ?? "GET",
            Url = url,
            ResourceType = root.TryGetProperty("type", out var type) ? type.GetString() ?? "Other" : "Other",
            StartedAt = root.TryGetProperty("timestamp", out var timestamp) ? timestamp.GetDouble() : 0,
            PageUrl = root.TryGetProperty("documentURL", out var documentUrl) ? documentUrl.GetString() : null,
            RequestHeaders = Headers(request.GetProperty("headers")),
            RequestBody = request.TryGetProperty("postData", out var postData) ? postData.GetString() : null
        };
        _inFlight[requestId] = captured;
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
        captured.SetResponse(response.GetProperty("status").GetInt32(), Headers(response.GetProperty("headers")));
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
        while (Requests.Count > MaxRequests) Requests.RemoveAt(0);
    }

    private static Dictionary<string, string> Headers(JsonElement headers) => headers.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.OrdinalIgnoreCase);
}
