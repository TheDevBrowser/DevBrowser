using System.Globalization;
using System.Text.Json;

namespace DeveloperBrowser.App;

internal enum NetworkProblemCategory { All, Broken, Suspicious, Slow, Authentication, Content, Cache, Healthy }
internal enum NetworkFindingSeverity { Good, Info, Warning, Error }

internal sealed record NetworkFinding(
    NetworkProblemCategory Category,
    NetworkFindingSeverity Severity,
    string Title,
    string Explanation,
    string NextStep);

internal sealed record NetworkStoryStep(string Stage, string Detail, NetworkFindingSeverity Severity);
internal sealed record RelatedNetworkRequest(CapturedNetworkRequest Request, string Relationship, string Summary);

/// <summary>Turns raw network evidence into concise, local-only developer guidance.</summary>
internal static class NetworkInsightAnalyzer
{
    public static IReadOnlySet<NetworkProblemCategory> Categories(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all)
    {
        var categories = new HashSet<NetworkProblemCategory>();
        if (request.IsFailed) categories.Add(NetworkProblemCategory.Broken);
        if (request.StatusCode is 401 or 403 || request.RequestHeaders.ContainsKey("Authorization")) categories.Add(NetworkProblemCategory.Authentication);
        if (HasUnexpectedContent(request)) categories.Add(NetworkProblemCategory.Content);
        if (IsSlow(request)) categories.Add(NetworkProblemCategory.Slow);
        if (HasCacheConcern(request) || HasNoStore(request)) categories.Add(NetworkProblemCategory.Cache);
        if (SuspiciousReasons(request, all).Count > 0) categories.Add(NetworkProblemCategory.Suspicious);
        if (!categories.Any(category => category is NetworkProblemCategory.Broken or NetworkProblemCategory.Suspicious or NetworkProblemCategory.Slow or NetworkProblemCategory.Content or NetworkProblemCategory.Cache))
            categories.Add(NetworkProblemCategory.Healthy);
        return categories;
    }

    public static NetworkProblemCategory PrimaryCategory(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest>? all = null)
    {
        var categories = Categories(request, all ?? []);
        foreach (var category in new[] { NetworkProblemCategory.Broken, NetworkProblemCategory.Suspicious, NetworkProblemCategory.Slow, NetworkProblemCategory.Authentication, NetworkProblemCategory.Content, NetworkProblemCategory.Cache, NetworkProblemCategory.Healthy })
            if (categories.Contains(category)) return category;
        return NetworkProblemCategory.Healthy;
    }

