using System.Text.Json;
using DeveloperBrowser.Core.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeveloperBrowser.App.Storage;

/// <summary>Reads and edits only the current WebView2 origin's cookies and web storage.</summary>
public sealed class BrowserStorageService
{
    private const string ReadWebStorageScript = """
        (() => {
          const entries = storage => {
            try { return Array.from({ length: storage.length }, (_, i) => { const key = storage.key(i); return [key, storage.getItem(key)]; }); }
            catch { return []; }
          };
          return JSON.stringify({ origin: location.origin, local: entries(localStorage), session: entries(sessionStorage) });
        })();
        """;

    public async Task<BrowserStorageSnapshot> ReadAsync(WebView2 browser, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var core = browser.CoreWebView2;
        if (core is null || !TryGetOrigin(browser.Source, out var origin)) return BrowserStorageSnapshot.Empty("No inspectable origin");

        try
        {
            var encoded = await core.ExecuteScriptAsync(ReadWebStorageScript);
            var payload = JsonSerializer.Deserialize<string>(encoded) ?? "{}";
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var actualOrigin = root.TryGetProperty("origin", out var originValue) ? originValue.GetString() : origin;
            var local = ReadEntries(root, "local", BrowserStorageKind.LocalStorage);
            var session = ReadEntries(root, "session", BrowserStorageKind.SessionStorage);
            var cookies = await ReadCookiesAsync(core, origin, cancellationToken);
            return new BrowserStorageSnapshot(actualOrigin ?? origin, cookies, local, session);
        }
        catch (Exception) when (browser.CoreWebView2 is not null)
        {
            return BrowserStorageSnapshot.Empty(origin);
        }
    }

    public async Task SetValueAsync(WebView2 browser, BrowserStorageItem item, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("The browser is not ready.");
        if (item.Kind == BrowserStorageKind.Cookie)
        {
            var request = new Dictionary<string, object?>
            {
                ["name"] = item.Key, ["value"] = value, ["domain"] = item.Domain, ["path"] = item.Path ?? "/",
                ["secure"] = item.Secure, ["httpOnly"] = item.HttpOnly
            };
            if (!string.IsNullOrWhiteSpace(item.SameSite)) request["sameSite"] = item.SameSite;
            if (item.ExpiresAt is { } expires && !item.IsSession) request["expires"] = expires.ToUnixTimeMilliseconds() / 1000d;
            await core.CallDevToolsProtocolMethodAsync("Network.setCookie", JsonSerializer.Serialize(request));
            return;
        }

        var target = item.Kind == BrowserStorageKind.LocalStorage ? "localStorage" : "sessionStorage";
        await core.ExecuteScriptAsync($"window.{target}.setItem({JsonSerializer.Serialize(item.Key)}, {JsonSerializer.Serialize(value)});");
    }

    public async Task DeleteAsync(WebView2 browser, BrowserStorageItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("The browser is not ready.");
        if (item.Kind == BrowserStorageKind.Cookie)
        {
            await core.CallDevToolsProtocolMethodAsync("Network.deleteCookies", JsonSerializer.Serialize(new { name = item.Key, domain = item.Domain, path = item.Path ?? "/" }));
            return;
        }

        var target = item.Kind == BrowserStorageKind.LocalStorage ? "localStorage" : "sessionStorage";
        await core.ExecuteScriptAsync($"window.{target}.removeItem({JsonSerializer.Serialize(item.Key)});");
    }

    public async Task ClearAsync(WebView2 browser, BrowserStorageSnapshot snapshot, BrowserStorageKind kind, CancellationToken cancellationToken = default)
    {
        var items = kind switch
        {
            BrowserStorageKind.Cookie => snapshot.Cookies,
            BrowserStorageKind.LocalStorage => snapshot.LocalStorage,
            _ => snapshot.SessionStorage
        };
        if (kind == BrowserStorageKind.Cookie)
        {
            foreach (var item in items) await DeleteAsync(browser, item, cancellationToken);
            return;
        }

        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("The browser is not ready.");
        var target = kind == BrowserStorageKind.LocalStorage ? "localStorage" : "sessionStorage";
        await core.ExecuteScriptAsync($"window.{target}.clear();");
    }

    private static async Task<IReadOnlyList<BrowserStorageItem>> ReadCookiesAsync(CoreWebView2 core, string origin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await core.CallDevToolsProtocolMethodAsync("Network.getCookies", JsonSerializer.Serialize(new { urls = new[] { origin } }));
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return [];
        return cookies.EnumerateArray().Select(cookie =>
        {
            DateTimeOffset? expires = null;
            if (cookie.TryGetProperty("expires", out var expiry) && expiry.TryGetDouble(out var seconds) && seconds > 0)
                expires = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            return new BrowserStorageItem(
                BrowserStorageKind.Cookie,
                ReadString(cookie, "name") ?? string.Empty,
                ReadString(cookie, "value") ?? string.Empty,
                ReadString(cookie, "domain"),
                ReadString(cookie, "path"),
                expires,
                ReadBoolean(cookie, "secure"),
                ReadBoolean(cookie, "httpOnly"),
                ReadString(cookie, "sameSite"),
                ReadBoolean(cookie, "session"));
        }).ToList();
    }

    private static IReadOnlyList<BrowserStorageItem> ReadEntries(JsonElement root, string property, BrowserStorageKind kind)
    {
        if (!root.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array) return [];
        var values = new List<BrowserStorageItem>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
            values.Add(new BrowserStorageItem(kind, entry[0].GetString() ?? string.Empty, entry[1].GetString() ?? string.Empty));
        }
        return values;
    }

    private static bool TryGetOrigin(Uri? uri, out string origin)
    {
        origin = string.Empty;
        if (uri is null || uri.Scheme is not ("http" or "https")) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static string? ReadString(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static bool ReadBoolean(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;
}
