using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Teams.Core;

namespace Teams.Host;

public sealed class SyntheticUserContexts(IDistributedCache cache, IDataProtectionProvider protection,
    LiveOptions options, IConfiguration configuration, TimeProvider clock, ILogger<SyntheticUserContexts> logger)
{
    private readonly string tenant = configuration["App:TenantId"]!;
    private readonly IDataProtector protector = protection.CreateProtector("live-synthetic-delegation-v1",
        configuration["App:TenantId"]!, configuration["AzureAd:ClientId"]!);
    private string Key(string actor) => $"live-synthetic-context:{tenant}:{actor}";
    public static string? ActorId(ClaimsPrincipal principal) => principal.FindFirstValue("oid") ??
        principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
    private void Validate(ClaimsPrincipal principal)
    {
        var actor = ActorId(principal);
        var tid = principal.FindFirstValue("tid") ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        if (!options.DelegatedSynthetic || principal.Identity?.IsAuthenticated != true || tid != tenant ||
            actor is null || !options.SyntheticActorIds.Contains(actor) || !Guid.TryParse(principal.FindFirstValue("app_sid"), out _))
            throw new AppError(401, "synthetic_context_denied", "A fresh approved test-user session is required.");
    }
    public Task SaveAsync(ClaimsPrincipal principal, DateTimeOffset expiry, CancellationToken ct)
    {
        Validate(principal);
        if (expiry <= clock.GetUtcNow() || expiry > clock.GetUtcNow().AddHours(8).AddMinutes(1))
            throw new AppError(401, "synthetic_context_denied", "The delegated context needs the original bounded session expiry.");
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = expiry }, "synthetic-delegation");
        return cache.SetAsync(Key(ActorId(principal)!), protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiry }, ct);
    }
    public async Task<ClaimsPrincipal> ReadAsync(string actor, CancellationToken ct)
    {
        if (!options.SyntheticActorIds.Contains(actor)) throw new AppError(403, "synthetic_actor_denied", "The actor is outside the test scope.");
        var encrypted = await cache.GetAsync(Key(actor), ct);
        if (encrypted is null) throw new AppError(401, "synthetic_signin_required", "The approved test account must sign in before delegated synchronization.");
        AuthenticationTicket? ticket;
        try { ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(encrypted)); }
        catch (CryptographicException)
        {
            logger.LogWarning("A synthetic delegation context could not be unprotected; fresh sign-in is required.");
            throw new AppError(401, "synthetic_signin_required", "The approved test account must sign in again.");
        }
        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow() ||
            ActorId(ticket.Principal) != actor)
            throw new AppError(401, "synthetic_signin_required", "The retained delegated context expired.");
        Validate(ticket.Principal);
        return ticket.Principal;
    }
}

