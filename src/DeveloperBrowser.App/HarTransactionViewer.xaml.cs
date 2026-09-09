using System.Text.Json;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeveloperBrowser.App;

public partial class HarTransactionViewer : UserControl
{
    private static readonly Brush Good = Frozen(97, 208, 149);
    private static readonly Brush Notice = Frozen(245, 176, 65);
    private static readonly Brush Bad = Frozen(239, 102, 102);
    private static readonly Brush Neutral = Frozen(142, 214, 255);

    public HarTransactionViewer() => InitializeComponent();

    public void SetEntry(HarEntry entry)
    {
        MethodText.Text = entry.Method;
        MethodText.Foreground = HttpMethodPalette.Foreground(entry.Method);
        MethodBadge.Background = HttpMethodPalette.Background(entry.Method);
        SummaryHostText.Text = entry.Domain;
        SummaryUrlText.Text = entry.Url;
        StatusText.Text = StatusLabel(entry);
        StatusBadge.Background = entry.IsFailed ? Frozen(111, 47, 57) : entry.IsRedirect ? Frozen(91, 65, 29) : Frozen(29, 100, 70);
        TypeText.Text = entry.ResourceType;
        DurationText.Text = entry.DurationText;
        SizeText.Text = entry.SizeText;
        ProtocolText.Text = entry.HttpVersion;
        InsightsList.ItemsSource = BuildInsights(entry);
        ConnectionViewer.SetItems(ConnectionItems(entry), "No connection metadata");
        CacheViewer.SetItems(CacheItems(entry), "No cache information recorded");
        RequestViewer.SetRequest(entry.Method, entry.Url, entry.QueryParameters(), entry.RequestBody, entry.RequestContentType,
            entry.RequestHeaders, entry.OrderedRequestHeaders, entry.RequestCookieItems);
        ResponseStatusText.Text = StatusLabel(entry);
        ResponseStatusBadge.Background = StatusBadge.Background;
        ResponseTypeText.Text = entry.ContentType == "—" ? "Unknown response type" : entry.ContentType;
        ResponseSummaryText.Text = $"{entry.SizeText} transferred · {entry.DurationText} · {entry.HttpVersion}";
        ResponseHeadersViewer.SetItems(HttpInspectorFormatting.Headers(entry.OrderedResponseHeaders), "No response headers");
        var responseCookies = entry.ResponseCookieItems.Count > 0
            ? HttpInspectorFormatting.Cookies(entry.ResponseCookieItems, "Response").ToList()
            : HttpInspectorFormatting.Cookies(new Dictionary<string, string>(), entry.ResponseHeaders).ToList();
        ResponseCookiesViewer.SetItems(responseCookies, "No response cookies");
        var displayedBody = entry.IsBinaryResponse
            ? $"Binary response body ({entry.ContentType}, {entry.SizeText}). The original Base64 payload is preserved in Raw and when exporting the HAR."
            : entry.ResponseBody ?? "(Response body was not included in this HAR entry.)";
        ResponseBodyViewer.SetContent(displayedBody, entry.IsBinaryResponse ? "text/plain" : entry.ContentType);
        ResponseHeadersMetaText.Text = CountLabel(entry.ResponseHeaderCount, "header");
        ResponseCookiesMetaText.Text = CountLabel(responseCookies.Count, "cookie");
        ResponseBodyMetaText.Text = entry.IsBinaryResponse ? BinaryBodyLabel(entry) : BodyLabel(entry.ResponseBody, entry.ContentType, entry.TransferSize);
        ResponseCookiesExpander.IsEnabled = responseCookies.Count > 0;
        ResponseBodyExpander.IsEnabled = entry.IsBinaryResponse || !string.IsNullOrWhiteSpace(entry.ResponseBody);
        ResponseHeadersExpander.IsExpanded = entry.IsFailed || entry.IsRedirect;
        ResponseBodyExpander.IsExpanded = entry.IsBinaryResponse || !string.IsNullOrWhiteSpace(entry.ResponseBody);
        PerformanceDurationText.Text = entry.DurationText;
        TimingExplanationText.Text = HasPhaseTimings(entry.Timings)
            ? "The source HAR includes phase-level timing. Unavailable phases are kept distinct from phases recorded as zero milliseconds."
            : "The source HAR contains only an overall elapsed time; phase-level timing was not recorded.";
        TimingViewer.SetItems(TimingItems(entry));
        RawViewer.SetContent(entry.OriginalEntryJson ?? JsonSerializer.Serialize(BuildRaw(entry), new JsonSerializerOptions { WriteIndented = true }), "application/json");
    }

