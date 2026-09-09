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
    private readonly Dictionary<string, List<HarEntry>> _liveRedirectChains = new();
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
        Entries.Clear(); _inFlight.Clear(); _liveRedirectChains.Clear();
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
        Entries.Clear(); _inFlight.Clear(); _liveRedirectChains.Clear();
        using var rawDocument = JsonDocument.Parse(json);
        var rawEntries = rawDocument.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().Select(item => item.GetRawText()).ToArray();
        for (var index = 0; index < file.Log.Entries.Count; index++)
            Entries.Add(FromHar(file.Log.Entries[index], index < rawEntries.Length ? rawEntries[index] : null));
        BuildRedirectChains(Entries);
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
        HarEntry? redirectedFrom = null;
        if (root.TryGetProperty("redirectResponse", out var redirectResponse) && _inFlight.TryGetValue(requestId, out redirectedFrom))
        {
            ApplyResponseMetadata(redirectedFrom, redirectResponse);
            redirectedFrom.RedirectUrl = url;
            redirectedFrom.DurationMs = TimestampDelta(redirectedFrom, root);
            redirectedFrom.ResponseBodySize = redirectResponse.TryGetProperty("encodedDataLength", out var redirectSize) ? (long)redirectSize.GetDouble() : -1;
            redirectedFrom.TransferSize = Math.Max(0, redirectedFrom.ResponseBodySize);
        }
        var entry = new HarEntry
        {
            Id = requestId,
            StartedAt = ToDateTimeOffset(root),
            MonotonicStartedAt = root.TryGetProperty("timestamp", out var startedTimestamp) ? startedTimestamp.GetDouble() : null,
            PageUrl = root.TryGetProperty("documentURL", out var documentUrl) ? documentUrl.GetString() ?? string.Empty : string.Empty,
            Method = request.GetProperty("method").GetString() ?? "GET",
            Url = url,
            ResourceType = root.TryGetProperty("type", out var type) ? type.GetString() ?? "Other" : "Other",
            RequestBody = request.TryGetProperty("postData", out var postData) ? postData.GetString() : null
        };
        entry.RequestBodySize = entry.RequestBody is null ? 0 : Encoding.UTF8.GetByteCount(entry.RequestBody);
        if (request.TryGetProperty("headers", out var headers)) Merge(entry.RequestHeaders, headers);
        _inFlight[requestId] = entry;
        Entries.Add(entry);
        if (redirectedFrom is not null)
        {
            if (!_liveRedirectChains.TryGetValue(requestId, out var chain)) _liveRedirectChains[requestId] = chain = [redirectedFrom];
            chain.Add(entry);
            var urls = chain.Select(item => item.Url).ToList();
            for (var step = 0; step < chain.Count; step++) { chain[step].RedirectChain.Clear(); chain[step].RedirectChain.AddRange(urls); chain[step].RedirectStep = step; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRequestExtraInfo(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var entry))
        {
            if (root.TryGetProperty("headers", out var headers)) Merge(entry.RequestHeaders, headers);
            if (root.TryGetProperty("headersText", out var headersText) && headersText.ValueKind == JsonValueKind.String)
                entry.RequestHeadersSize = Encoding.UTF8.GetByteCount(headersText.GetString() ?? string.Empty);
        }
    }

    private void OnResponseReceived(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_inFlight.TryGetValue(requestId, out var entry)) return;
        var response = root.GetProperty("response");
        ApplyResponseMetadata(entry, response);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnResponseExtraInfo(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (_inFlight.TryGetValue(requestId, out var entry))
        {
            if (root.TryGetProperty("headers", out var headers)) Merge(entry.ResponseHeaders, headers);
            if (root.TryGetProperty("headersText", out var headersText) && headersText.ValueKind == JsonValueKind.String)
                entry.ResponseHeadersSize = Encoding.UTF8.GetByteCount(headersText.GetString() ?? string.Empty);
        }
    }

    private void OnLoadingFinished(CoreWebView2 webView, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!Capturing(webView)) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var root = document.RootElement;
        var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_inFlight.TryGetValue(requestId, out var entry)) return;
        entry.DurationMs = TimestampDelta(entry, root);
        if (entry.ResponseHeadersEndMs is not null)
            entry.Timings.Receive = Math.Max(0, entry.DurationMs - entry.ResponseHeadersEndMs.Value);
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
            var encoded = root.TryGetProperty("base64Encoded", out var encodedElement) && encodedElement.GetBoolean();
            entry.ResponseBodyWasBase64 = encoded;
            entry.EncodedResponseBody = encoded ? body : null;
            var decoded = DecodeResponseBody(body, entry.ContentType, encoded);
            entry.ResponseBody = decoded.Text;
            entry.IsBinaryResponse = decoded.Binary;
            if (!string.IsNullOrEmpty(body))
                entry.DecodedContentSize = encoded ? Convert.FromBase64String(body).LongLength : Encoding.UTF8.GetByteCount(body);
        }
        catch { /* Some browser responses cannot be retrieved; HAR permits omitted content text. */ }
    }

    private static double TimestampDelta(HarEntry entry, JsonElement root)
    {
        if (entry.MonotonicStartedAt is not null && root.TryGetProperty("timestamp", out var timestamp))
            return Math.Max(0, (timestamp.GetDouble() - entry.MonotonicStartedAt.Value) * 1000);
        return Math.Max(0, (DateTimeOffset.UtcNow - entry.StartedAt).TotalMilliseconds);
    }

    private static void ApplyTiming(HarEntry entry, JsonElement timing)
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
        var headersEnd = TimingPoint(timing, "receiveHeadersEnd");

        entry.Timings = new HarTimings
        {
            Blocked = FirstAvailable(dnsStart, connectStart, sendStart),
            Dns = PhaseDuration(dnsStart, dnsEnd),
            Connect = PhaseDuration(connectStart, connectEnd),
            Ssl = PhaseDuration(sslStart, sslEnd),
            Send = PhaseDuration(sendStart, sendEnd),
            Wait = sendEnd >= 0 && headersStart >= 0 ? Math.Max(0, headersStart - sendEnd) : -1,
            Receive = -1
        };
        entry.ResponseHeadersEndMs = headersEnd >= 0 ? headersEnd : headersStart >= 0 ? headersStart : null;
    }

    private static void ApplyResponseMetadata(HarEntry entry, JsonElement response)
    {
        entry.Status = response.TryGetProperty("status", out var status) ? (int)status.GetDouble() : 0;
        entry.StatusDescription = response.TryGetProperty("statusText", out var statusText) ? statusText.GetString() ?? string.Empty : string.Empty;
        entry.MimeType = response.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;
        entry.HttpVersion = response.TryGetProperty("protocol", out var protocol) ? protocol.GetString() ?? "HTTP/1.1" : "HTTP/1.1";
        entry.RemoteAddress = response.TryGetProperty("remoteIPAddress", out var address) ? address.GetString() : null;
        entry.RemotePort = response.TryGetProperty("remotePort", out var port) ? port.GetInt32() : null;
        entry.CacheSource = response.TryGetProperty("fromServiceWorker", out var worker) && worker.GetBoolean() ? "Service worker" :
            response.TryGetProperty("fromDiskCache", out var disk) && disk.GetBoolean() ? "Disk cache" :
            response.TryGetProperty("fromPrefetchCache", out var prefetch) && prefetch.GetBoolean() ? "Prefetch cache" : "Network";
        if (response.TryGetProperty("timing", out var timing)) ApplyTiming(entry, timing);
        if (response.TryGetProperty("headers", out var headers)) Merge(entry.ResponseHeaders, headers);
    }

    private static double TimingPoint(JsonElement timing, string name) =>
        timing.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : -1;

    private static double PhaseDuration(double start, double end) => start >= 0 && end >= start ? end - start : -1;

    private static double FirstAvailable(params double[] values)
    {
        foreach (var value in values) if (value >= 0) return value;
        return -1;
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
            Headers = entry.RequestHeaderItems.Count > 0 ? entry.RequestHeaderItems : Headers(entry.RequestHeaders), QueryString = entry.QueryItems.Count > 0 ? entry.QueryItems : entry.QueryParameters().Select(pair => new HarHeader { Name = pair.Key, Value = pair.Value }).ToList(),
            Cookies = entry.RequestCookieItems.Count > 0 ? entry.RequestCookieItems : Cookies(entry.RequestHeaders), HeadersSize = entry.RequestHeadersSize,
            BodySize = entry.RequestBodySize >= 0 ? entry.RequestBodySize : entry.RequestBody is null ? 0 : Encoding.UTF8.GetByteCount(entry.RequestBody),
            PostData = string.IsNullOrEmpty(entry.RequestBody) ? null : new HarPostData { MimeType = entry.RequestContentType == "—" ? string.Empty : entry.RequestContentType, Text = entry.RequestBody }
        },
        Response = new HarResponse
        {
            Status = entry.Status, StatusText = entry.StatusDescription, HttpVersion = entry.HttpVersion, RedirectUrl = entry.RedirectUrl,
            Headers = entry.ResponseHeaderItems.Count > 0 ? entry.ResponseHeaderItems : Headers(entry.ResponseHeaders), Cookies = entry.ResponseCookieItems.Count > 0 ? entry.ResponseCookieItems : Cookies(entry.ResponseHeaders),
            HeadersSize = entry.ResponseHeadersSize, BodySize = entry.ResponseBodySize >= 0 ? entry.ResponseBodySize : entry.TransferSize,
            Content = new HarContent { Size = entry.DecodedContentSize >= 0 ? entry.DecodedContentSize : entry.TransferSize, Compression = entry.CompressionSavings >= 0 ? entry.CompressionSavings : null, MimeType = entry.ContentType == "—" ? string.Empty : entry.ContentType, Text = entry.ResponseBodyWasBase64 ? entry.EncodedResponseBody : entry.ResponseBody, Encoding = entry.ResponseBodyWasBase64 ? "base64" : null }
        },
        Cache = entry.CacheMetadata, Timings = ExportTimings(entry), ResourceType = entry.ResourceType, Failure = entry.FailureReason, RemoteAddress = entry.RemoteAddress, RemotePort = entry.RemotePort
    };

    private static HarTimings ExportTimings(HarEntry entry) => new()
    {
        Blocked = entry.Timings.Blocked,
        Dns = entry.Timings.Dns,
        Connect = entry.Timings.Connect,
        Ssl = entry.Timings.Ssl,
        Send = Math.Max(0, entry.Timings.Send),
        Wait = entry.Timings.Wait >= 0 ? entry.Timings.Wait : entry.DurationMs,
        Receive = Math.Max(0, entry.Timings.Receive),
        Comment = entry.Timings.Comment
    };

    private static HarEntry FromHar(HarFileEntry entry, string? originalJson)
    {
        var encoded = entry.Response.Content.Encoding?.Equals("base64", StringComparison.OrdinalIgnoreCase) == true;
        var (responseBody, binary) = DecodeResponseBody(entry.Response.Content.Text, entry.Response.Content.MimeType, encoded);
        var result = new HarEntry
        {
            StartedAt = entry.StartedDateTime, Method = entry.Request.Method, Url = entry.Request.Url, ResourceType = entry.ResourceType ?? GuessResourceType(entry.Response.Content.MimeType),
            HttpVersion = entry.Response.HttpVersion ?? entry.Request.HttpVersion, PageUrl = string.Empty, RequestBody = entry.Request.PostData?.Text,
            MimeType = entry.Response.Content.MimeType, Status = entry.Response.Status, DurationMs = entry.Time, TransferSize = TransferredSize(entry.Response),
            ResponseBody = responseBody, FailureReason = entry.Failure, RemoteAddress = entry.RemoteAddress, RemotePort = entry.RemotePort,
            Timings = entry.Timings, OriginalEntryJson = originalJson, ResponseBodyWasBase64 = encoded,
            EncodedResponseBody = encoded ? entry.Response.Content.Text : null, IsBinaryResponse = binary,
            StatusDescription = entry.Response.StatusText, RedirectUrl = entry.Response.RedirectUrl,
            RequestHeadersSize = entry.Request.HeadersSize, RequestBodySize = entry.Request.BodySize,
            ResponseHeadersSize = entry.Response.HeadersSize, ResponseBodySize = entry.Response.BodySize,
            DecodedContentSize = entry.Response.Content.Size, CompressionSavings = entry.Response.Content.Compression ?? -1,
            CacheMetadata = entry.Cache
        };
        foreach (var header in entry.Request.Headers) { result.RequestHeaderItems.Add(header); result.RequestHeaders[header.Name] = header.Value; }
        foreach (var header in entry.Response.Headers) { result.ResponseHeaderItems.Add(header); result.ResponseHeaders[header.Name] = header.Value; }
        result.QueryItems.AddRange(entry.Request.QueryString);
        result.RequestCookieItems.AddRange(entry.Request.Cookies);
        result.ResponseCookieItems.AddRange(entry.Response.Cookies);
        return result;
    }

    private static void BuildRedirectChains(IReadOnlyList<HarEntry> entries)
    {
        var targetIndexes = new HashSet<int>();
        for (var index = 0; index < entries.Count; index++)
            if (!string.IsNullOrWhiteSpace(entries[index].RedirectUrl))
            {
                var target = FindNext(entries, index + 1, entries[index].RedirectUrl);
                if (target >= 0) targetIndexes.Add(target);
            }

        for (var start = 0; start < entries.Count; start++)
        {
            if (targetIndexes.Contains(start) || string.IsNullOrWhiteSpace(entries[start].RedirectUrl)) continue;
            var members = new List<HarEntry> { entries[start] };
            var current = start;
            var visited = new HashSet<int> { start };
            while (!string.IsNullOrWhiteSpace(entries[current].RedirectUrl))
            {
                var next = FindNext(entries, current + 1, entries[current].RedirectUrl);
                if (next < 0 || !visited.Add(next)) break;
                members.Add(entries[next]); current = next;
            }
            var urls = members.Select(item => item.Url).ToList();
            for (var step = 0; step < members.Count; step++) { members[step].RedirectChain.AddRange(urls); members[step].RedirectStep = step; }
        }
    }

    private static int FindNext(IReadOnlyList<HarEntry> entries, int start, string url)
    {
        for (var index = start; index < entries.Count; index++)
            if (entries[index].Url.Equals(url, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static long TransferredSize(HarResponse response)
    {
        if (response.BodySize >= 0 && response.HeadersSize >= 0) return response.BodySize + response.HeadersSize;
        if (response.BodySize >= 0) return response.BodySize;
        return Math.Max(0, response.Content.Size);
    }

    private static (string? Text, bool Binary) DecodeResponseBody(string? content, string? mimeType, bool encoded)
    {
        if (!encoded || string.IsNullOrEmpty(content)) return (content, false);
        try
        {
            var bytes = Convert.FromBase64String(content);
            if (!IsTextualContent(mimeType)) return (null, true);
            return (Encoding.UTF8.GetString(bytes), false);
        }
        catch (FormatException) { return (content, false); }
    }

    private static bool IsTextualContent(string? mimeType)
    {
        var type = mimeType ?? string.Empty;
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("xml", StringComparison.OrdinalIgnoreCase) || type.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("svg", StringComparison.OrdinalIgnoreCase) || type.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static List<HarHeader> Headers(IReadOnlyDictionary<string, string> headers) => headers.Select(header => new HarHeader { Name = header.Key, Value = header.Value }).ToList();
    private static List<HarCookie> Cookies(IReadOnlyDictionary<string, string> headers) => headers.Where(header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Select(header => new HarCookie { Name = header.Key, Value = header.Value }).ToList();
    private static string GuessResourceType(string? mimeType) => (mimeType ?? string.Empty).ToLowerInvariant() switch { var text when text.Contains("javascript") => "Script", var text when text.Contains("css") => "Stylesheet", var text when text.StartsWith("image/") => "Image", var text when text.StartsWith("font/") => "Font", var text when text.StartsWith("audio/") || text.StartsWith("video/") => "Media", var text when text.Contains("html") => "Document", _ => "Other" };
}
