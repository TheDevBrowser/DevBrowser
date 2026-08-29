using System.Globalization;
using System.Text.Json;
using DeveloperBrowser.Core.Security;

namespace DeveloperBrowser.Core.Storage;

public enum BrowserStorageKind { Cookie, LocalStorage, SessionStorage }
public enum StorageValueKind { Jwt, Json, Url, Boolean, Number, Timestamp, Text }

public sealed record BrowserStorageItem(
    BrowserStorageKind Kind,
    string Key,
    string Value,
    string? Domain = null,
    string? Path = null,
    DateTimeOffset? ExpiresAt = null,
    bool Secure = false,
    bool HttpOnly = false,
    string? SameSite = null,
    bool IsSession = false);

public sealed record BrowserStorageSnapshot(
    string Origin,
    IReadOnlyList<BrowserStorageItem> Cookies,
    IReadOnlyList<BrowserStorageItem> LocalStorage,
    IReadOnlyList<BrowserStorageItem> SessionStorage)
{
    public static BrowserStorageSnapshot Empty(string origin) => new(origin, [], [], []);
    public IEnumerable<BrowserStorageItem> AllItems => Cookies.Concat(LocalStorage).Concat(SessionStorage);
}

public sealed record StorageValueAnalysis(StorageValueKind Kind, JwtTokenInspection? Jwt = null, string? Detail = null)
{
    public bool IsJwt => Kind == StorageValueKind.Jwt;
    public bool IsJson => Kind == StorageValueKind.Json;
}

/// <summary>Conservative local classification for browser-storage values. It never sends values anywhere.</summary>
public static class StorageValueAnalyzer
{
    private const int JsonParseLimit = 750_000;

    public static StorageValueAnalysis Analyze(string key, string? value)
    {
        value ??= string.Empty;
        if (JwtTokenInspector.TryInspectToken(value, out var jwt) && jwt is not null)
            return new(StorageValueKind.Jwt, jwt, jwt.TimeRemaining);

        var trimmed = value.Trim();
        if (trimmed.Length <= JsonParseLimit && trimmed.Length > 1 && (trimmed[0] is '{' or '['))
        {
            try { using var _ = JsonDocument.Parse(trimmed); return new(StorageValueKind.Json); }
            catch (JsonException) { }
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return new(StorageValueKind.Url);
        if (bool.TryParse(trimmed, out _)) return new(StorageValueKind.Boolean);
        if (LooksLikeTimestampKey(key) && TryReadTimestamp(trimmed, out var timestamp))
            return new(StorageValueKind.Timestamp, Detail: timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        if (double.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return new(StorageValueKind.Number);
        return new(StorageValueKind.Text);
    }

    private static bool LooksLikeTimestampKey(string key) =>
        key.Contains("date", StringComparison.OrdinalIgnoreCase) || key.Contains("time", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("expire", StringComparison.OrdinalIgnoreCase) || key.EndsWith("at", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp)) return true;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) return false;
        try
        {
            timestamp = unix > 99_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
            return timestamp.Year is >= 2000 and <= 2100;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
