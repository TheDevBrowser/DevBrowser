namespace DeveloperBrowser.Core.Rest;

public enum CrossOriginHeaderPolicy { Ask, Remove }
public enum CrossOriginBodyPolicy { Ask, Block, Allow }

public sealed record RedirectTrust(string SourceOrigin, string DestinationOrigin, bool Credentials, bool Body);

public sealed record RedirectSettings
{
    public bool FollowRedirects { get; init; } = true;
    public CrossOriginHeaderPolicy Headers { get; init; } = CrossOriginHeaderPolicy.Ask;
    public CrossOriginBodyPolicy Body { get; init; } = CrossOriginBodyPolicy.Ask;
    public bool AllowHttpsToHttp { get; init; }
    public int MaxRedirects { get; init; } = 10;
    public List<RedirectTrust> TrustedOrigins { get; init; } = [];

    public void Validate()
    {
        if (MaxRedirects is < 0 or > 50 || !Enum.IsDefined(Headers) || !Enum.IsDefined(Body) || TrustedOrigins is null)
            throw new InvalidOperationException("Invalid redirect settings. Maximum redirects must be between 0 and 50.");
    }

    public static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
}

public sealed record RedirectPrompt(int StatusCode, Uri Source, Uri Destination, string Method,
    IReadOnlyList<string> SensitiveHeaders, bool NeedsBodyApproval);
public sealed record RedirectDecision(bool Follow, bool ForwardCredentials = false, bool ForwardBody = false)
{
    public static RedirectDecision Stop { get; } = new(false);
}
public sealed record RedirectHop(int StatusCode, string SourceOrigin, string DestinationOrigin, string Outcome);
