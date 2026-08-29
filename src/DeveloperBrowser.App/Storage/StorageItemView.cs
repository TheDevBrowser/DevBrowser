using DeveloperBrowser.Core.Security;
using DeveloperBrowser.Core.Storage;

namespace DeveloperBrowser.App.Storage;

public sealed class StorageItemView(BrowserStorageItem item, StorageValueAnalysis analysis)
{
    public BrowserStorageItem Item { get; } = item;
    public StorageValueAnalysis Analysis { get; } = analysis;
    public int DuplicateCount { get; set; }

    public string Key => Item.Key;
    public string TypeLabel => Analysis.Kind switch
    {
        StorageValueKind.Jwt => "JWT",
        StorageValueKind.Json => "JSON",
        StorageValueKind.Url => "URL",
        StorageValueKind.Boolean => "Boolean",
        StorageValueKind.Number => "Number",
        StorageValueKind.Timestamp => "Timestamp",
        _ => "Text"
    };
    public string Preview => IsJwt ? "JWT token detected — value hidden by default" : Item.Value.Length <= 150 ? Item.Value : $"{Item.Value[..147]}…";
    public string SourceLabel => Item.Kind switch
    {
        BrowserStorageKind.Cookie => "Cookie",
        BrowserStorageKind.LocalStorage => "Local Storage",
        _ => "Session Storage"
    };
    public bool IsJwt => Analysis.IsJwt;
    public bool IsJson => Analysis.IsJson;
    public bool IsWarning => !string.IsNullOrEmpty(Warning);
    public bool HasDuplicates => DuplicateCount > 1;
    public bool IsAuthenticationRelated => IsJwt || Key.Contains("token", StringComparison.OrdinalIgnoreCase) || Key.Contains("auth", StringComparison.OrdinalIgnoreCase);
    public string DuplicateText => DuplicateCount > 1 ? $"Also found in {DuplicateCount - 1} other storage location{(DuplicateCount == 2 ? string.Empty : "s")}" : string.Empty;
    public string Metadata => Item.Kind == BrowserStorageKind.Cookie ? CookieMetadata() : $"{SourceLabel} • {TypeLabel}{(Analysis.Detail is null ? string.Empty : $" • {Analysis.Detail}")}";
    public string ExpiryText => Item.Kind == BrowserStorageKind.Cookie ? CookieExpiry() : IsJwt ? Analysis.Jwt?.TimeRemaining ?? string.Empty : string.Empty;

    public string Warning
    {
        get
        {
            if (IsJwt && Analysis.Jwt?.TimeStatus == JwtTimeStatus.Expired) return "JWT expired";
            if (Item.Kind != BrowserStorageKind.Cookie) return string.Empty;
            if (Item.ExpiresAt is { } expiry)
            {
                if (expiry <= DateTimeOffset.UtcNow) return "Cookie expired";
                if (expiry - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5)) return "Expires soon";
            }
            if (string.Equals(Item.SameSite, "None", StringComparison.OrdinalIgnoreCase) && !Item.Secure) return "SameSite=None without Secure";
            if (!Item.Secure && (Key.Contains("token", StringComparison.OrdinalIgnoreCase) || Key.Contains("session", StringComparison.OrdinalIgnoreCase) || Key.Contains("auth", StringComparison.OrdinalIgnoreCase))) return "Sensitive-looking cookie without Secure";
            return string.Empty;
        }
    }

    private string CookieMetadata()
    {
        var attributes = new List<string> { "Cookie" };
        if (!string.IsNullOrWhiteSpace(Item.Domain)) attributes.Add(Item.Domain);
        if (!string.IsNullOrWhiteSpace(Item.Path)) attributes.Add(Item.Path);
        if (Item.Secure) attributes.Add("Secure");
        if (Item.HttpOnly) attributes.Add("HttpOnly");
        if (!string.IsNullOrWhiteSpace(Item.SameSite)) attributes.Add($"SameSite={Item.SameSite}");
        return string.Join(" • ", attributes);
    }

    private string CookieExpiry()
    {
        if (Item.IsSession || Item.ExpiresAt is null) return "Session cookie";
        var duration = Item.ExpiresAt.Value - DateTimeOffset.UtcNow;
        if (duration <= TimeSpan.Zero) return "Expired";
        if (duration.TotalDays >= 1) return $"Expires in {(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"Expires in {(int)duration.TotalHours}h {duration.Minutes}m";
        return $"Expires in {Math.Max(0, (int)duration.TotalMinutes)}m";
    }
}
