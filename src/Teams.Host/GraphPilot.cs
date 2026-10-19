using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teams.Core;

namespace Teams.Host;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GraphPilotOptions
{
    public const string ResourcePrefix = "Teams Reference Validation";
    public static readonly string[] Scopes = ["Chat.Read", "ChatMessage.Send", "Channel.ReadBasic.All", "ChannelMessage.Read.All", "ChannelMessage.Send"];
    public DateTimeOffset NotBefore { get; set; }
    public ApprovedResource[] Resources { get; set; } = [];
    public void Validate(string tenant, string role, bool local, bool localAuth, string[] actors)
    {
        if (local || localAuth || role != "Api" || !Guid.TryParse(tenant, out _) ||
            actors.Length != 3 || actors.Distinct().Count() != 3 ||
            actors.Any(a => !Guid.TryParse(a, out _)) || NotBefore == default || NotBefore > DateTimeOffset.UtcNow ||
            Resources.Length is < 1 or > 2 || Resources.Select(r => r.Id).Distinct().Count() != Resources.Length ||
            Resources.Select(r => r.Path).Distinct().Count() != Resources.Length ||
            Resources.Any(r => !r.Id.StartsWith("graph-pilot-", StringComparison.Ordinal) ||
                !System.Text.RegularExpressions.Regex.IsMatch(r.Id, "^[A-Za-z0-9_-]{1,80}$") ||
                !r.Title.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("GraphPilot requires Entra/Cosmos API hosting, exactly three approved actors and 1-2 dedicated tagged resources.");
    }
}
public interface IGraphPilotReader
{
    Task<JsonElement> GetAsync(ApprovedResource resource, string path, CancellationToken ct);
}
public sealed class GraphPilotClient(HttpClient http, IGraphReadTokenSource tokens, IGraphCallBudget budget) : IGraphPilotReader
{
    public async Task<JsonElement> GetAsync(ApprovedResource resource, string path, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        ct = deadline.Token;
        var uri = GraphUrls.Validate(path);
        var bucket = uri.AbsolutePath.Contains("/messages", StringComparison.Ordinal)
            ? $"pilot-messages:{GraphUrls.BudgetResource(uri)}" : $"pilot-metadata:{uri.AbsolutePath}";
        for (var attempt = 0; ; attempt++)
        {
            try { await budget.AcquireAsync("data", bucket, ct).WaitAsync(ct); break; }
            catch (AppError ex) when (ex.Status == 429 && attempt < 3) { await Task.Delay(1300, ct); }
        }
        var token = await tokens.AcquireReadAsync(resource.Kind == "channel").WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        LiveTelemetry.GraphAttempts.Add(1, new KeyValuePair<string, object?>("mode", "delegated-read"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode == 429)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            await budget.CooldownAsync(delay, ct);
            throw new AppError(429, "graph_throttled", "Teams reads are throttled. Wait before refreshing.");
        }
        if ((int)response.StatusCode is 401 or 403 or 404)
            throw new AppError((int)response.StatusCode, "graph_access_denied", "Teams did not authorize this resource. Check sign-in, consent and membership.");
        if (!response.IsSuccessStatusCode) throw new AppError(503, "graph_unavailable", "Teams could not supply this view. Try again.");
        return await BoundedJson.ReadAsync(response.Content, 4 * 1024 * 1024, ct);
    }
}
public sealed class GraphPilotStore(IStateStore journal, IGraphPilotReader graph, GraphPilotOptions options,
    IHttpContextAccessor context, string tenant, string[] actors, TimeProvider clock, ILogger<GraphPilotStore> logger) : IStateStore, IDirectGraphReadStore
{
    private readonly Dictionary<string, Task<Conversation>> snapshots = [];
    private Actor Current()
    {
        var principal = context.HttpContext?.User ?? throw new AppError(401, "authentication_required", "Sign in to continue.");
        var id = principal.FindFirstValue("oid") ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        var tid = principal.FindFirstValue("tid") ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        if (principal.Identity?.IsAuthenticated != true || tid != tenant || id is null || !actors.Contains(id))
            throw new AppError(401, "authentication_required", "This validation environment is limited to its three approved identities.");
        return new(tenant, id, principal.FindFirstValue("name") ?? "Test member");
    }
    private ApprovedResource Resource(string id) => options.Resources.SingleOrDefault(r => r.Id == id)
        ?? throw new AppError(404, "access_denied", "Conversation is not in the dedicated Teams validation scope.");
    public async Task<IReadOnlyList<Conversation>> ListAsync(string requestedTenant, CancellationToken ct)
    {
        var actor = Current();
        if (requestedTenant != tenant) throw new AppError(404, "access_denied", "Conversation is not available.");
        List<Conversation> results = [];
        foreach (var resource in options.Resources)
        {
            try
            {
                await VerifyResourceAsync(resource, ct);
                var stored = await EnsureAsync(resource, ct);
                results.Add(stored.Value with { Members = [new(actor.Id, actor.Name, options.NotBefore)] });
            }
            catch (AppError ex) when (ex.Status is 403 or 404)
            {
                logger.LogInformation("GraphPilot catalog resource omitted after delegated denial: {Status}.", ex.Status);
            }
        }
        return results;
    }
    private async Task VerifyResourceAsync(ApprovedResource resource, CancellationToken ct)
    {
        var path = resource.Kind == "chat" ? $"chats/{Uri.EscapeDataString(resource.GraphChatId!)}" :
            $"teams/{Uri.EscapeDataString(resource.GraphTeamId!)}/channels/{Uri.EscapeDataString(resource.GraphChannelId!)}";
        var data = await graph.GetAsync(resource, path, ct);
        var name = resource.Kind == "chat" ? data.GetProperty("topic").GetString() : data.GetProperty("displayName").GetString();
        if (name != resource.Title || !name.StartsWith(GraphPilotOptions.ResourcePrefix, StringComparison.Ordinal))
            throw new AppError(503, "test_scope_mismatch", "The Graph resource does not match its dedicated validation tag.");
    }
    private async Task<Versioned<Conversation>> EnsureAsync(ApprovedResource resource, CancellationToken ct)
    {
        var stored = await journal.GetAsync(tenant, resource.Id, ct);
        if (stored is null)
        {
            await journal.CreateAsync(new(resource.Id, tenant, resource.Title, resource.Kind, resource.Team, [], [], [],
                GraphChatId: resource.GraphChatId, GraphTeamId: resource.GraphTeamId, GraphChannelId: resource.GraphChannelId, Epoch: 1), ct);
            stored = await journal.GetAsync(tenant, resource.Id, ct);
        }
        if (stored is null || !resource.Matches(stored.Value))
            throw new AppError(503, "pilot_mapping_unavailable", "Validation resource mapping changed or is not available.");
        return stored;
    }
    public async Task<Versioned<Conversation>?> GetAsync(string requestedTenant, string id, CancellationToken ct)
    {
        var actor = Current();
        if (requestedTenant != tenant) throw new AppError(404, "access_denied", "Conversation is not available.");
        var resource = Resource(id);
        if (!snapshots.TryGetValue(id, out var snapshot))
        {
            snapshot = SnapshotAsync(resource, actor, ct);
            snapshots[id] = snapshot;
        }
        var value = await snapshot;
        var current = await EnsureAsync(resource, ct);
        return current with { Value = current.Value with { Members = value.Members, Messages = value.Messages,
            Changes = [], CoverageReady = true } };
    }
    private async Task<Conversation> SnapshotAsync(ApprovedResource resource, Actor actor, CancellationToken ct)
    {
        await VerifyResourceAsync(resource, ct);
        var parent = context.HttpContext?.Request.Query["parent"].ToString();
        if (!string.IsNullOrEmpty(parent) && resource.Kind != "channel")
            throw new AppError(400, "invalid_parent", "Only channel threads have replies.");
        var root = await graph.GetAsync(resource, $"{resource.Path}?$top=50" +
            (resource.Kind == "chat" ? "&$orderby=createdDateTime desc" : ""), ct);
        var messages = Parse(root.GetProperty("value"), null).ToList();
        if (!string.IsNullOrEmpty(parent))
        {
            var path = $"{resource.Path}/{Uri.EscapeDataString(parent)}";
            if (!messages.Any(m => m.Id == parent && m.ParentId is null))
                messages.Add(GraphMessages.Parse(await graph.GetAsync(resource, path, ct)));
            var replies = await graph.GetAsync(resource, $"{path}/replies?$top=50", ct);
            messages.AddRange(Parse(replies.GetProperty("value"), parent));
        }
        var cutoff = options.NotBefore > clock.GetUtcNow().AddDays(-7) ? options.NotBefore : clock.GetUtcNow().AddDays(-7);
        return new(resource.Id, tenant, resource.Title, resource.Kind, resource.Team,
            [new(actor.Id, actor.Name, cutoff)], messages.Where(m => m.CreatedAt >= cutoff).ToList(), []);
    }
    private IEnumerable<Message> Parse(JsonElement items, string? parent)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 50) throw new JsonException("GraphPilot page exceeds the approved bound.");
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("messageType", out var type) && type.GetString() is not (null or "message")) continue;
            var message = GraphMessages.Parse(item, parent);
            if (!string.IsNullOrEmpty(message.ActorId) && !actors.Contains(message.ActorId))
                throw new AppError(503, "test_scope_mismatch", "A message author is outside the dedicated test identities.");
            yield return message;
        }
    }
    private static Versioned<Conversation> BodyFree(Versioned<Conversation> value) => value with
    {
        Value = value.Value with { Messages = [], Members = [], Changes = [], Tombstones = [] }
    };
    public Task<bool> SaveAsync(Versioned<Conversation> value, CancellationToken ct) => journal.SaveAsync(BodyFree(value), ct);
    public Task<bool> CreateAsync(Conversation value, CancellationToken ct) => throw new InvalidOperationException("Only dedicated pilot onboarding may create metadata.");
    public Task<bool> CommitAsync(Versioned<Conversation> value, Versioned<SendOperation>? operation, CancellationToken ct) =>
        journal.CommitAsync(BodyFree(value), operation, ct);
    public Task<Versioned<SendOperation>?> GetOperationAsync(string tenantId, string conversation, string id, CancellationToken ct) =>
        journal.GetOperationAsync(tenantId, Resource(conversation).Id, id, ct);
    public Task<bool> CreateOperationAsync(SendOperation value, CancellationToken ct) => journal.CreateOperationAsync(value with { Body = "" }, ct);
    public Task<bool> SaveOperationAsync(Versioned<SendOperation> value, CancellationToken ct) =>
        journal.SaveOperationAsync(value with { Value = value.Value with { Body = "" } }, ct);
    public Task<bool> IsBlockedAsync(string tenantId, string actor, CancellationToken ct) => journal.IsBlockedAsync(tenantId, actor, ct);
    public Task SetBlockedAsync(string tenantId, string actor, bool blocked, CancellationToken ct) =>
        throw new InvalidOperationException("GraphPilot does not expose simulator identity controls.");
    public Task<WriteResult?> GetSimulatorReceiptAsync(string tenantId, string conversation, string operation, CancellationToken ct) =>
        throw new InvalidOperationException("GraphPilot never uses simulator receipts.");
    public Task SaveSimulatorReceiptAsync(string tenantId, string conversation, string operation, WriteResult result, CancellationToken ct) =>
        throw new InvalidOperationException("GraphPilot never stores simulator receipts.");
}