public sealed class DelegatedSyntheticGraph(IHttpClientFactory clients, IServiceScopeFactory scopes,
    SyntheticUserContexts contexts, IGraphCallBudget budget, LiveOptions options, IConfiguration configuration,
    LiveControlStore controlStore) : IGraphBackground
{
    public IGraphBackground ForResource(string resourcePath)
    {
        var resource = options.Resources.SingleOrDefault(value => value.Path == resourcePath)
            ?? throw new AppError(403, "synthetic_scope_denied", "Only an approved per-resource subscription is supported.");
        return new Scoped(this, resource, options.SyntheticResourceOwners[resource.Id]);
    }
    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => throw new InvalidOperationException("Select a resource context first.");
    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Select a resource context first.");
    public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Select a resource context first.");
    public async Task<string> TokenAsync(string actor, ApprovedResource resource, bool sending, CancellationToken ct)
    {
        var requested = sending ? new[] { resource.Kind == "channel" ? "ChannelMessage.Send" : "ChatMessage.Send" } :
            resource.Kind == "channel" ? new[] { "Channel.ReadBasic.All", "ChannelMessage.Read.All" } : new[] { "Chat.Read" };
        return await ScopesAsync(actor, requested, ct);
    }
    public async Task<string> ScopesAsync(string actor, string[] requested, CancellationToken ct)
    {
        if (requested.Length == 0 || requested.Any(scope => !DevelopmentTeamsScope.Scopes.Contains(scope) && scope != "Chat.Create"))
            throw new AppError(403, "synthetic_scope_denied", "The requested token scope is not part of the existing test consent.");
        var principal = await contexts.ReadAsync(actor, ct);
        using var scope = scopes.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenAcquisition>();
        try
        {
            return await tokens.GetAccessTokenForUserAsync(requested,
                tenantId: configuration["App:TenantId"], user: principal).WaitAsync(ct);
        }
        catch (Exception error) when (error is MicrosoftIdentityWebChallengeUserException or MsalUiRequiredException)
        {
            throw new AppError(401, "synthetic_signin_required", "The delegated synchronization account must sign in again.");
        }
    }
    public async Task<JsonElement> ReadAsAsync(string actor, ApprovedResource resource, string path, CancellationToken ct) =>
        await RequestAsync(actor, resource, HttpMethod.Get, path, null, ct);
    private async Task<JsonElement> RequestAsync(string actor, ApprovedResource resource, HttpMethod method,
        string path, object? body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        ct = deadline.Token;
        var uri = GraphUrls.Validate(path);
        var canonical = GraphUrls.ResourcePath(uri);
        var control = canonical == "subscriptions" || canonical.StartsWith("subscriptions/", StringComparison.Ordinal);
        var root = resource.Path[..resource.Path.LastIndexOf("/messages", StringComparison.Ordinal)];
        if (control && method == HttpMethod.Post && canonical == "subscriptions")
        {
            var definition = JsonSerializer.SerializeToElement(body);
            if (GraphMessages.NormalizeResource(definition.GetProperty("resource").GetString() ?? "") != resource.Path ||
                definition.GetProperty("notificationUrl").GetString() != options.NotificationUrl ||
                definition.GetProperty("lifecycleNotificationUrl").GetString() != options.LifecycleUrl ||
                !definition.GetProperty("includeResourceData").GetBoolean())
                throw new AppError(403, "synthetic_scope_denied", "Subscription creation left the approved resource or callback.");
        }
        else if (control && canonical != "subscriptions")
        {
            var parts = canonical.Split('/');
            var subscription = parts.Length >= 2 ? await controlStore.FindSubscriptionAsync(Uri.UnescapeDataString(parts[1]), ct) : null;
            if (subscription?.Resource != resource.Path)
                throw new AppError(403, "synthetic_scope_denied", "The subscription is not registered for this test resource.");
        }
        if (!control && (method != HttpMethod.Get ||
            !(canonical == root || canonical == $"{root}/members" ||
              canonical == resource.Path || canonical.StartsWith(resource.Path + "/", StringComparison.Ordinal))))
            throw new AppError(403, "synthetic_scope_denied", "The requested Graph path is outside the approved test resource.");
        string token;
        try
        {
            for (;;)
            {
                try { await budget.AcquireAsync(control ? "control" : "data", GraphUrls.BudgetResource(uri), ct); break; }
                catch (AppError error) when (error.Code == "graph_capacity_exceeded")
                { await Task.Delay(TimeSpan.FromMilliseconds(1300), ct); }
            }
            token = await TokenAsync(actor, resource, false, ct);
        }
        catch (OperationCanceledException)
        {
            throw new AppError(429, "graph_capacity_exceeded", "Capacity or token preparation ended before Graph dispatch; work remains pending.");
        }
        catch (AppError error) when (error.Status == 401)
        {
            throw new AppError(401, "graph_not_dispatched", error.Message);
        }
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        if (body is not null) request.Content = JsonContent.Create(body);
        LiveTelemetry.GraphAttempts.Add(1, new KeyValuePair<string, object?>("mode", "delegated-synthetic-sync"));
        using var response = await clients.CreateClient("live-graph").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode == 429)
            await budget.CooldownAsync(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!response.IsSuccessStatusCode)
            throw new AppError((int)response.StatusCode is 401 or 403 or 404 or 429 ? (int)response.StatusCode : 503,
                $"graph_{(int)response.StatusCode}", "Graph rejected scoped delegated synchronization; no checkpoint was advanced.");
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? JsonSerializer.SerializeToElement(new { }) :
            await BoundedJson.ReadAsync(response.Content, 16 * 1024 * 1024, ct,
                allowEmpty: GraphUrls.IsReauthorization(method, uri));
    }
    private sealed class Scoped(DelegatedSyntheticGraph owner, ApprovedResource resource, string actor) : IGraphBackground
    {
        public Task<JsonElement> GetAsync(string path, CancellationToken ct) => owner.RequestAsync(actor, resource, HttpMethod.Get, path, null, ct);
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => owner.RequestAsync(actor, resource, HttpMethod.Post, path, body, ct);
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => owner.RequestAsync(actor, resource, HttpMethod.Patch, path, body, ct);
    }
}

