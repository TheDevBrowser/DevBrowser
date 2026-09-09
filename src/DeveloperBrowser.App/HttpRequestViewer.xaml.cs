using System.Windows.Controls;
using System.Text;

namespace DeveloperBrowser.App;

public partial class HttpRequestViewer : UserControl
{
    public HttpRequestViewer() => InitializeComponent();

    public void SetRequest(string? method, string? url, IEnumerable<KeyValuePair<string, string>>? query, string? body, string? contentType,
        IReadOnlyDictionary<string, string>? headers = null, IEnumerable<KeyValuePair<string, string>>? orderedHeaders = null,
        IEnumerable<HarCookie>? structuredCookies = null)
    {
        var displayMethod = string.IsNullOrWhiteSpace(method) ? "HTTP" : method.ToUpperInvariant();
        MethodText.Text = displayMethod;
        MethodText.Foreground = HttpMethodPalette.Foreground(displayMethod);
        MethodBadge.Background = HttpMethodPalette.Background(displayMethod);
        UrlText.Text = url ?? string.Empty;
        var queryItems = query?.ToList() ?? [];
        var headerItems = orderedHeaders?.ToList() ?? headers?.ToList() ?? [];
        List<KeyValuePair<string, string>> cookieItems = structuredCookies?.Any() == true
            ? HttpInspectorFormatting.Cookies(structuredCookies, "Request").ToList()
            : headers is null ? [] : HttpInspectorFormatting.Cookies(headers, new Dictionary<string, string>()).ToList();
        QueryViewer.SetItems(queryItems, "No query parameters");
        HeadersViewer.SetItems(HttpInspectorFormatting.Headers(headerItems), "No request headers");
        CookiesViewer.SetItems(cookieItems, "No request cookies");
        BodyViewer.SetContent(body ?? "(No request body)", contentType);
        QueryMetaText.Text = CountLabel(queryItems.Count, "parameter");
        HeadersMetaText.Text = CountLabel(headerItems.Count, "header");
        CookiesMetaText.Text = CountLabel(cookieItems.Count, "cookie");
        BodyMetaText.Text = BodyLabel(body, contentType);
        QueryExpander.IsEnabled = queryItems.Count > 0;
        CookiesExpander.IsEnabled = cookieItems.Count > 0;
        BodyExpander.IsEnabled = !string.IsNullOrWhiteSpace(body);
        QueryExpander.IsExpanded = queryItems.Count > 0 && string.IsNullOrWhiteSpace(body);
        BodyExpander.IsExpanded = !string.IsNullOrWhiteSpace(body);
    }

    public void Clear() => SetRequest(null, null, null, null, null);

    private static string CountLabel(int count, string noun) => count == 0 ? "None" : $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
    private static string BodyLabel(string? body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return "None";
        var type = string.IsNullOrWhiteSpace(contentType) || contentType == "—" ? "Text" : contentType.Split(';')[0].Replace("application/", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("text/", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var bytes = Encoding.UTF8.GetByteCount(body);
        var size = bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.0} KB";
        return $"{type} · {size}";
    }
}