    public void Clear()
    {
        MethodText.Text = "HTTP"; SummaryHostText.Text = "Select a request"; SummaryUrlText.Text = string.Empty;
        StatusText.Text = "—"; TypeText.Text = DurationText.Text = SizeText.Text = ProtocolText.Text = "—";
        InsightsList.ItemsSource = null; ConnectionViewer.SetItems(null); CacheViewer.SetItems(null); RequestViewer.Clear();
        ResponseHeadersViewer.SetItems(null); ResponseCookiesViewer.SetItems(null); ResponseBodyViewer.SetContent(null);
        PerformanceDurationText.Text = "—"; TimingViewer.SetItems(null); RawViewer.SetContent(null);
    }

    private static string StatusLabel(HarEntry entry) => entry.Status > 0 ? $"{entry.Status} {(string.IsNullOrWhiteSpace(entry.StatusDescription) ? StatusMeaning(entry.Status) : entry.StatusDescription)}" : "Failed";
    private static string StatusMeaning(int status) => status switch { >= 200 and < 300 => "Success", >= 300 and < 400 => "Redirect", >= 400 and < 500 => "Client error", >= 500 => "Server error", _ => "HTTP" };

    private static List<InsightItem> BuildInsights(HarEntry entry)
    {
        var items = new List<InsightItem>();
        if (entry.IsFailed) items.Add(new(entry.FailureReason ?? $"The server returned HTTP {entry.Status}.", Bad));
        else if (entry.IsRedirect) items.Add(new(entry.RedirectChain.Count > 1 ? $"Redirect step {entry.RedirectStep + 1} of {entry.RedirectChain.Count}; next destination: {entry.RedirectUrl}" : $"This response redirects to {entry.RedirectUrl}.", Notice));
        else items.Add(new("The exchange completed without a captured HTTP failure.", Good));
        if (entry.RequestHeaders.ContainsKey("Authorization")) items.Add(new("Authentication credentials were attached to the request; sensitive values are redacted in the inspector.", Neutral));
        if (entry.RequestHeaders.ContainsKey("Cookie")) items.Add(new("The request included browser cookies.", Neutral));
        if (entry.DurationMs >= 1000) items.Add(new($"This exchange took {entry.DurationMs / 1000d:0.00} seconds and may deserve a performance review.", Notice));
        if (entry.TransferSize >= 1024 * 1024) items.Add(new($"The response transferred {entry.SizeText}, which is relatively large.", Notice));
        if (entry.ResponseHeaders.TryGetValue("Cache-Control", out var cache) && cache.Contains("no-store", StringComparison.OrdinalIgnoreCase)) items.Add(new("The response explicitly prevents storage in caches.", Neutral));
        if (entry.ResponseBodyWasBase64) items.Add(new(entry.IsBinaryResponse ? "The response is binary; its original Base64 payload is preserved for lossless export." : "The Base64 response was decoded for inspection and preserved for lossless export.", Neutral));
        if (entry.ResponseHeaderItems.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) items.Add(new("Repeated response headers are preserved as separate values.", Neutral));
        if (!string.IsNullOrWhiteSpace(entry.CacheSource) && entry.CacheSource != "Network") items.Add(new($"The response was served from {entry.CacheSource.ToLowerInvariant()}.", Good));
        return items;
    }

