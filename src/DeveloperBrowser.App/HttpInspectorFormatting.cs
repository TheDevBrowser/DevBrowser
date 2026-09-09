using DeveloperBrowser.Core.Security;

namespace DeveloperBrowser.App;

internal static class HttpInspectorFormatting
{
    public static IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string> headers) => Headers(headers.AsEnumerable());

    public static IEnumerable<KeyValuePair<string, string>> Headers(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var header in headers)
        {
            var value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? RedactAuthorization(header.Value)
                : header.Value;
            yield return new KeyValuePair<string, string>(header.Key, value);
        }
    }

    public static IEnumerable<KeyValuePair<string, string>> Cookies(IEnumerable<HarCookie> cookies, string direction)
    {
        foreach (var cookie in cookies)
        {
            var attributes = new List<string>();
            if (!string.IsNullOrWhiteSpace(cookie.Domain)) attributes.Add("Domain=" + cookie.Domain);
            if (!string.IsNullOrWhiteSpace(cookie.Path)) attributes.Add("Path=" + cookie.Path);
            if (cookie.Expires is not null) attributes.Add("Expires=" + cookie.Expires.Value.LocalDateTime.ToString("G"));
            if (!string.IsNullOrWhiteSpace(cookie.SameSite)) attributes.Add("SameSite=" + cookie.SameSite);
            if (cookie.Secure == true) attributes.Add("Secure");
            if (cookie.HttpOnly == true) attributes.Add("HttpOnly");
            var suffix = attributes.Count == 0 ? string.Empty : Environment.NewLine + string.Join(" · ", attributes);
            yield return new KeyValuePair<string, string>($"{direction} · {cookie.Name}", cookie.Value + suffix);
        }
    }

    public static IEnumerable<KeyValuePair<string, string>> Cookies(
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> responseHeaders)
    {
        if (requestHeaders.TryGetValue("Cookie", out var requestCookie))
            foreach (var cookie in requestCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return CookieRow("Request", cookie);

        if (responseHeaders.TryGetValue("Set-Cookie", out var responseCookie))
            foreach (var cookie in SplitSetCookie(responseCookie))
                yield return CookieRow("Response", cookie);
    }

    private static KeyValuePair<string, string> CookieRow(string direction, string cookie)
    {
        var equals = cookie.IndexOf('=');
        return equals > 0
            ? new KeyValuePair<string, string>($"{direction} · {cookie[..equals].Trim()}", cookie[(equals + 1)..].Trim())
            : new KeyValuePair<string, string>(direction, cookie.Trim());
    }

    private static IEnumerable<string> SplitSetCookie(string value)
    {
        // CDP commonly joins multiple Set-Cookie values with a newline. Avoid splitting Expires dates on commas.
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string RedactAuthorization(string value) =>
        JwtTokenInspector.IsBearerJwt(value) ? "Bearer •••• (JWT detected)" :
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? "Bearer ••••" :
        "••••";
}
