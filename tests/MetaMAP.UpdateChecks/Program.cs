using MetaMap;
using System.Net;
using Newtonsoft.Json;

int passed = 0;
await Check("0.0.59", "0.0.60", "available on Yak", instructions: true);
await Check("0.0.60", "0.0.60", "up to date");
await Check("0.0.60.0", "0.0.60", "up to date");
await Check("0.0.60", "0.0.59", "newer than Yak's published version");
await Check("0.0.9", "0.0.10", "available on Yak", instructions: true);
await Check("0.0.60", "0.1.0", "available on Yak", instructions: true);
await Reject<InvalidOperationException>("{\"name\":\"OtherPlugin\",\"version\":\"1.0.0\"}");
await Reject<InvalidOperationException>("{\"name\":\"MetaMAP\"}");
await Reject<InvalidOperationException>("{\"name\":\"MetaMAP\",\"version\":\"invalid\"}");
await Reject<InvalidOperationException>("{\"name\":\"MetaMAP\",\"version\":\"0.1.0-beta\"}");
await Reject<JsonReaderException>("<html>Server unavailable</html>");
await Reject<HttpRequestException>("{}", HttpStatusCode.ServiceUnavailable);
using (var client = new HttpClient(new ReplyHandler("{}")))
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await YakUpdateChecker.CheckAsync(client, "0.0.60", cancelled.Token);
        throw new Exception("Cancelled requests must not report a version.");
    }
    catch (OperationCanceledException) { passed++; }
}
Console.WriteLine($"Passed {passed} Yak update checks.");

// Optional integration check against the public metadata endpoint; CI stays deterministic.
if (args.Contains("--live"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MetaMAP/0.0.60");
    Console.WriteLine(await YakUpdateChecker.CheckAsync(client, "0.0.59", CancellationToken.None));
}

async Task Check(string installed, string latest, string expected, bool instructions = false)
{
    using var handler = new ReplyHandler($"{{\"name\":\"MetaMAP\",\"version\":\"{latest}\"}}");
    using var client = new HttpClient(handler);
    string result = await YakUpdateChecker.CheckAsync(client, installed, CancellationToken.None);
    if (!result.Contains(expected) || (instructions && (!result.Contains("_PackageManager") || !result.Contains("Restart Rhino"))))
        throw new Exception($"Unexpected result: {result}");
    if (handler.RequestCount != 1)
        throw new Exception("Expected one metadata request and no package download.");
    passed++;
}

async Task Reject<T>(string json, HttpStatusCode status = HttpStatusCode.OK) where T : Exception
{
    using var client = new HttpClient(new ReplyHandler(json, status));
    try
    {
        await YakUpdateChecker.CheckAsync(client, "0.0.60", CancellationToken.None);
        throw new Exception("Invalid response must not report an update or success.");
    }
    catch (T) { passed++; }
}

sealed class ReplyHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://yak.rhino3d.com/packages/metamap")
            throw new Exception("Update checks must only read Yak metadata.");
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
    }
}