    private static IEnumerable<KeyValuePair<string, string>> ConnectionItems(HarEntry entry)
    {
        yield return new("Started", entry.StartedAt.LocalDateTime.ToString("G"));
        yield return new("Remote endpoint", entry.RemoteAddress is null ? "Not recorded" : entry.RemoteAddress + (entry.RemotePort is null ? string.Empty : $":{entry.RemotePort}"));
        yield return new("Content type", entry.ContentType);
        yield return new("Page", string.IsNullOrWhiteSpace(entry.PageUrl) ? "Not recorded" : entry.PageUrl);
        if (entry.RedirectChain.Count > 1)
        {
            yield return new("Redirect position", $"{entry.RedirectStep + 1} of {entry.RedirectChain.Count}");
            for (var index = 0; index < entry.RedirectChain.Count; index++) yield return new($"Hop {index + 1}", entry.RedirectChain[index]);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> CacheItems(HarEntry entry)
    {
        yield return new("Source", !string.IsNullOrWhiteSpace(entry.CacheSource) ? entry.CacheSource : entry.CacheMetadata.Count > 0 ? "HAR cache metadata available" : "Not recorded");
        foreach (var name in new[] { "Cache-Control", "Age", "ETag", "Expires", "Last-Modified", "Vary" })
            if (entry.ResponseHeaders.TryGetValue(name, out var value)) yield return new(name, value);
        foreach (var item in entry.CacheMetadata)
            yield return new(item.Key, item.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? JsonSerializer.Serialize(item.Value, new JsonSerializerOptions { WriteIndented = true }) : item.Value.ToString());
    }

    private static IEnumerable<KeyValuePair<string, string>> TimingItems(HarEntry entry)
    {
        yield return new("Started", entry.StartedAt.LocalDateTime.ToString("G"));
        yield return new("Elapsed", entry.DurationText);
        yield return new("Blocked / queued", TimingValue(entry.Timings.Blocked));
        yield return new("DNS lookup", TimingValue(entry.Timings.Dns));
        yield return new("Connection", TimingValue(entry.Timings.Connect));
        yield return new("TLS negotiation", TimingValue(entry.Timings.Ssl));
        yield return new("Request sent", TimingValue(entry.Timings.Send));
        yield return new("Waiting for server", TimingValue(entry.Timings.Wait));
        yield return new("Response received", TimingValue(entry.Timings.Receive));
        yield return new("Request headers", SizeValue(entry.RequestHeadersSize));
        yield return new("Request body", SizeValue(entry.RequestBodySize));
        yield return new("Response headers", SizeValue(entry.ResponseHeadersSize));
        yield return new("Response body on wire", SizeValue(entry.ResponseBodySize));
        yield return new("Decoded content", SizeValue(entry.DecodedContentSize));
        yield return new("Compression saved", SizeValue(entry.CompressionSavings));
        yield return new("Transferred", entry.SizeText);
        yield return new("Protocol", entry.HttpVersion);
        if (!string.IsNullOrWhiteSpace(entry.Timings.Comment)) yield return new("Timing note", entry.Timings.Comment);
    }

    private static object BuildRaw(HarEntry entry) => new
    {
        entry.StartedAt, entry.Method, entry.Url, entry.HttpVersion, entry.ResourceType,
        request = new { headers = entry.OrderedRequestHeaders, query = entry.QueryParameters(), cookies = entry.RequestCookieItems, body = entry.RequestBody },
        response = new { entry.Status, headers = entry.OrderedResponseHeaders, cookies = entry.ResponseCookieItems, contentType = entry.ContentType, body = entry.ResponseBody },
        timing = entry.Timings, durationMs = entry.DurationMs, transferSize = entry.TransferSize,
        remote = new { address = entry.RemoteAddress, port = entry.RemotePort }, failure = entry.FailureReason
    };

    private static string CountLabel(int count, string noun) => count == 0 ? "None" : $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
    private static string TimingValue(double value) => value < 0 ? "Not recorded" : $"{value:0.##} ms";
    private static string SizeValue(long value) => value < 0 ? "Not recorded" : value < 1024 ? $"{value} B" : value < 1024 * 1024 ? $"{value / 1024d:0.0} KB" : $"{value / 1024d / 1024d:0.0} MB";
    private static bool HasPhaseTimings(HarTimings timings) => timings.Blocked >= 0 || timings.Dns >= 0 || timings.Connect >= 0 || timings.Ssl >= 0 || timings.Send > 0 || timings.Receive > 0;
    private static string BodyLabel(string? body, string contentType, long recordedSize)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Not captured";
        var type = contentType == "—" ? "Text" : contentType.Split(';')[0].Replace("application/", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("text/", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var bytes = recordedSize > 0 ? recordedSize : Encoding.UTF8.GetByteCount(body);
        var size = bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / 1024d / 1024d:0.0} MB";
        return $"{type} · {size}";
    }
    private static string BinaryBodyLabel(HarEntry entry)
    {
        var type = entry.ContentType == "—" ? "BINARY" : entry.ContentType.Split(';')[0].ToUpperInvariant();
        return $"{type} · {entry.SizeText} · BASE64";
    }

    private static Brush Frozen(byte red, byte green, byte blue) { var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush; }
    private sealed record InsightItem(string Text, Brush Brush);
}
