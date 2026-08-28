namespace DeveloperBrowser.Core.Networking;

/// <summary>Pure CORS/CSP analysis over captured browser-network evidence.</summary>
public static class NetworkPolicyAnalyzer
{
    public static NetworkPolicyReport Analyze(NetworkRequestEvidence request, IReadOnlyCollection<NetworkRequestEvidence> allRequests)
    {
        var csp = AnalyzeCsp(request);
        if (csp is not null) return csp;
        return AnalyzeCors(request, allRequests) ?? NetworkPolicyReport.None;
    }

    private static NetworkPolicyReport? AnalyzeCors(NetworkRequestEvidence request, IReadOnlyCollection<NetworkRequestEvidence> allRequests)
    {
        var pageOrigin = Origin(request.PageUrl);
        var targetOrigin = Origin(request.Url);
        var crossOrigin = pageOrigin is not null && targetOrigin is not null && !SameOrigin(pageOrigin, targetOrigin);
        var browserMarkedCors = Contains(request.BlockedReason, "cors") || !string.IsNullOrWhiteSpace(request.CorsError);
        if (!crossOrigin) return null;

        var preflight = allRequests.Where(candidate => candidate.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                                                       (candidate.ResourceType.Equals("Preflight", StringComparison.OrdinalIgnoreCase) || candidate.Url.Equals(request.Url, StringComparison.OrdinalIgnoreCase)) &&
                                                       SameOrigin(Origin(candidate.PageUrl), pageOrigin) &&
                                                       SameOrigin(Origin(candidate.Url), targetOrigin))
                                  .OrderByDescending(candidate => candidate.StartedAt).FirstOrDefault();
        if (!browserMarkedCors && !(request.Failed && request.StatusCode is null) && preflight is null) return null;
        var facts = new List<string>
        {
            $"Page origin: {DisplayOrigin(pageOrigin)}", $"Target origin: {DisplayOrigin(targetOrigin)}",
            $"Request: {request.Method} {request.Url}", "Cross-origin: yes"
        };
        if (!string.IsNullOrWhiteSpace(request.BlockedReason)) facts.Add($"Browser blocked reason: {request.BlockedReason}");
        if (!string.IsNullOrWhiteSpace(request.CorsError)) facts.Add($"Browser CORS detail: {request.CorsError}");
        facts.Add($"Credentials mode: {request.CredentialsDescription}");

        var customHeaders = RequestedHeaders(request, preflight);
        if (customHeaders.Count > 0) facts.Add($"Requested non-simple headers: {string.Join(", ", customHeaders)}");
        var responseHeaders = preflight?.ResponseHeaders ?? request.ResponseHeaders;
        if (preflight is not null)
        {
            facts.Add($"Preflight: OPTIONS {preflight.Url}");
            facts.Add(preflight.StatusCode is null ? "Preflight status: no response observed" : $"Preflight status: {preflight.StatusCode}");
            if (preflight.RequestHeaders.TryGetValue("Access-Control-Request-Headers", out var requested)) facts.Add($"Preflight requested headers: {requested}");
        }
        else facts.Add("Preflight: not observed");
        AddCorsHeaderFacts(facts, responseHeaders);

        var diagnosis = DiagnoseCors(request, preflight, pageOrigin, responseHeaders, customHeaders);
        return new NetworkPolicyReport(browserMarkedCors ? "CORS FAILURE" : "POSSIBLE CORS FAILURE", facts, diagnosis, SuggestCorsFix(diagnosis, pageOrigin, targetOrigin));
    }

