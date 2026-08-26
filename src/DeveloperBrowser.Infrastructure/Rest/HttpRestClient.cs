using DeveloperBrowser.Core.Rest;
namespace DeveloperBrowser.Infrastructure.Rest;
public sealed class HttpRestClient(HttpClient httpClient) : IRestClient
{
    public async Task<RestResponse> SendAsync(RestRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(request.Method, request.Uri);
        if (request.Body is not null) message.Content = new StringContent(request.Body, System.Text.Encoding.UTF8, "application/json");
        if (request.Headers is not null) foreach (var (key, value) in request.Headers) message.Headers.TryAddWithoutValidation(key, value);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(header => header.Key, header => header.Value.ToArray());
        return new RestResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), headers);
    }
}
