using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using StackExchange.Redis;
using Teams.Core;

namespace Teams.Host;

public interface IGraphCallBudget
{
    Task AcquireAsync(string lane, string resource, CancellationToken ct);
    Task CooldownAsync(TimeSpan duration, CancellationToken ct);
}
public sealed class RedisGraphCallBudget(IConnectionMultiplexer redis, string tenant, string clientId,
    int intervalMilliseconds, int dataIntervalMilliseconds) : IGraphCallBudget
{
    private string Key => $"graph:{{{clientId}}}";
    private const string Acquire = """
        local t=redis.call('TIME'); local now=t[1]*1000+math.floor(t[2]/1000)
        for i=1,#ARGV,2 do
            if tonumber(redis.call('HGET',KEYS[1],ARGV[i]) or '0')>now then return 0 end
        end
        for i=1,#ARGV,2 do redis.call('HSET',KEYS[1],ARGV[i],now+tonumber(ARGV[i+1])) end
        redis.call('EXPIRE',KEYS[1],86400); return 1
        """;
    public async Task AcquireAsync(string lane, string resource, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fields = new List<RedisValue> { $"cooldown:{tenant}", 0, "global", intervalMilliseconds,
            $"tenant:{tenant}", intervalMilliseconds, $"lane:{tenant}:{lane}",
            lane == "data" ? dataIntervalMilliseconds : intervalMilliseconds, $"resource:{tenant}:{resource}", 1250 };
        var result = await redis.GetDatabase().ScriptEvaluateAsync(Acquire, [Key], fields.ToArray());
        if ((long)result != 1) throw new AppError(429, "graph_capacity_exceeded", "Shared Graph capacity is unavailable; work remains pending.");
    }
    public async Task CooldownAsync(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await redis.GetDatabase().ScriptEvaluateAsync("""
            local t=redis.call('TIME'); local untilAt=t[1]*1000+math.floor(t[2]/1000)+tonumber(ARGV[1])
            local previous=tonumber(redis.call('HGET',KEYS[1],ARGV[2]) or '0')
            redis.call('HSET',KEYS[1],ARGV[2],math.max(previous,untilAt)); redis.call('EXPIRE',KEYS[1],86400)
            """, [Key], [(long)duration.TotalMilliseconds, $"cooldown:{tenant}"]);
    }
}
public static class GraphUrls
{
    public static string SubscriptionResource(string canonicalPath) =>
        canonicalPath.Replace("%3A", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("%40", "@", StringComparison.OrdinalIgnoreCase);
    public static bool IsReauthorization(HttpMethod method, Uri uri) =>
        method == HttpMethod.Post && ResourcePath(uri).Split('/')
            is ["subscriptions", _, "reauthorize"];
    public static Uri Validate(string value, string? expectedPath = null)
    {
        var uri = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute : new Uri(new Uri("https://graph.microsoft.com/v1.0/"), value);
        if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
            throw new AppError(400, "graph_url_invalid", "Graph URL leaves the configured authority/version.");
        if (expectedPath is not null && ResourcePath(uri) != ResourcePath(Validate(expectedPath)))
            throw new AppError(400, "graph_cursor_invalid", "Graph continuation leaves the approved resource.");
        return uri;
    }
    public static string ResourcePath(Uri uri) =>
        GraphMessages.NormalizeResource(Validate(uri.AbsoluteUri).AbsolutePath["/v1.0/".Length..]);
    public static string BudgetResource(Uri uri)
    {
        var path = ResourcePath(uri);
        var segments = path.Split('/');
        if (segments is ["chats", _, ..]) return "v1.0/" + string.Join('/', segments.Take(2));
        if (segments is ["teams", _, "channels", _, ..]) return "v1.0/" + string.Join('/', segments.Take(4));
        return "/v1.0/" + path;
    }
}
public sealed class GraphBackground(HttpClient http, TokenCredential credential, IGraphCallBudget budget) : IGraphBackground
{
    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => RequestAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => RequestAsync(HttpMethod.Post, path, body, ct);
    public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => RequestAsync(HttpMethod.Patch, path, body, ct);
    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var measurement = LiveTelemetry.Measure("graph-background");
        var callerToken = ct;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        ct = deadline.Token;
        var uri = GraphUrls.Validate(path);
        var canonical = GraphUrls.ResourcePath(uri);
        var control = canonical == "subscriptions" || canonical.StartsWith("subscriptions/", StringComparison.Ordinal);
        if (!control && method != HttpMethod.Get) throw new InvalidOperationException("Background Graph clients cannot send user messages.");
        string token;
        try
        {
            for (;;)
            {
                try { await budget.AcquireAsync(control ? "control" : "data", GraphUrls.BudgetResource(uri), ct).WaitAsync(ct); break; }
                catch (AppError error) when (error.Code == "graph_capacity_exceeded")
                { await Task.Delay(TimeSpan.FromMilliseconds(1300), ct); }
            }
            token = (await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct)
                .AsTask().WaitAsync(ct)).Token;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        { throw; }
        catch (OperationCanceledException)
        {
            throw new AppError(429, "graph_capacity_exceeded", "Capacity or token preparation ended before Graph dispatch; work remains pending.");
        }
        catch (Azure.Identity.AuthenticationFailedException)
        {
            throw new AppError(503, "graph_not_dispatched", "Workload authentication was unavailable before Graph dispatch; work remains pending.");
        }
        catch (RedisException)
        {
            throw new AppError(503, "graph_not_dispatched", "Shared permit storage was unavailable before Graph dispatch; work remains pending.");
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        if (body is not null) request.Content = JsonContent.Create(body);
        LiveTelemetry.GraphAttempts.Add(1, new KeyValuePair<string, object?>("mode", "application"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);
            await budget.CooldownAsync(delay, ct);
        }
        if (!response.IsSuccessStatusCode)
            throw new AppError(response.StatusCode == HttpStatusCode.TooManyRequests ? 429 : 503,
                $"graph_{(int)response.StatusCode}", "Graph rejected background work; its checkpoint has not advanced.");
        if (response.StatusCode == HttpStatusCode.NoContent) return JsonSerializer.SerializeToElement(new { });
        return await BoundedJson.ReadAsync(response.Content, 16 * 1024 * 1024, ct,
            allowEmpty: GraphUrls.IsReauthorization(method, uri));
    }
}
