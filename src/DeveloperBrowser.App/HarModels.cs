using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DeveloperBrowser.App;

/// <summary>One inspectable HTTP exchange. It maps directly to a HAR 1.2 entry.</summary>
public sealed class HarEntry : INotifyPropertyChanged
{
    private int _status;
    private double _durationMs;
    private long _transferSize;
    private string? _responseBody;
    private string? _failureReason;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public string PageUrl { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = string.Empty;
    public string ResourceType { get; init; } = "Other";
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RequestBody { get; init; }
    public string? MimeType { get; set; }
    public string? RemoteAddress { get; set; }
    public int? RemotePort { get; set; }
    public int Status { get => _status; set { SetField(ref _status, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(IsRedirect)); } }
    public double DurationMs { get => _durationMs; set { SetField(ref _durationMs, Math.Max(0, value)); OnPropertyChanged(nameof(DurationText)); } }
    public long TransferSize { get => _transferSize; set { SetField(ref _transferSize, Math.Max(0, value)); OnPropertyChanged(nameof(SizeText)); } }
    public string? ResponseBody { get => _responseBody; set => SetField(ref _responseBody, value); }
    public string? FailureReason { get => _failureReason; set { SetField(ref _failureReason, value); OnPropertyChanged(nameof(IsFailed)); } }
    public bool IsFailed => Status >= 400 || !string.IsNullOrWhiteSpace(FailureReason);
    public bool IsRedirect => Status is >= 300 and < 400;
    public string StatusText => Status > 0 ? Status.ToString() : string.IsNullOrWhiteSpace(FailureReason) ? "—" : "Failed";
    public string DurationText => DurationMs <= 0 ? "—" : $"{DurationMs:0} ms";
    public string SizeText => TransferSize < 1024 ? $"{TransferSize} B" : TransferSize < 1024 * 1024 ? $"{TransferSize / 1024d:0.0} KB" : $"{TransferSize / 1024d / 1024d:0.0} MB";
    public string Domain => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "—";
    public string ContentType => !string.IsNullOrWhiteSpace(MimeType) ? MimeType : Header("Content-Type", ResponseHeaders);
    public string RequestContentType => Header("Content-Type", RequestHeaders);

    public CapturedNetworkRequest ToCapturedRequest() => new()
    {
        RequestId = Id,
        Method = Method,
        Url = Url,
        ResourceType = ResourceType,
        StartedAt = StartedAt.ToUnixTimeMilliseconds() / 1000d,
        PageUrl = PageUrl,
        RequestHeaders = new Dictionary<string, string>(RequestHeaders, StringComparer.OrdinalIgnoreCase),
        RequestBody = RequestBody,
        ResponseBody = ResponseBody
    };

    public IEnumerable<KeyValuePair<string, string>> QueryParameters()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Query)) yield break;
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            yield return new KeyValuePair<string, string>(Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
        }
    }

    public string Cookies() => string.Join(Environment.NewLine, RequestHeaders.Where(item => item.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Concat(ResponseHeaders.Where(item => item.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))).Select(item => $"{item.Key}: {item.Value}"));
    private static string Header(string name, IReadOnlyDictionary<string, string> headers) => headers.TryGetValue(name, out var value) ? value : "—";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
}

public sealed class HarFile
{
    [JsonPropertyName("log")] public HarLog Log { get; set; } = new();
}

public sealed class HarLog
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1.2";
    [JsonPropertyName("creator")] public HarCreator Creator { get; set; } = new();
    [JsonPropertyName("entries")] public List<HarFileEntry> Entries { get; set; } = [];
}

public sealed class HarCreator { [JsonPropertyName("name")] public string Name { get; set; } = "DevBrowser"; [JsonPropertyName("version")] public string Version { get; set; } = "1.0"; }
public sealed class HarFileEntry
{
    [JsonPropertyName("startedDateTime")] public DateTimeOffset StartedDateTime { get; set; }
    [JsonPropertyName("time")] public double Time { get; set; }
    [JsonPropertyName("request")] public HarRequest Request { get; set; } = new();
    [JsonPropertyName("response")] public HarResponse Response { get; set; } = new();
    [JsonPropertyName("cache")] public Dictionary<string, object?> Cache { get; set; } = [];
    [JsonPropertyName("timings")] public HarTimings Timings { get; set; } = new();
    [JsonPropertyName("_resourceType")] public string? ResourceType { get; set; }
    [JsonPropertyName("_failure")] public string? Failure { get; set; }
    [JsonPropertyName("_remoteAddress")] public string? RemoteAddress { get; set; }
    [JsonPropertyName("_remotePort")] public int? RemotePort { get; set; }
}
public sealed class HarRequest
{
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("httpVersion")] public string HttpVersion { get; set; } = "HTTP/1.1";
    [JsonPropertyName("headers")] public List<HarHeader> Headers { get; set; } = [];
    [JsonPropertyName("queryString")] public List<HarHeader> QueryString { get; set; } = [];
    [JsonPropertyName("cookies")] public List<HarCookie> Cookies { get; set; } = [];
    [JsonPropertyName("headersSize")] public long HeadersSize { get; set; } = -1;
    [JsonPropertyName("bodySize")] public long BodySize { get; set; } = -1;
    [JsonPropertyName("postData")] public HarPostData? PostData { get; set; }
}
public sealed class HarResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("statusText")] public string StatusText { get; set; } = string.Empty;
    [JsonPropertyName("httpVersion")] public string HttpVersion { get; set; } = "HTTP/1.1";
    [JsonPropertyName("headers")] public List<HarHeader> Headers { get; set; } = [];
    [JsonPropertyName("cookies")] public List<HarCookie> Cookies { get; set; } = [];
    [JsonPropertyName("content")] public HarContent Content { get; set; } = new();
    [JsonPropertyName("redirectURL")] public string RedirectUrl { get; set; } = string.Empty;
    [JsonPropertyName("headersSize")] public long HeadersSize { get; set; } = -1;
    [JsonPropertyName("bodySize")] public long BodySize { get; set; } = -1;
}
public sealed class HarHeader { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; [JsonPropertyName("value")] public string Value { get; set; } = string.Empty; }
public sealed class HarCookie { [JsonPropertyName("name")] public string Name { get; set; } = string.Empty; [JsonPropertyName("value")] public string Value { get; set; } = string.Empty; }
public sealed class HarPostData { [JsonPropertyName("mimeType")] public string MimeType { get; set; } = string.Empty; [JsonPropertyName("text")] public string? Text { get; set; } }
public sealed class HarContent { [JsonPropertyName("size")] public long Size { get; set; } = -1; [JsonPropertyName("mimeType")] public string MimeType { get; set; } = string.Empty; [JsonPropertyName("text")] public string? Text { get; set; } [JsonPropertyName("encoding")] public string? Encoding { get; set; } }
public sealed class HarTimings { [JsonPropertyName("send")] public double Send { get; set; } = 0; [JsonPropertyName("wait")] public double Wait { get; set; } = -1; [JsonPropertyName("receive")] public double Receive { get; set; } = 0; }
