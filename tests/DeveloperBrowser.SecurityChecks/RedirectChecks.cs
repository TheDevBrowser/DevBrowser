using System.Net;
using System.Net.Sockets;
using System.Text;
using DeveloperBrowser.Core.Rest;
using DeveloperBrowser.Infrastructure.Rest;

internal static class RedirectChecks
{
    public static async Task RunAsync()
    {
        foreach (var (status, original, expected, body) in new[]
        {
            (301, "POST", "GET", false), (302, "POST", "GET", false), (303, "PUT", "GET", false),
            (303, "HEAD", "HEAD", false), (307, "POST", "POST", true), (308, "PUT", "PUT", true),
            (301, "PUT", "PUT", true), (302, "GET", "GET", false)
        })
        {
            var transport = new RecordingHandler((_, index) => index == 0 ? Redirect(status, "/next") : Ok());
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start", original, original is "POST" or "PUT");
            using var result = await client.SendAsync(request);
            var next = transport.Seen.Last();
            Check(next.Method == expected && (next.Body is not null) == body, $"Method/body handling for {status} {original}");
            Check(next.Headers["X-Strange-Credential"] == "SECRET" && next.Headers["Cookie"] == "session=SECRET", "Same-origin credentials retained");
        }

        foreach (var target in new[] { "https://b.test/next", "https://a.test:444/next" })
        {
            var transport = new RecordingHandler((_, i) => i == 0 ? Redirect(302, target) : Ok());
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var stopped = await client.SendAsync(request);
            Check(transport.Seen.Count == 1 && (int)stopped.Response.StatusCode == 302, "Unapproved hostname/port change stops");
        }

        {
            var transport = new RecordingHandler((_, i) => i switch { 0 => Redirect(302, "https://b.test/"), 1 => Redirect(302, "https://a.test/back"), _ => Ok() });
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var result = await client.SendAsync(request, decide: (_, _) => Task.FromResult(new RedirectDecision(true)));
            Check(transport.Seen.Count == 3, "Sanitized chain follows");
            foreach (var next in transport.Seen.Skip(1))
                Check(!next.Headers.ContainsKey("Authorization") && !next.Headers.ContainsKey("Cookie") && !next.Headers.ContainsKey("X-Strange-Credential"), "Credentials stripped and never resurrected");
            Check(!string.Join("", result.History).Contains("SECRET"), "History excludes credential values");
        }
        {
            var transport = new RecordingHandler((_, i) => i switch { 0 => Redirect(302, "https://b.test/"), 1 => Redirect(302, "https://c.test/"), _ => Ok() });
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            var settings = new RedirectSettings { TrustedOrigins = [new("https://a.test", "https://b.test", true, false)] };
            using var result = await client.SendAsync(request, settings);
            Check(transport.Seen.Count == 2 && transport.Seen[1].Headers.ContainsKey("X-Strange-Credential"), "Trust applies to exact pair only");
        }
        foreach (var status in new[] { 307, 308 })
        {
            foreach (var approved in new[] { false, true })
            {
                var transport = new RecordingHandler((_, i) => i == 0 ? Redirect(status, "https://b.test/") : Ok());
                using var client = new SafeRedirectClient(transport);
                using var request = Request("https://a.test/start", "POST", true);
                using var result = await client.SendAsync(request, decide: (prompt, _) =>
                {
                    Check(prompt.NeedsBodyApproval, "Body requires approval");
                    return Task.FromResult(new RedirectDecision(true, false, approved));
                });
                Check(transport.Seen.Count == (approved ? 2 : 1), "Body cannot leave without approval");
                if (approved)
                    Check(transport.Seen[1].Body == "BODY_SECRET" && !transport.Seen[1].Headers.ContainsKey("X-Body-Key"), "Approved body sent, custom content headers stripped");
            }
        }
        foreach (var bodyPolicy in new[] { CrossOriginBodyPolicy.Block, CrossOriginBodyPolicy.Allow })
        {
            var transport = new RecordingHandler((_, i) => i == 0 ? Redirect(307, "https://b.test/") : Ok());
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start", "POST", true);
            using var result = await client.SendAsync(request, new() { Headers = CrossOriginHeaderPolicy.Remove, Body = bodyPolicy });
            Check(transport.Seen.Count == (bodyPolicy == CrossOriginBodyPolicy.Allow ? 2 : 1), "Body policy override");
        }
        foreach (var allow in new[] { false, true })
        {
            var transport = new RecordingHandler((_, i) => i == 0 ? Redirect(302, "http://a.test/") : Ok());
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var result = await client.SendAsync(request, new() { AllowHttpsToHttp = allow, Headers = CrossOriginHeaderPolicy.Remove });
            Check(transport.Seen.Count == (allow ? 2 : 1), "Downgrade policy");
            if (allow) Check(!transport.Seen[1].Headers.ContainsKey("Cookie"), "Downgrade still strips credentials");
        }
        foreach (var invalid in new[] { "file:///C:/secret", "https://user:password@b.test/" })
        {
            var transport = new RecordingHandler((_, _) => Redirect(302, invalid));
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var result = await client.SendAsync(request);
            Check(transport.Seen.Count == 1, "Unsafe redirect URI blocked");
        }
        foreach (var limit in new[] { 0, 3, 10 })
        {
            var transport = new RecordingHandler((_, _) => Redirect(302, "/loop"));
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var result = await client.SendAsync(request, new() { MaxRedirects = limit });
            Check(transport.Seen.Count == limit + 1, "Redirect loop bounded");
        }
        {
            var transport = new RecordingHandler((_, _) => Redirect(302, "/next"));
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var result = await client.SendAsync(request, new() { FollowRedirects = false });
            Check(transport.Seen.Count == 1, "Redirects can be disabled");
        }
        {
            var transport = new RecordingHandler((_, _) => Redirect(302, "https://b.test/"));
            using var client = new SafeRedirectClient(transport);
            using var request = Request("https://a.test/start");
            using var cancellation = new CancellationTokenSource();
            try
            {
                using var result = await client.SendAsync(request, decide: (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(new RedirectDecision(true, true));
                }, ct: cancellation.Token);
                throw new Exception("Cancellation was ignored");
            }
            catch (OperationCanceledException) { Check(transport.Seen.Count == 1, "Cancelled redirect is never sent"); }
        }
        // Real loopback servers validate that HttpClient's own redirect/cookie handling cannot bypass policy.
        using (var target = new LoopbackServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"))
        using (var origin = new LoopbackServer(_ => $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {target.Url}\r\nSet-Cookie: hidden=SECRET; Path=/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
        using (var client = new SafeRedirectClient())
        {
            using var request = Request(origin.Url, "POST", true);
            using var stopped = await client.SendAsync(request);
            Check(target.Seen.Count == 0, "Real transport does not auto-follow");
            using var followed = await client.SendAsync(request, decide: (_, _) => Task.FromResult(new RedirectDecision(true, false, true)));
            Check((int)followed.Response.StatusCode == 200, "Real cross-port redirect succeeded");
            var received = target.Seen.Single();
            Check(received.Contains("BODY_SECRET") && !received.Contains("session=SECRET") && !received.Contains("hidden=SECRET") &&
                !received.Contains("X-Strange-Credential", StringComparison.OrdinalIgnoreCase) && !received.Contains("Authorization", StringComparison.OrdinalIgnoreCase), "Real destination receives only approved data");
        }
        Console.WriteLine("PASS: redirect method rules, credential stripping, body approval, origin-pair trust, downgrade/limits/cancellation and real loopback transport");
    }

    private static HttpRequestMessage Request(string url, string method = "GET", bool body = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer SECRET");
        request.Headers.TryAddWithoutValidation("Cookie", "session=SECRET");
        request.Headers.TryAddWithoutValidation("X-Strange-Credential", "SECRET");
        if (body) { request.Content = new StringContent("BODY_SECRET"); request.Content.Headers.TryAddWithoutValidation("X-Body-Key", "SECRET"); }
        return request;
    }
    private static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("OK") };
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }

    private sealed record Snapshot(string Method, string? Body, Dictionary<string, string> Headers);
    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Snapshot> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add(new(request.Method.Method, body, request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return respond(request, Seen.Count - 1);
        }
    }
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task task;
        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = [];
        public string Url { get; }
        public LoopbackServer(Func<string, string> respond)
        {
            listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            task = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                        using var stream = socket.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                        var received = new StringBuilder(); var length = 0;
                        while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                        {
                            received.AppendLine(line);
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
                        }
                        var body = new char[length];
                        if (await reader.ReadBlockAsync(body.AsMemory(), stop.Token) != length) throw new IOException("Incomplete test request");
                        received.Append(body);
                        Seen.Add(received.ToString());
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(respond(received.ToString())), stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) when (stop.IsCancellationRequested) { }
            });
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); task.GetAwaiter().GetResult(); stop.Dispose(); }
    }
}