    public static IReadOnlyList<NetworkFinding> Findings(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all)
    {
        var findings = new List<NetworkFinding>();
        if (!string.IsNullOrWhiteSpace(request.CorsError))
            findings.Add(new(NetworkProblemCategory.Broken, NetworkFindingSeverity.Error, "CORS stopped this request", request.CorsError!, "Inspect the CORS / CSP evidence below and align the server's allow-origin, credentials, methods, and headers."));
        else if (request.BlockedReason?.Contains("csp", StringComparison.OrdinalIgnoreCase) == true)
            findings.Add(new(NetworkProblemCategory.Broken, NetworkFindingSeverity.Error, "Content Security Policy blocked the connection", request.FailureReason, "Review the effective connect-src policy in the CORS / CSP section."));
        else if (!string.IsNullOrWhiteSpace(request.FailureReason))
            findings.Add(new(NetworkProblemCategory.Broken, NetworkFindingSeverity.Error, "The browser could not complete this request", request.FailureReason, "Check the related requests and response evidence to locate the first failure."));
        else if (request.StatusCode >= 500)
            findings.Add(new(NetworkProblemCategory.Broken, NetworkFindingSeverity.Error, "The server failed while handling the request", $"HTTP {request.StatusCode} was returned by {Host(request.Url)}.", "Inspect the response body and correlation headers, then reproduce it in the REST client."));
        else if (request.StatusCode >= 400)
            findings.Add(new(NetworkProblemCategory.Broken, NetworkFindingSeverity.Error, "The server rejected this request", $"HTTP {request.StatusCode} indicates a client, authentication, or resource error.", "Inspect the response body and compare this request with a related successful call."));

        if (request.StatusCode is 401 or 403)
            findings.Add(new(NetworkProblemCategory.Authentication, NetworkFindingSeverity.Warning, "Authentication or authorization was rejected", request.StatusCode == 401 ? "The server did not accept the supplied identity." : "The identity was accepted but does not appear to have permission.", "Inspect the JWT when available and compare its audience, expiry, scopes, and roles with the endpoint."));
        else if (request.RequestHeaders.ContainsKey("Authorization"))
            findings.Add(new(NetworkProblemCategory.Authentication, NetworkFindingSeverity.Info, "Credentials were attached", "An Authorization header was observed and is redacted in the inspector.", "Use Inspect JWT for local claim and expiry diagnostics."));

        if (IsSlow(request))
        {
            var threshold = SlowThresholdMs(request);
            var explanation = ExplainTiming(request);
            findings.Add(new(NetworkProblemCategory.Slow, request.DurationMs >= threshold * 2 ? NetworkFindingSeverity.Warning : NetworkFindingSeverity.Info, explanation.Title, explanation.Explanation, explanation.NextStep));
        }

        if (HasUnexpectedContent(request))
            findings.Add(new(NetworkProblemCategory.Content, NetworkFindingSeverity.Warning, "The response type looks unexpected", $"A {request.ResourceType} request returned {request.ResponseContentType}. This often means a login page, proxy error, or fallback document was returned instead of API data.", "Inspect the response body and redirect-related requests."));

        if (request.ResponseHeaders.TryGetValue("Cache-Control", out var cache) && cache.Contains("no-store", StringComparison.OrdinalIgnoreCase))
            findings.Add(new(NetworkProblemCategory.Cache, NetworkFindingSeverity.Info, "The response cannot be stored in caches", $"Cache-Control is “{cache}”. Repeated calls will return to the server.", "Confirm that no-store is intentional for this resource."));
        else if (HasCacheConcern(request))
            findings.Add(new(NetworkProblemCategory.Cache, NetworkFindingSeverity.Info, "No explicit cache policy was captured", "A cacheable static resource has no Cache-Control or Expires header.", "Add an intentional caching policy if the resource is versioned."));

        foreach (var reason in SuspiciousReasons(request, all)) findings.Add(reason);

        if (findings.Count == 0)
            findings.Add(new(NetworkProblemCategory.Healthy, NetworkFindingSeverity.Good, "No obvious problem detected", "The exchange completed successfully and no suspicious evidence was found.", "Use the technical evidence below when you need a deeper inspection."));
        return findings;
    }

