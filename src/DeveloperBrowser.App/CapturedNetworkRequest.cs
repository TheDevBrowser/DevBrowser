using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeveloperBrowser.App;

public sealed class CapturedNetworkRequest : INotifyPropertyChanged
{
    private int? _statusCode;
    private double? _durationMs;
    private string _responseContentType = string.Empty;
    private string _failureReason = string.Empty;
    private string? _responseBody;
    private string? _blockedReason;
    private string? _corsError;

    public required string RequestId { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required string ResourceType { get; init; }
    public required double StartedAt { get; init; }
    public string? PageUrl { get; init; }
    public Dictionary<string, string> RequestHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RequestBody { get; init; }
    public string RequestContentType => HeaderValue(RequestHeaders, "Content-Type");
    public string ResponseContentType { get => _responseContentType; private set => SetField(ref _responseContentType, value); }
    public int? StatusCode { get => _statusCode; private set { SetField(ref _statusCode, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsFailed)); } }
    public double? DurationMs { get => _durationMs; private set { SetField(ref _durationMs, value); OnPropertyChanged(nameof(DurationText)); } }
    public string FailureReason { get => _failureReason; private set { SetField(ref _failureReason, value); OnPropertyChanged(nameof(IsFailed)); } }
    public string? ResponseBody { get => _responseBody; set => SetField(ref _responseBody, value); }
    public string? BlockedReason { get => _blockedReason; private set => SetField(ref _blockedReason, value); }
    public string? CorsError { get => _corsError; private set => SetField(ref _corsError, value); }
    public bool IsFailed => StatusCode >= 400 || !string.IsNullOrEmpty(FailureReason);
    public string StatusText => StatusCode?.ToString() ?? (string.IsNullOrEmpty(FailureReason) ? "…" : "Failed");
    public string DurationText => DurationMs is null ? "—" : $"{DurationMs.Value:0} ms";

    public void SetResponse(int statusCode, Dictionary<string, string> headers)
    {
        StatusCode = statusCode;
        foreach (var (name, value) in headers) ResponseHeaders[name] = value;
        ResponseContentType = HeaderValue(ResponseHeaders, "Content-Type");
    }

    public void MergeRequestHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers) RequestHeaders[name] = value;
        OnPropertyChanged(nameof(RequestContentType));
    }

    public void MergeResponseHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers) ResponseHeaders[name] = value;
        ResponseContentType = HeaderValue(ResponseHeaders, "Content-Type");
    }

    public void SetFinished(double timestamp) => DurationMs = Math.Max(0, (timestamp - StartedAt) * 1000);
    public void SetFailed(string reason, double timestamp, string? blockedReason = null, string? corsError = null)
    {
        FailureReason = reason;
        BlockedReason = blockedReason;
        CorsError = corsError;
        SetFinished(timestamp);
    }

    public IEnumerable<KeyValuePair<string, string>> QueryParameters()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) yield break;
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            yield return new KeyValuePair<string, string>(Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
        }
    }

    public string Cookies() => string.Join(Environment.NewLine, RequestHeaders.Where(header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Concat(ResponseHeaders.Where(header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))).Select(header => $"{header.Key}: {header.Value}"));
    private static string HeaderValue(IReadOnlyDictionary<string, string> headers, string name) => headers.TryGetValue(name, out var value) ? value : "—";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
}
