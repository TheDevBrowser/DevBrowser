using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;

namespace DeveloperBrowser.App;

public sealed record PageMetadata(string Title, string? Description, string? FaviconUrl);

/// <summary>Extracts a small bookmark preview without retaining complete page content.</summary>
public sealed class PageMetadataService
{
    private const string ExtractionScript = """
        (() => {
          const meta = (...selectors) => {
            for (const selector of selectors) {
              const value = document.querySelector(selector)?.getAttribute('content');
              if (value) return value.trim();
            }
            return '';
          };
          const visible = (document.body?.innerText || '').replace(/s+/g, ' ').trim().slice(0, 420);
          const icon = document.querySelector("link[rel~='icon']")?.href || new URL('/favicon.ico', location.href).href;
          return JSON.stringify({
            title: (document.title || '').trim(),
            description: meta("meta[name='description']", "meta[property='og:description']", "meta[name='twitter:description']") || visible,
            faviconUrl: icon
          });
        })()
        """;

    public async Task<PageMetadata> ExtractAsync(WebView2 browser, string fallbackUrl)
    {
        var fallback = new PageMetadata(fallbackUrl, null, FaviconFor(fallbackUrl));
        if (browser.CoreWebView2 is null) return fallback;
        try
        {
            var encoded = await browser.CoreWebView2.ExecuteScriptAsync(ExtractionScript);
            var json = JsonSerializer.Deserialize<string>(encoded);
            var metadata = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PageMetadata>(json);
            return metadata is null
                ? fallback
                : metadata with { Title = string.IsNullOrWhiteSpace(metadata.Title) ? fallbackUrl : metadata.Title, FaviconUrl = metadata.FaviconUrl ?? FaviconFor(fallbackUrl) };
        }
        catch
        {
            return fallback;
        }
    }

    private static string? FaviconFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/favicon.ico").AbsoluteUri : null;
}