    public static IReadOnlyList<NetworkStoryStep> Story(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all)
    {
        var steps = new List<NetworkStoryStep>();
        var source = !string.IsNullOrWhiteSpace(request.InitiatorUrl) ? ShortUrl(request.InitiatorUrl!) :
            !string.IsNullOrWhiteSpace(request.PageUrl) ? ShortUrl(request.PageUrl!) : "Browser activity";
        steps.Add(new("Triggered", $"{InitiatorLabel(request)} · {source}", NetworkFindingSeverity.Info));

        var preflight = FindPreflight(request, all);
        if (preflight is not null)
            steps.Add(new("Preflight", preflight.IsFailed ? $"OPTIONS failed · {preflight.StatusText}" : $"OPTIONS approved · {preflight.DurationText}", preflight.IsFailed ? NetworkFindingSeverity.Error : NetworkFindingSeverity.Good));

        AddMeasuredStep(steps, "Queued", request.Timing.BlockedMs, NetworkFindingSeverity.Warning);
        AddMeasuredStep(steps, "DNS", request.Timing.DnsMs, NetworkFindingSeverity.Info);
        if (request.ConnectionReused)
            steps.Add(new("Connection reused", "No new connection setup was required · reported by Chromium", NetworkFindingSeverity.Good));
        else
            AddMeasuredStep(steps, "Connected", ExclusiveConnectMs(request), NetworkFindingSeverity.Info);
        AddMeasuredStep(steps, "TLS", request.Timing.TlsMs, NetworkFindingSeverity.Info);
        steps.Add(new("Sent", $"{request.Method} to {Host(request.Url)}", NetworkFindingSeverity.Info));
        AddMeasuredStep(steps, "Upload", request.Timing.SendMs, NetworkFindingSeverity.Info);
        AddMeasuredStep(steps, "Waiting for server", request.Timing.WaitMs, NetworkFindingSeverity.Warning);
        if (request.StatusCode is >= 300 and < 400)
            steps.Add(new("Redirected", RedirectExplanation(request), NetworkFindingSeverity.Warning));
        if (request.IsFailed)
            steps.Add(new("Stopped", string.IsNullOrWhiteSpace(request.FailureReason) ? request.StatusText : request.FailureReason, NetworkFindingSeverity.Error));
        else if (request.StatusCode is not null)
            steps.Add(new("Responded", $"HTTP {request.StatusCode} · {request.ResponseContentType}", request.StatusCode >= 400 ? NetworkFindingSeverity.Error : NetworkFindingSeverity.Good));
        else
            steps.Add(new("Waiting", "The browser has not reported a response yet.", NetworkFindingSeverity.Warning));
        if (request.DurationMs is not null)
        {
            if (request.CacheSource != "Network")
                steps.Add(new("Response source", $"{request.CacheSource} · reported by Chromium", NetworkFindingSeverity.Good));
            AddMeasuredStep(steps, "Downloaded", request.Timing.ReceiveMs, NetworkFindingSeverity.Info);
            steps.Add(new("Completed", request.DurationText, IsSlow(request) ? NetworkFindingSeverity.Warning : NetworkFindingSeverity.Good));
        }
        return steps;
    }

    public static IReadOnlyList<RelatedNetworkRequest> Related(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all)
    {
        var related = new List<RelatedNetworkRequest>();
        foreach (var candidate in all)
        {
            if (candidate == request) continue;
            string? relationship = null;
            if (IsPreflightPair(candidate, request)) relationship = candidate.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ? "Preflight" : "Preflight target";
            else if (RedirectsTo(candidate, request)) relationship = "Redirected here";
            else if (RedirectsTo(request, candidate)) relationship = "Redirect destination";
            else if (SameExchange(candidate, request)) relationship = "Repeated call";
            else if (!string.IsNullOrWhiteSpace(request.InitiatorUrl) && candidate.Url.Equals(request.InitiatorUrl, StringComparison.OrdinalIgnoreCase)) relationship = "Initiator";
            if (relationship is not null)
                related.Add(new(candidate, relationship, $"{candidate.Method} · {candidate.StatusText} · {candidate.DurationText}"));
        }
        return related.OrderBy(item => Math.Abs(item.Request.StartedAt - request.StartedAt)).Take(12).ToList();
    }

