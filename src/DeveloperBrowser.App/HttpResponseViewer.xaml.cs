using System.Text;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeveloperBrowser.App;

public partial class HttpResponseViewer : UserControl
{
    public HttpResponseViewer() => InitializeComponent();

    public void SetResponse(int? statusCode, bool isFailed, string? contentType, string? durationText, string? protocol,
        IReadOnlyDictionary<string, string>? headers, string? body)
    {
        StatusText.Text = statusCode?.ToString() ?? (isFailed ? "Failed" : "—");
        StatusBadge.Background = new SolidColorBrush(isFailed ? Color.FromRgb(111, 47, 57) : Color.FromRgb(29, 100, 70));
        TypeText.Text = string.IsNullOrWhiteSpace(contentType) || contentType == "—" ? "Unknown response type" : contentType;
        SummaryText.Text = $"{durationText ?? "—"} · {protocol ?? "Not recorded"}";

        var headerItems = headers?.ToList() ?? [];
        var cookieItems = headers is null
            ? []
            : HttpInspectorFormatting.Cookies(new Dictionary<string, string>(), headers).ToList();
        HeadersViewer.SetItems(HttpInspectorFormatting.Headers(headerItems), "No response headers");
        CookiesViewer.SetItems(cookieItems, "No response cookies");
        BodyViewer.SetContent(body ?? "(Response body is not available yet.)", contentType);
        HeadersMetaText.Text = CountLabel(headerItems.Count, "header");
        CookiesMetaText.Text = CountLabel(cookieItems.Count, "cookie");
        BodyMetaText.Text = BodyLabel(body, contentType);
        CookiesExpander.IsEnabled = cookieItems.Count > 0;
        HeadersExpander.IsExpanded = isFailed;
        BodyExpander.IsEnabled = !string.IsNullOrWhiteSpace(body);
        BodyExpander.IsExpanded = !string.IsNullOrWhiteSpace(body);
    }

    public void Clear()
    {
        StatusText.Text = "—";
        TypeText.Text = "Select a request";
        SummaryText.Text = string.Empty;
        HeadersViewer.SetItems(null, "No response headers");
        CookiesViewer.SetItems(null, "No response cookies");
        BodyViewer.SetContent(null);
        HeadersMetaText.Text = CookiesMetaText.Text = "None";
        BodyMetaText.Text = "Not captured";
    }

    private static string CountLabel(int count, string noun) => count == 0 ? "None" : $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string BodyLabel(string? body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Not captured";
        var type = string.IsNullOrWhiteSpace(contentType) || contentType == "—" ? "TEXT" : contentType.Split(';')[0].Replace("application/", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("text/", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        var bytes = Encoding.UTF8.GetByteCount(body);
        var size = bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / 1024d / 1024d:0.0} MB";
        return $"{type} · {size}";
    }
}
