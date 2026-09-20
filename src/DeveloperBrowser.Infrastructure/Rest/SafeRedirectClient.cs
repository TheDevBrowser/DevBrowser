using DeveloperBrowser.Core.Rest;

namespace DeveloperBrowser.Infrastructure.Rest;

/// <summary>Applies redirect policy before any request reaches a new destination.</summary>
public sealed class SafeRedirectClient : IDisposable
{
    private readonly HttpClient _client;
    // Unknown headers are treated as sensitive, including nonstandard API keys.
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Accept", "Accept-Encoding", "Accept-Language", "User-Agent" };
    private static readonly HashSet<string> SafeContentHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Content-Type", "Content-Length" };

    public SafeRedirectClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    public SafeRedirectClient(HttpMessageHandler transport)
    {
        if (transport is HttpClientHandler handler) { handler.AllowAutoRedirect = false; handler.UseCookies = false; }
        _client = new HttpClient(transport);
    }

    public async Task<RedirectResponse> SendAsync(HttpRequestMessage request, RedirectSettings? settings = null,
        Func<RedirectPrompt, CancellationToken, Task<RedirectDecision>>? decide = null, CancellationToken ct = default)
    {
        settings ??= new RedirectSettings();
        settings.Validate();
        var currentUri = request.RequestUri ?? throw new InvalidOperationException("A request URL is required.");
        ValidateUri(currentUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        ct = timeout.Token;
        var history = new List<RedirectHop>();
        var current = await CloneAsync(request, currentUri, request.Method, true, false, ct);
        try
        {
            for (var followed = 0; ; followed++)
            {
                var response = await _client.SendAsync(current, ct);
                var status = (int)response.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308)) return new(response, history);
                Uri? destination;
                try
                {
                    var location = response.Headers.Location;
                    if (location is null) return Stop("Missing redirect destination.", null);
                    destination = new Uri(currentUri, location);
                    ValidateUri(destination);
                    destination = new UriBuilder(destination) { Fragment = "" }.Uri;
                }
                catch (Exception exception) when (exception is UriFormatException or InvalidOperationException)
                {
                    return Stop("Invalid or unsupported redirect destination.", null);
                }
                if (!settings.FollowRedirects) return Stop("Automatic redirects are disabled.", destination);
                if (followed >= settings.MaxRedirects) return Stop("Redirect limit reached.", destination);
                if (currentUri.Scheme == "https" && destination.Scheme == "http" && !settings.AllowHttpsToHttp)
                    return Stop("HTTPS to HTTP redirect blocked.", destination);

                var method = current.Method;
                if ((status is 301 or 302 && method == HttpMethod.Post) || (status == 303 && method != HttpMethod.Head)) method = HttpMethod.Get;
                var keepBody = method == current.Method && current.Content is not null;
                var crossOrigin = RedirectSettings.Origin(currentUri) != RedirectSettings.Origin(destination);
                var sensitive = current.Headers.Where(h => !SafeHeaders.Contains(h.Key)).Select(h => h.Key)
                    .Concat(keepBody ? current.Content!.Headers.Where(h => !SafeContentHeaders.Contains(h.Key)).Select(h => h.Key) : [])
                    .Where(h => !h.Equals("Host", StringComparison.OrdinalIgnoreCase)).ToArray();
                var trust = settings.TrustedOrigins.FirstOrDefault(t => t is not null &&
                    t.SourceOrigin == RedirectSettings.Origin(currentUri) && t.DestinationOrigin == RedirectSettings.Origin(destination));
                var forwardCredentials = !crossOrigin || trust?.Credentials == true;
                var forwardBody = !crossOrigin || !keepBody || trust?.Body == true || settings.Body == CrossOriginBodyPolicy.Allow;
                if (!forwardBody && settings.Body == CrossOriginBodyPolicy.Block) return Stop("Sending a body to another origin is blocked.", destination);
                var needsHeaders = crossOrigin && sensitive.Length > 0 && !forwardCredentials && settings.Headers == CrossOriginHeaderPolicy.Ask;
                var needsBody = !forwardBody;
                if (needsHeaders || needsBody)
                {
                    if (decide is null) return Stop("Cross-origin redirect needs approval.", destination);
                    RedirectDecision decision;
                    try { decision = await decide(new(status, currentUri, destination, method.Method, sensitive, needsBody), ct); }
                    catch { response.Dispose(); throw; }
                    if (!decision.Follow || (needsBody && !decision.ForwardBody)) return Stop("Redirect stopped by user.", destination);
                    forwardCredentials |= decision.ForwardCredentials;
                    forwardBody |= decision.ForwardBody;
                }
                HttpRequestMessage next;
                try { next = await CloneAsync(current, destination, method, keepBody, crossOrigin && !forwardCredentials, ct); }
                catch { response.Dispose(); throw; }
                var outcome = crossOrigin && !forwardCredentials && sensitive.Length > 0
                    ? "Followed; removed headers: " + string.Join(", ", sensitive)
                    : "Followed" + (crossOrigin && forwardCredentials && sensitive.Length > 0 ? "; credential forwarding approved" : "");
                history.Add(new(status, RedirectSettings.Origin(currentUri), RedirectSettings.Origin(destination), outcome));
                response.Dispose();
                current.Dispose();
                current = next;
                currentUri = destination;

                RedirectResponse Stop(string reason, Uri? target)
                {
                    history.Add(new(status, RedirectSettings.Origin(currentUri), target is null ? "(unavailable)" : RedirectSettings.Origin(target), reason));
                    return new(response, history);
                }
            }
        }
        finally { current.Dispose(); }
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("REST requests require an HTTP(S) URL without embedded username/password.");
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, Uri uri, HttpMethod method, bool keepBody, bool strip, CancellationToken ct)
    {
        var next = new HttpRequestMessage(method, uri);
        try
        {
            foreach (var header in source.Headers)
                if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && (!strip || SafeHeaders.Contains(header.Key)))
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (keepBody && source.Content is not null)
            {
                next.Content = new ByteArrayContent(await source.Content.ReadAsByteArrayAsync(ct));
                foreach (var header in source.Content.Headers)
                    if (!strip || SafeContentHeaders.Contains(header.Key)) next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return next;
        }
        catch { next.Dispose(); throw; }
    }

    public void Dispose() => _client.Dispose();
}

public sealed record RedirectResponse(HttpResponseMessage Response, IReadOnlyList<RedirectHop> History) : IDisposable
{
    public void Dispose() => Response.Dispose();
}