    private static CapturedNetworkRequest? FindPreflight(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all) =>
        all.Where(candidate => candidate != request && candidate.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) && SameUrl(candidate.Url, request.Url) && candidate.StartedAt <= request.StartedAt)
           .OrderBy(candidate => request.StartedAt - candidate.StartedAt).FirstOrDefault();

    private static bool IsPreflightPair(CapturedNetworkRequest a, CapturedNetworkRequest b) =>
        SameUrl(a.Url, b.Url) && (a.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ^ b.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase));

    private static bool RedirectsTo(CapturedNetworkRequest source, CapturedNetworkRequest target)
    {
        if (source.StatusCode is not (>= 300 and < 400) || !source.ResponseHeaders.TryGetValue("Location", out var location)) return false;
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var baseUri) || !Uri.TryCreate(baseUri, location, out var destination)) return false;
        return SameUrl(destination.AbsoluteUri, target.Url);
    }

    private static bool SameExchange(CapturedNetworkRequest a, CapturedNetworkRequest b) => a.Method.Equals(b.Method, StringComparison.OrdinalIgnoreCase) && SameUrl(a.Url, b.Url);
    private static bool SameUrl(string a, string b) => a.TrimEnd('/').Equals(b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private static bool HasUnexpectedContent(CapturedNetworkRequest request) => request.ResourceType is "XHR" or "Fetch" && request.ResponseContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    private static bool HasCacheConcern(CapturedNetworkRequest request) => request.ResourceType is "Script" or "Stylesheet" or "Image" or "Font" && !request.ResponseHeaders.ContainsKey("Cache-Control") && !request.ResponseHeaders.ContainsKey("Expires") && request.StatusCode is >= 200 and < 300;
    private static bool HasNoStore(CapturedNetworkRequest request) => request.ResponseHeaders.TryGetValue("Cache-Control", out var cache) && cache.Contains("no-store", StringComparison.OrdinalIgnoreCase);
    private static bool IsSlow(CapturedNetworkRequest request) => request.DurationMs >= SlowThresholdMs(request);
    private static double SlowThresholdMs(CapturedNetworkRequest request) => request.ResourceType switch
    {
        "XHR" or "Fetch" => 1000,
        "Document" => 2000,
        "Script" or "Stylesheet" or "Font" => 750,
        "Image" or "Media" => 3000,
        _ => 1500
    };

    private static (string Title, string Explanation, string NextStep) ExplainTiming(CapturedNetworkRequest request)
    {
        if (!request.Timing.HasRecordedPhases)
            return ("This request is slow for its type", $"The {request.ResourceType.ToLowerInvariant()} completed in {request.DurationText}, but Chromium did not expose phase-level timing for this exchange.", "Inspect related requests and server-side traces; DevBrowser will not estimate missing phases.");

        var phases = new List<(string Name, double Value, string NextStep)>();
        AddPhase(phases, "browser queueing", request.Timing.BlockedMs, "Check request priority, connection limits, and excessive parallel calls to this host.");
        AddPhase(phases, "DNS lookup", request.Timing.DnsMs, "Check DNS resolution, proxy configuration, and geographic resolver latency.");
        AddPhase(phases, "connection setup", ExclusiveConnectMs(request), "Check connection reuse, proxy latency, and distance to the remote endpoint.");
        AddPhase(phases, "TLS negotiation", request.Timing.TlsMs, "Check connection reuse, certificate-chain size, and TLS termination latency.");
        AddPhase(phases, "uploading the request", request.Timing.SendMs, "Inspect request-body size and upload bandwidth.");
        AddPhase(phases, "waiting for the server", request.Timing.WaitMs, "Investigate backend processing, database queries, and downstream services.");
        AddPhase(phases, "downloading the response", request.Timing.ReceiveMs, "Inspect response size, compression, streaming, and available bandwidth.");
        if (phases.Count == 0)
            return ("This request is slow for its type", $"The exchange took {request.DurationText}; all exposed network phases were effectively zero or unavailable.", "Use server-side traces and related requests to investigate the remaining time.");

        var dominant = phases.MaxBy(phase => phase.Value);
        var percentage = request.DurationMs > 0 ? Math.Clamp(dominant.Value / request.DurationMs.Value * 100, 0, 100) : 0;
        return ($"Most measured time was spent {dominant.Name}", $"Chromium recorded {dominant.Value:0.##} ms in this phase ({percentage:0}% of the {request.DurationText} total). Unavailable phases are not estimated.", dominant.NextStep);
    }

    private static void AddPhase(List<(string Name, double Value, string NextStep)> phases, string name, double? value, string nextStep)
    {
        if (value > 0) phases.Add((name, value.Value, nextStep));
    }

    private static double? ExclusiveConnectMs(CapturedNetworkRequest request)
    {
        if (request.ConnectionReused) return null;
        if (request.Timing.ConnectMs is null) return null;
        return Math.Max(0, request.Timing.ConnectMs.Value - (request.Timing.TlsMs ?? 0));
    }

    private static void AddMeasuredStep(List<NetworkStoryStep> steps, string stage, double? duration, NetworkFindingSeverity notableSeverity)
    {
        if (duration is null) return;
        var severity = duration >= 500 ? notableSeverity : NetworkFindingSeverity.Good;
        steps.Add(new(stage, $"{duration.Value:0.##} ms · measured by Chromium", severity));
    }

    private static IReadOnlyList<NetworkFinding> SuspiciousReasons(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all)
    {
        var findings = new List<NetworkFinding>();
        var duplicates = all.Count(candidate => candidate != request && IsNearDuplicate(candidate, request));
        if (duplicates > 0)
            findings.Add(new(NetworkProblemCategory.Suspicious, NetworkFindingSeverity.Warning, "A near-identical call repeats", $"{duplicates + 1} matching {request.Method} requests with the same body started within two seconds.", "Use Related requests to confirm whether one user action triggered duplicate work."));

        var authRetries = all.Count(candidate => candidate != request && SameUrl(candidate.Url, request.Url) && candidate.StatusCode is 401 or 403 && Math.Abs(candidate.StartedAt - request.StartedAt) <= 10);
        if (request.StatusCode is 401 or 403 && authRetries > 0)
            findings.Add(new(NetworkProblemCategory.Suspicious, NetworkFindingSeverity.Warning, "Authentication is retrying", $"{authRetries + 1} rejected calls to this endpoint occurred within ten seconds.", "Check whether token refresh is failing or the client is retrying a request that cannot succeed."));

        if (HasRedirectProblem(request, all, out var redirectDetail))
            findings.Add(new(NetworkProblemCategory.Suspicious, NetworkFindingSeverity.Warning, "The redirect path needs attention", redirectDetail, "Inspect Related requests for a loop or an unexpectedly long redirect chain."));

        if (HasApplicationError(request))
            findings.Add(new(NetworkProblemCategory.Suspicious, NetworkFindingSeverity.Warning, "The HTTP request succeeded but the payload reports an error", "A successful 2xx response contains a top-level error or errors value.", "Inspect the response body; application-level failures may not be represented by the HTTP status."));
        return findings;
    }

    private static bool IsNearDuplicate(CapturedNetworkRequest candidate, CapturedNetworkRequest request) =>
        SameExchange(candidate, request) && candidate.RequestBody == request.RequestBody && Math.Abs(candidate.StartedAt - request.StartedAt) <= 2;

    private static bool HasRedirectProblem(CapturedNetworkRequest request, IReadOnlyList<CapturedNetworkRequest> all, out string detail)
    {
        detail = string.Empty;
        if (request.StatusCode is not (>= 300 and < 400)) return false;
        if (request.ResponseHeaders.TryGetValue("Location", out var location) && Uri.TryCreate(request.Url, UriKind.Absolute, out var source) && Uri.TryCreate(source, location, out var destination) && SameUrl(source.AbsoluteUri, destination.AbsoluteUri))
        {
            detail = "The redirect points back to the same URL.";
            return true;
        }
        var nearbyRedirects = all.Count(candidate => candidate.StatusCode is >= 300 and < 400 && candidate.PageUrl == request.PageUrl && Math.Abs(candidate.StartedAt - request.StartedAt) <= 10);
        if (nearbyRedirects >= 3)
        {
            detail = $"At least {nearbyRedirects} redirects occurred in this navigation window.";
            return true;
        }
        return false;
    }

    private static bool HasApplicationError(CapturedNetworkRequest request)
    {
        if (request.StatusCode is not (>= 200 and < 300) || string.IsNullOrWhiteSpace(request.ResponseBody)) return false;
        try
        {
            using var document = JsonDocument.Parse(request.ResponseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   ((document.RootElement.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null) ||
                    (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind is JsonValueKind.Array && errors.GetArrayLength() > 0));
        }
        catch (JsonException) { return false; }
    }
    private static string RedirectExplanation(CapturedNetworkRequest request) => request.ResponseHeaders.TryGetValue("Location", out var location) ? $"HTTP {request.StatusCode} → {location}" : $"HTTP {request.StatusCode} redirect; the destination was not exposed in captured headers.";
    private static string InitiatorLabel(CapturedNetworkRequest request) => string.IsNullOrWhiteSpace(request.InitiatorType) ? "Page" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.InitiatorType!);
    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    private static string ShortUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath : url;
}