public sealed class DelegatedSyntheticAuthority(DelegatedSyntheticGraph graph, SyntheticUserContexts contexts,
    ILiveStateStore store, LiveOptions options, IConfiguration configuration, TimeProvider clock,
    ILogger<DelegatedSyntheticAuthority> logger) : IEntitlementFeed
{
    private readonly ConcurrentDictionary<string, AuthorityPage> snapshots = new();
    private string Tenant => configuration["App:TenantId"]!;
    public async Task<AuthorityPage> ReadAsync(ApprovedResource resource, string? cursor, CancellationToken ct)
    {
        if (cursor is not null) throw new AppError(400, "synthetic_cursor_denied", "Synthetic authority has one bounded page.");
        if (snapshots.TryGetValue(resource.Id, out var cached) && cached.Entries.All(grant => grant.FreshUntil > clock.GetUtcNow().AddSeconds(60)))
            return cached;
        var owner = options.SyntheticResourceOwners[resource.Id];
        var root = resource.Path[..resource.Path.LastIndexOf("/messages", StringComparison.Ordinal)];
        var metadata = await graph.ReadAsAsync(owner, resource, root, ct);
        var pair = options.DirectChats.SingleOrDefault(value => value.ResourceId == resource.Id);
        var actors = pair is null ? options.SyntheticActorIds : new[] { pair.FirstActorId, pair.SecondActorId };
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var histories = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (resource.Kind == "chat")
        {
            if (metadata.GetProperty("chatType").GetString() != (pair is null ? "group" : "oneOnOne"))
                throw new AppError(503, "synthetic_scope_mismatch", "The approved chat type changed.");
            if (pair is null && metadata.GetProperty("topic").GetString() != resource.Title)
                throw new AppError(503, "synthetic_scope_mismatch", "The dedicated chat title changed.");
            var members = await graph.ReadAsAsync(owner, resource, $"{root}/members", ct);
            if (members.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind != JsonValueKind.Null ||
                members.GetProperty("value").GetArrayLength() != actors.Length)
                throw new AppError(503, "synthetic_scope_mismatch", "The test chat no longer has its exact approved membership.");
            foreach (var member in members.GetProperty("value").EnumerateArray())
            {
                var id = member.GetProperty("userId").GetString() ?? "";
                if (!actors.Contains(id) || names.ContainsKey(id) ||
                    member.TryGetProperty("tenantId", out var tenant) && tenant.GetString() != Tenant)
                    throw new AppError(503, "synthetic_scope_mismatch", "An unexpected member or tenant entered the test chat.");
                names[id] = member.GetProperty("displayName").GetString() ?? "Test member";
                histories[id] = member.TryGetProperty("visibleHistoryStartDateTime", out var history) &&
                    history.ValueKind == JsonValueKind.String && history.TryGetDateTimeOffset(out var from) ? from : DateTimeOffset.MinValue;
            }
        }
        else if (metadata.GetProperty("displayName").GetString() != resource.Title ||
            metadata.GetProperty("membershipType").GetString() != "standard")
            throw new AppError(503, "synthetic_scope_mismatch", "Only the dedicated standard test channel is supported.");
        List<Entitlement> grants = [];
        foreach (var actor in actors)
        {
            var active = false;
            var name = names.GetValueOrDefault(actor, "Test member");
            try
            {
                var principal = await contexts.ReadAsync(actor, ct);
                name = principal.FindFirstValue("name") ?? name;
                if (!await store.IsBlockedAsync(Tenant, actor, ct))
                {
                    await graph.ReadAsAsync(actor, resource, $"{resource.Path}?$top=1", ct);
                    active = true;
                }
            }
            catch (AppError error) when (error.Status is 401 or 403 or 404)
            { logger.LogInformation("A synthetic source grant was denied after delegated validation: {Code}.", error.Code); }
            var checkedAt = clock.GetUtcNow();
            var cutoff = new[] { options.SyntheticHistoryFrom[resource.Id], histories.GetValueOrDefault(actor), checkedAt.AddDays(-7) }.Max();
            var evidence = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{resource.Id}:{actor}:{checkedAt:O}:{active}:{cutoff:O}")));
            grants.Add(new(Tenant, resource.Id, actor, name, cutoff, checkedAt, checkedAt.AddMinutes(2),
                evidence, active, active, options.WriterEpoch));
        }
        var verified = clock.GetUtcNow();
        var direct = pair is null ? null : new DirectChatAuthority(Tenant, resource.Id, resource.GraphChatId!, "oneOnOne",
            actors, verified, verified.AddMinutes(2), $"delegated-membership:{verified:O}");
        var page = new AuthorityPage(Tenant, resource.Id, grants, null, direct);
        snapshots[resource.Id] = page;
        return page;
    }
}
