namespace DeveloperBrowser.Core.Rest;
public sealed record RestRequest(HttpMethod Method, Uri Uri, string? Body = null, IReadOnlyDictionary<string, string>? Headers = null);
public sealed record RestResponse(int StatusCode, string Body, IReadOnlyDictionary<string, string[]> Headers);
public interface IRestClient { Task<RestResponse> SendAsync(RestRequest request, CancellationToken cancellationToken = default); }
