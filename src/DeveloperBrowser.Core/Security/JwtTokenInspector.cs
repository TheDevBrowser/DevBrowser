using System.Text;
using System.Text.Json;

namespace DeveloperBrowser.Core.Security;

/// <summary>
/// Decodes JWT metadata locally. This class intentionally does not retain, log, or validate tokens.
/// </summary>
public static class JwtTokenInspector
{
    public static bool IsBearerJwt(string? authorizationHeader) =>
        TryGetBearerToken(authorizationHeader, out _);

    public static bool TryInspectBearerToken(string? authorizationHeader, out JwtTokenInspection? inspection)
    {
        inspection = null;
        if (!TryGetBearerToken(authorizationHeader, out var token)) return false;

        var segments = token.Split('.');
        if (segments.Length != 3) return false;

        try
        {
            using var header = JsonDocument.Parse(DecodeBase64Url(segments[0]));
            using var payload = JsonDocument.Parse(DecodeBase64Url(segments[1]));
            if (header.RootElement.ValueKind != JsonValueKind.Object || payload.RootElement.ValueKind != JsonValueKind.Object) return false;

            inspection = new JwtTokenInspection(
                FormatJson(header.RootElement),
                FormatJson(payload.RootElement),
                StringClaim(payload.RootElement, "iss"),
                JoinedClaim(payload.RootElement, "aud"),
                StringClaim(payload.RootElement, "sub"),
                FirstJoinedClaim(payload.RootElement, "scp", "scope"),
                JoinedClaim(payload.RootElement, "roles"),
                ReadTimestamp(payload.RootElement, "iat"),
                ReadTimestamp(payload.RootElement, "nbf"),
                ReadTimestamp(payload.RootElement, "exp"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetBearerToken(string? authorizationHeader, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;

        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        token = authorizationHeader[prefix.Length..].Trim();
        return token.Count(character => character == '.') == 2;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }

    private static string FormatJson(JsonElement element) => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    private static string? StringClaim(JsonElement payload, string claim) =>
        payload.TryGetProperty(claim, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static string? FirstJoinedClaim(JsonElement payload, params string[] claims) =>
        claims.Select(claim => JoinedClaim(payload, claim)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? JoinedClaim(JsonElement payload, string claim)
    {
        if (!payload.TryGetProperty(claim, out var value)) return null;
        return value.ValueKind == JsonValueKind.Array
            ? string.Join(", ", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()))
            : value.ToString();
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement payload, string claim)
    {
        if (!payload.TryGetProperty(claim, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return null;
    }
}

public sealed record JwtTokenInspection(
    string HeaderJson,
    string PayloadJson,
    string? Issuer,
    string? Audience,
    string? Subject,
    string? Scopes,
    string? Roles,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt)
{
    public string SignatureStatus => "Not validated — decoded locally only";

    public JwtTimeStatus TimeStatus
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            if (NotBefore is { } notBefore && now < notBefore) return JwtTimeStatus.NotValidYet;
            if (ExpiresAt is { } expires && now >= expires) return JwtTimeStatus.Expired;
            return JwtTimeStatus.ValidTimeRange;
        }
    }

    public string TimeRemaining =>
        ExpiresAt is null ? "No expiry claim" :
        ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" :
        FormatDuration(ExpiresAt.Value - DateTimeOffset.UtcNow);

    public static string LocalTime(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("ddd, dd MMM yyyy HH:mm:ss zzz") ?? "Not present";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}d {duration.Hours}h remaining" :
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m remaining" :
        $"{Math.Max(0, (int)duration.TotalMinutes)}m remaining";
}

public enum JwtTimeStatus
{
    ValidTimeRange,
    Expired,
    NotValidYet
}