    private static NetworkPolicyReport? AnalyzeCsp(NetworkRequestEvidence request)
    {
        var browserMarkedCsp = Contains(request.BlockedReason, "csp") || Contains(request.FailureReason, "content security policy");
        if (!browserMarkedCsp) return null;

        var pageOrigin = Origin(request.PageUrl);
        var targetOrigin = Origin(request.Url);
        var facts = new List<string>
        {
            $"Page: {request.PageUrl ?? "Not available"}", $"Blocked resource: {request.Url}",
            $"Resource type: {FriendlyResourceType(request.ResourceType)}",
            $"Browser blocked reason: {request.BlockedReason ?? "CSP indicated by failure data"}"
        };
        var policies = request.ContentSecurityPolicies.SelectMany(ParsePolicy).ToList();
        var directiveNames = DirectiveCandidates(request.ResourceType);
        var matchingDirective = directiveNames.SelectMany(name => policies.Where(policy => policy.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
        if (matchingDirective is null)
        {
            facts.Add("Observed policy: no matching directive was available from captured document headers or HTML metadata.");
            return new NetworkPolicyReport("CSP BLOCKED REQUEST", facts,
                "The browser reported a CSP block, but the effective directive could not be determined from captured policy data.",
                "Review the page's Content-Security-Policy header and any CSP meta tag. If this resource is intentional, allow only its specific origin in the applicable directive.");
        }

        facts.Add($"Violated directive: {matchingDirective.Name}");
        facts.Add($"Current allowed sources: {(matchingDirective.Sources.Count == 0 ? "'none'" : string.Join(" ", matchingDirective.Sources))}");
        var allowed = SourceAllows(matchingDirective.Sources, targetOrigin, pageOrigin);
        var diagnosis = allowed
            ? "The browser reported a CSP block, but the captured directive appears to allow this origin. Another policy, a nonce/hash requirement, or policy not captured by the network trace may be responsible."
            : $"{DisplayOrigin(targetOrigin)} does not match the captured {matchingDirective.Name} source list.";
        var suggestion = $"If this external resource is intentionally required, consider adding its exact origin {DisplayOrigin(targetOrigin)} to {matchingDirective.Name}. Do not broaden the policy or add it if the request is unexpected.";
        return new NetworkPolicyReport("CSP BLOCKED REQUEST", facts, diagnosis, suggestion);
    }

    private static string DiagnoseCors(NetworkRequestEvidence request, NetworkRequestEvidence? preflight, Uri? pageOrigin, IReadOnlyDictionary<string, string> headers, IReadOnlyList<string> customHeaders)
    {
        if (preflight is not null && preflight.StatusCode is not null && (preflight.StatusCode < 200 || preflight.StatusCode >= 300)) return $"The observed preflight returned HTTP {preflight.StatusCode}, which is not a successful preflight response.";
        if (preflight is not null && preflight.StatusCode is null) return "A preflight was observed, but no successful preflight response was captured. The main request may have been blocked before it was sent.";
        if (!headers.TryGetValue("Access-Control-Allow-Origin", out var allowedOrigin)) return "No Access-Control-Allow-Origin header was observed on the relevant response.";
        if (pageOrigin is not null && allowedOrigin != "*" && !allowedOrigin.Equals(DisplayOrigin(pageOrigin), StringComparison.OrdinalIgnoreCase)) return $"The server allows {allowedOrigin}, which does not match the current page origin {DisplayOrigin(pageOrigin)}.";
        var credentialsObserved = request.CredentialsDescription.StartsWith("Credentials may be included", StringComparison.Ordinal);
        if (credentialsObserved && allowedOrigin == "*") return "The response uses Access-Control-Allow-Origin: * while credentials may be included; browsers do not allow this combination.";
        if (credentialsObserved && (!headers.TryGetValue("Access-Control-Allow-Credentials", out var allowCredentials) || !allowCredentials.Equals("true", StringComparison.OrdinalIgnoreCase))) return "Credentials may be included, but Access-Control-Allow-Credentials: true was not observed.";
        if (headers.TryGetValue("Access-Control-Allow-Methods", out var methods) && !ContainsToken(methods, request.Method)) return $"The requested method {request.Method} is not listed in Access-Control-Allow-Methods.";
        if (preflight is not null && !headers.ContainsKey("Access-Control-Allow-Methods")) return "A preflight was observed, but no Access-Control-Allow-Methods header was observed.";
        if (customHeaders.Count > 0 && headers.TryGetValue("Access-Control-Allow-Headers", out var allowedHeaders) && customHeaders.Any(header => !ContainsToken(allowedHeaders, header))) return "At least one requested non-simple header is not listed in Access-Control-Allow-Headers.";
        if (preflight is not null && customHeaders.Count > 0 && !headers.ContainsKey("Access-Control-Allow-Headers")) return "A preflight requested non-simple headers, but no Access-Control-Allow-Headers header was observed.";
        if (!string.IsNullOrWhiteSpace(request.CorsError)) return $"Chromium reported the CORS error {request.CorsError}. The available headers do not identify a more specific mismatch.";
        return "This cross-origin request failed, but the captured browser data is insufficient to confirm the exact CORS rule that blocked it.";
    }

    private static string SuggestCorsFix(string diagnosis, Uri? pageOrigin, Uri? targetOrigin)
    {
        var source = DisplayOrigin(pageOrigin);
        if (diagnosis.Contains("does not match", StringComparison.OrdinalIgnoreCase) || diagnosis.Contains("Allow-Origin", StringComparison.OrdinalIgnoreCase)) return $"If this page should call the API, add {source} to the API's allowed origins for the appropriate development environment.";
        if (diagnosis.Contains("method", StringComparison.OrdinalIgnoreCase)) return "If this operation is intended, include the required HTTP method in Access-Control-Allow-Methods for this origin.";
        if (diagnosis.Contains("header", StringComparison.OrdinalIgnoreCase)) return "If these headers are intentional, include only the required header names in Access-Control-Allow-Headers.";
        if (diagnosis.Contains("credentials", StringComparison.OrdinalIgnoreCase)) return $"For credentialed requests, return Access-Control-Allow-Origin: {source} (not *) and Access-Control-Allow-Credentials: true.";
        if (diagnosis.Contains("preflight", StringComparison.OrdinalIgnoreCase)) return "Ensure the OPTIONS preflight route is reachable without authentication redirects and returns a successful response with the required CORS headers.";
        return $"Review the API CORS policy for {DisplayOrigin(targetOrigin)} and confirm it intentionally permits requests from {source}.";
    }

    private static void AddCorsHeaderFacts(List<string> facts, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var name in CorsHeaders) if (headers.TryGetValue(name, out var value)) facts.Add($"{name}: {value}");
    }
    private static IReadOnlyList<string> RequestedHeaders(NetworkRequestEvidence request, NetworkRequestEvidence? preflight)
    {
        if (preflight?.RequestHeaders.TryGetValue("Access-Control-Request-Headers", out var requested) == true) return requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return request.RequestHeaders.Keys.Where(header => !SimpleRequestHeaders.Contains(header) && !header.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) && !header.Equals("Origin", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private static IEnumerable<PolicyDirective> ParsePolicy(string policy) =>
        policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
              .Select(part => part.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
              .Where(parts => parts.Length > 0).Select(parts => new PolicyDirective(parts[0], parts.Skip(1).ToArray()));
    private static IReadOnlyList<string> DirectiveCandidates(string type) => type switch
    {
        "XHR" or "Fetch" => ["connect-src", "default-src"],
        "Script" => ["script-src-elem", "script-src", "default-src"],
        "Stylesheet" => ["style-src", "default-src"], "Image" => ["img-src", "default-src"],
        "Font" => ["font-src", "default-src"], "Media" => ["media-src", "default-src"],
        "Worker" => ["worker-src", "child-src", "script-src", "default-src"],
        "Document" or "Frame" => ["frame-ancestors", "frame-src", "child-src", "default-src"],
        "Object" => ["object-src", "default-src"], _ => ["default-src"]
    };
    private static bool SourceAllows(IReadOnlyList<string> sources, Uri? target, Uri? page)
    {
        if (target is null) return false;
        foreach (var source in sources)
        {
            if (source == "*") return true;
            if (source.Equals("'self'", StringComparison.OrdinalIgnoreCase) && SameOrigin(target, page)) return true;
            if (source.EndsWith(':') && source[..^1].Equals(target.Scheme, StringComparison.OrdinalIgnoreCase)) return true;
            if (!Uri.TryCreate(source.Replace("*.", "placeholder."), UriKind.Absolute, out var sourceUri)) continue;
            if (!sourceUri.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            var sourceHost = sourceUri.Host.Replace("placeholder.", "*.", StringComparison.Ordinal);
            if (sourceHost.StartsWith("*.", StringComparison.Ordinal) && target.Host.EndsWith(sourceHost[1..], StringComparison.OrdinalIgnoreCase)) return true;
            if (sourceHost.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && sourceUri.Port == target.Port) return true;
        }
        return false;
    }
    private static Uri? Origin(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? new Uri(uri.GetLeftPart(UriPartial.Authority)) : null;
    private static bool SameOrigin(Uri? left, Uri? right) => left is not null && right is not null && left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
    private static string DisplayOrigin(Uri? origin) => origin?.GetLeftPart(UriPartial.Authority) ?? "Not available";
    private static bool Contains(string? value, string search) => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
    private static bool ContainsToken(string value, string token) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(item => item.Equals(token, StringComparison.OrdinalIgnoreCase));
    private static string FriendlyResourceType(string type) => type is "XHR" or "Fetch" ? "Connection" : type;
    private static readonly string[] CorsHeaders = ["Access-Control-Allow-Origin", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers", "Access-Control-Allow-Credentials", "Access-Control-Expose-Headers", "Access-Control-Max-Age"];
    private static readonly HashSet<string> SimpleRequestHeaders = new(StringComparer.OrdinalIgnoreCase) { "Accept", "Accept-Language", "Content-Language", "Content-Type", "User-Agent", "Referer" };
    private sealed record PolicyDirective(string Name, IReadOnlyList<string> Sources);
}

public sealed record NetworkRequestEvidence(string RequestId, string Url, string Method, string ResourceType, double StartedAt, string? PageUrl, IReadOnlyDictionary<string, string> RequestHeaders, IReadOnlyDictionary<string, string> ResponseHeaders, int? StatusCode, bool Failed, string? FailureReason, string? BlockedReason, string? CorsError, string CredentialsDescription, IReadOnlyList<string> ContentSecurityPolicies);

public sealed record NetworkPolicyReport(string Title, IReadOnlyList<string> ObservedFacts, string LikelyDiagnosis, string SuggestedFix)
{
    public static NetworkPolicyReport None { get; } = new("NO CORS / CSP BLOCK OBSERVED", ["The browser did not mark this request as blocked by CORS or CSP."], "No CORS or CSP diagnosis is available for this request.", "Select a failed request marked with a CORS or CSP browser-block reason to inspect it.");
    public string ToDisplayText() => $"{Title}{Environment.NewLine}{Environment.NewLine}OBSERVED FACTS{Environment.NewLine}{string.Join(Environment.NewLine, ObservedFacts.Select(fact => $"• {fact}"))}{Environment.NewLine}{Environment.NewLine}LIKELY DIAGNOSIS{Environment.NewLine}{LikelyDiagnosis}{Environment.NewLine}{Environment.NewLine}SUGGESTED FIX{Environment.NewLine}{SuggestedFix}";
}
