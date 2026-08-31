using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace DeveloperBrowser.App;

/// <summary>Creates standard HAR 1.2 sessions from a single active WebView2 tab.</summary>
public sealed class HarCaptureService
{
    private readonly Dictionary<string, HarEntry> _inFlight = new();
    private readonly HashSet<CoreWebView2> _attached = [];
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _receivers = [];
    private CoreWebView2? _activeWebView;
    private DateTimeOffset _startedAt;

    public ObservableCollection<HarEntry> Entries { get; } = [];
    public bool IsCapturing { get; private set; }
    public DateTimeOffset StartedAt => _startedAt;
    public string? SourceName { get; private set; }
    public event EventHandler? Changed;

    public async Task StartAsync(CoreWebView2 webView)
    {
        await AttachAsync(webView);
        Entries.Clear(); _inFlight.Clear();
        _activeWebView = webView;
        _startedAt = DateTimeOffset.UtcNow;
        SourceName = "Live browser capture";
        IsCapturing = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        if (_activeWebView is not null)
            foreach (var entry in Entries.Where(entry => entry.ResponseBody is null && entry.Status > 0).ToArray())
                await TryPopulateBodyAsync(_activeWebView, entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        HarFile? file;
        try { file = JsonSerializer.Deserialize<HarFile>(json, JsonOptions); }
        catch (JsonException exception) { throw new InvalidOperationException("This file is not valid HAR JSON.", exception); }
        if (file?.Log is null || !file.Log.Version.StartsWith("1.", StringComparison.Ordinal) || file.Log.Entries is null)
            throw new InvalidOperationException("This file does not contain a valid HAR 1.x log and entries collection.");

        IsCapturing = false;
        Entries.Clear(); _inFlight.Clear();
        foreach (var source in file.Log.Entries) Entries.Add(FromHar(source));
        _startedAt = Entries.Count == 0 ? DateTimeOffset.UtcNow : Entries.Min(entry => entry.StartedAt);
        SourceName = Path.GetFileName(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(string path)
    {
        var file = new HarFile
        {
            Log = new HarLog
            {
                Entries = Entries.Select(ToHar).ToList()
            }
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, JsonOptions), Encoding.UTF8);
    }

    private async Task AttachAsync(CoreWebView2 webView)
    {
        if (!_attached.Add(webView)) return;
        await webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        Subscribe(webView, "Network.requestWillBeSent", OnRequestWillBeSent);
        Subscribe(webView, "Network.requestWillBeSentExtraInfo", OnRequestExtraInfo);
        Subscribe(webView, "Network.responseReceived", OnResponseReceived);
        Subscribe(webView, "Network.responseReceivedExtraInfo", OnResponseExtraInfo);
        Subscribe(webView, "Network.loadingFinished", OnLoadingFinished);
        Subscribe(webView, "Network.loadingFailed", OnLoadingFailed);
    }

    private void Subscribe(CoreWebView2 webView, string eventName, Action<CoreWebView2, CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler)
    {
        var receiver = webView.GetDevToolsProtocolEventReceiver(eventName);
        receiver.DevToolsProtocolEventReceived += (_, args) => handler(webView, args);
        _receivers.Add(receiver);
    }

    private bool Capturing(CoreWebView2 webView) => IsCapturing && ReferenceEquals(webView, _activeWebView);

    private void OnRequestWillBeSent(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var request = root.GetProperty("request");
        var url = request.GetProperty("url").GetString() ?? string.Empty;
        if (!IsHttp(url)) return;
        var requestId = root.GetProperty("requestId").GetString() ?? Guid.NewGuid().ToString("N");
        var entry = new HarEntry
        {
            Id = requestId,
            StartedAt = ToDateTimeOffset(root),
            PageUrl = root.TryGetProperty("documentURL", out var documentUrl) ? documentUrl.GetString() ?? string.Empty : string.Empty,
            Method = request.GetProperty("method").GetString() ?? "GET",
            Url = url,
            ResourceType = root.TryGetProperty("type", out var type) ? type.GetString() ?? "Other" : "Other",
            RequestBody = request.TryGetProperty("postData", out var postData) ? postData.GetString() : null
        };
        if (request.TryGetProperty("headers", out var headers)) Merge(entry.RequestHeaders, headers);
        _inFlight[requestId] = entry;
        Entries.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRequestExtraInfo(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var entry) && root.TryGetProperty("headers", out var headers)) Merge(entry.RequestHeaders, headers);
    }

    private void OnResponseReceived(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_inFlight.TryGetValue(requestId, out var entry)) return;
        var response = root.GetProperty("response");
        entry.Status = response.TryGetProperty("status", out var status) ? (int)status.GetDouble() : 0;
        entry.MimeType = response.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;
        entry.HttpVersion = response.TryGetProperty("protocol", out var protocol) ? protocol.GetString() ?? "HTTP/1.1" : "HTTP/1.1";
        entry.RemoteAddress = response.TryGetProperty("remoteIPAddress", out var address) ? address.GetString() : null;
        entry.RemotePort = response.TryGetProperty("remotePort", out var port) ? port.GetInt32() : null;
        if (response.TryGetProperty("headers", out var headers)) Merge(entry.ResponseHeaders, headers);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnResponseExtraInfo(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var entry) && root.TryGetProperty("headers", out var headers)) Merge(entry.ResponseHeaders, headers);
    }

    private void OnLoadingFinished(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_inFlight.TryGetValue(requestId, out var entry)) return;
        entry.DurationMs = TimestampDelta(entry, root);
        entry.TransferSize = root.TryGetProperty("encodedDataLength", out var size) ? (long)size.GetDouble() : 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoadingFailed(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_inFlight.TryGetValue(requestId, out var entry)) return;
        entry.DurationMs = TimestampDelta(entry, root);
        entry.FailureReason = root.TryGetProperty("errorText", out var error) ? error.GetString() : "Request failed";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task TryPopulateBodyAsync(CoreWebView2 webView, HarEntry entry)
    {
        try
        {
            var response = await webView.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = entry.Id }));
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var body = root.TryGetProperty("body", out var text) ? text.GetString() : null;
            if (root.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()) body = null;
            entry.ResponseBody = body;
        }
        catch { /* Some browser responses cannot be retrieved; HAR permits omitted content text. */ }
    }

    private static double TimestampDelta(HarEntry entry, JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var timestamp)) return 0;
        var ended = timestamp.GetDouble();
        // CDP monotonic timestamps cannot be compared to startedDateTime; they are only used when a matching start is available in the transport.
        return Math.Max(0, (DateTimeOffset.UtcNow - entry.StartedAt).TotalMilliseconds);
    }

    private static void Merge(IDictionary<string, string> destination, JsonElement source)
    {
        foreach (var property in source.EnumerateObject()) destination[property.Name] = property.Value.ToString();
    }

    private static bool IsHttp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    private static DateTimeOffset ToDateTimeOffset(JsonElement root) => root.TryGetProperty("wallTime", out var wall) ? DateTimeOffset.FromUnixTimeMilliseconds((long)(wall.GetDouble() * 1000)) : DateTimeOffset.UtcNow;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static HarFileEntry ToHar(HarEntry entry) => new()
    {
        StartedDateTime = entry.StartedAt,
        Time = entry.DurationMs,
        Request = new HarRequest
        {
            Method = entry.Method, Url = entry.Url, HttpVersion = entry.HttpVersion,
            Headers = Headers(entry.RequestHeaders), QueryString = entry.QueryParameters().Select(pair => new HarHeader { Name = pair.Key, Value = pair.Value }).ToList(),
            Cookies = Cookies(entry.RequestHeaders), BodySize = entry.RequestBody?.Length ?? 0,
            PostData = string.IsNullOrEmpty(entry.RequestBody) ? null : new HarPostData { MimeType = entry.RequestContentType == "—" ? string.Empty : entry.RequestContentType, Text = entry.RequestBody }
        },
        Response = new HarResponse
        {
            Status = entry.Status, StatusText = entry.FailureReason ?? string.Empty, HttpVersion = entry.HttpVersion,
            Headers = Headers(entry.ResponseHeaders), Cookies = Cookies(entry.ResponseHeaders), BodySize = entry.TransferSize,
            Content = new HarContent { Size = entry.TransferSize, MimeType = entry.ContentType == "—" ? string.Empty : entry.ContentType, Text = entry.ResponseBody }
        },
        Timings = new HarTimings { Wait = entry.DurationMs }, ResourceType = entry.ResourceType, Failure = entry.FailureReason, RemoteAddress = entry.RemoteAddress, RemotePort = entry.RemotePort
    };

    private static HarEntry FromHar(HarFileEntry entry)
    {
        var result = new HarEntry
        {
            StartedAt = entry.StartedDateTime, Method = entry.Request.Method, Url = entry.Request.Url, ResourceType = entry.ResourceType ?? GuessResourceType(entry.Response.Content.MimeType),
            HttpVersion = entry.Response.HttpVersion ?? entry.Request.HttpVersion, PageUrl = string.Empty, RequestBody = entry.Request.PostData?.Text,
            MimeType = entry.Response.Content.MimeType, Status = entry.Response.Status, DurationMs = entry.Time, TransferSize = entry.Response.Content.Size >= 0 ? entry.Response.Content.Size : Math.Max(0, entry.Response.BodySize),
            ResponseBody = entry.Response.Content.Text, FailureReason = entry.Failure, RemoteAddress = entry.RemoteAddress, RemotePort = entry.RemotePort
        };
        foreach (var header in entry.Request.Headers) result.RequestHeaders[header.Name] = header.Value;
        foreach (var header in entry.Response.Headers) result.ResponseHeaders[header.Name] = header.Value;
        return result;
    }

    private static List<HarHeader> Headers(IReadOnlyDictionary<string, string> headers) => headers.Select(header => new HarHeader { Name = header.Key, Value = header.Value }).ToList();
    private static List<HarCookie> Cookies(IReadOnlyDictionary<string, string> headers) => headers.Where(header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Select(header => new HarCookie { Name = header.Key, Value = header.Value }).ToList();
    private static string GuessResourceType(string? mimeType) => (mimeType ?? string.Empty).ToLowerInvariant() switch { var text when text.Contains("javascript") => "Script", var text when text.Contains("css") => "Stylesheet", var text when text.StartsWith("image/") => "Image", var text when text.StartsWith("font/") => "Font", var text when text.StartsWith("audio/") || text.StartsWith("video/") => "Media", var text when text.Contains("html") => "Document", _ => "Other" };
}
