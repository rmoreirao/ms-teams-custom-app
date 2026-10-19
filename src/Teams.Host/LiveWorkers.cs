using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Teams.Core;

namespace Teams.Host;

public sealed record AuthorityPage(string TenantId, string ResourceId, IReadOnlyList<Entitlement> Entries, string? Cursor);
public interface IEntitlementFeed
{
    Task<AuthorityPage> ReadAsync(ApprovedResource resource, string? cursor, CancellationToken ct);
}
public sealed class EntitlementFeed(HttpClient http, TokenCredential credential, LiveOptions options, string tenant) : IEntitlementFeed
{
    public async Task<AuthorityPage> ReadAsync(ApprovedResource resource, string? cursor, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        ct = deadline.Token;
        var origin = new Uri(options.EntitlementAuthority.TrimEnd('/') + "/");
        var uri = new Uri(origin, $"resources/{Uri.EscapeDataString(resource.Id)}/entitlements" +
            (cursor is null ? "" : $"?cursor={Uri.EscapeDataString(cursor)}"));
        var token = await credential.GetTokenAsync(new TokenRequestContext([options.EntitlementScope]), ct).AsTask().WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new("Bearer", token.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new AppError(503, "authority_unavailable", "Authoritative policy refresh failed; leases will not be extended.");
        var page = (await BoundedJson.ReadAsync(response.Content, 1024 * 1024, ct)).Deserialize<AuthorityPage>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Policy response is empty.");
        if (page.TenantId != tenant || page.ResourceId != resource.Id || page.Entries.Count > 100 ||
            page.Cursor?.Length > 4096 || page.Entries.Select(e => e.ActorId).Distinct().Count() != page.Entries.Count ||
            page.Entries.Any(e => e.TenantId != tenant || e.ConversationId != resource.Id || e.Epoch != options.WriterEpoch))
            throw new JsonException("Policy response violates its resource/identity contract.");
        return page;
    }
}
public sealed class ResourceHydrator(IGraphBackground graph, ILiveStateStore store, LiveControlStore control, TimeProvider clock)
{
    public async Task StepAsync(string tenant, ApprovedResource resource, RepairState pending, CancellationToken ct)
    {
        var state = await control.RepairAsync(resource.Id, pending.ParentId, ct);
        if (state is null || !state.Value.Pending) return;
        var value = state.Value;
        if (value.Generation is null)
        {
            var metadata = await store.GetAsync(tenant, resource.Id, ct)
                ?? throw new AppError(503, "resource_preparing", "Repair awaits resource onboarding.");
            value = value with { Generation = metadata.Value.RepairGeneration };
            if (!await control.SaveRepairAsync(state with { Value = value }, ct))
                throw new AppError(409, "repair_conflict", "Repair generation changed concurrently.");
            state = await control.RepairAsync(resource.Id, pending.ParentId, ct)
                ?? throw new InvalidOperationException("Repair generation checkpoint is missing.");
        }
        if (value.Verifying)
        {
            var scan = await store.ScanAsync(tenant, resource.Id, value.ParentId, value.ScanCursor, ct);
            foreach (var message in scan.Items.Where(m => !m.Deleted))
            {
                var path = message.ParentId is null ? $"{resource.Path}/{Uri.EscapeDataString(message.Id)}" :
                    $"{resource.Path}/{Uri.EscapeDataString(message.ParentId)}/replies/{Uri.EscapeDataString(message.Id)}";
                try { await store.ApplyVerifiedAsync(tenant, resource.Id, GraphMessages.Parse(await graph.GetAsync(path, ct), message.ParentId),
                    message.SourceVersion, ct); }
                catch (AppError ex) when (ex.Code == "graph_404")
                {
                    await store.ApplyVerifiedAsync(tenant, resource.Id, message with { Deleted = true, Body = "",
                        ModifiedAt = clock.GetUtcNow(), SourceVersion = $"verified-delete:{Guid.NewGuid():N}" }, message.SourceVersion, ct);
                }
            }
            if (!await control.SaveRepairAsync(state with { Value = value with { ScanCursor = scan.Cursor,
                Pending = scan.Cursor is not null, UpdatedAt = clock.GetUtcNow() } }, ct))
                throw new AppError(409, "repair_conflict", "Verification checkpoint changed concurrently.");
            return;
        }
        var expected = value.ParentId is null ? resource.Path : $"{resource.Path}/{Uri.EscapeDataString(value.ParentId)}/replies";
        var pathToRead = value.NextLink ?? value.Path;
        GraphUrls.Validate(pathToRead, expected);
        var page = await graph.GetAsync(pathToRead, ct);
        var items = page.GetProperty("value");
        if (items.GetArrayLength() > 50) throw new JsonException("Graph hydration page exceeds the requested bound.");
        foreach (var item in items.EnumerateArray())
        {
            var message = GraphMessages.Parse(item, value.ParentId);
            await store.ApplyAsync(tenant, resource.Id, message, ct);
            if (resource.Kind == "channel" && value.ParentId is null)
            {
                if (item.TryGetProperty("replies", out var replies))
                    foreach (var reply in replies.EnumerateArray())
                        await store.ApplyAsync(tenant, resource.Id, GraphMessages.Parse(reply, message.Id), ct);
                if (item.TryGetProperty("replies@odata.nextLink", out var nextReplies))
                    await control.EnqueueRepairAsync(resource, nextReplies.GetString(), message.Id, ct);
                else if (!item.TryGetProperty("replies", out _))
                    await control.EnqueueRepairAsync(resource, $"{resource.Path}/{Uri.EscapeDataString(message.Id)}/replies?$top=50", message.Id, ct);
            }
        }
        var next = page.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
        if (next is not null) GraphUrls.Validate(next, expected);
        if (resource.Kind == "chat" && items.EnumerateArray().Any(m =>
            m.GetProperty("createdDateTime").GetDateTimeOffset() < clock.GetUtcNow().AddDays(-7))) next = null;
        if (!await control.SaveRepairAsync(state with { Value = value with { NextLink = next,
            Verifying = next is null, UpdatedAt = clock.GetUtcNow() } }, ct))
            throw new AppError(409, "repair_conflict", "Hydration checkpoint changed concurrently.");
    }
}
public sealed class SubscriptionController(IGraphBackground graph, LiveControlStore control,
    LiveOptions options, TimeProvider clock)
{
    private readonly string owner = Guid.NewGuid().ToString("N");
    public async Task StepAsync(SubscriptionScope scope, CancellationToken ct)
    {
        var stored = await control.SubscriptionAsync(scope.Id, ct);
        var value = stored?.Value ?? new(scope.Id, scope.Path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            null, DateTimeOffset.MinValue, options.NotificationCertificateId, false, "", DateTimeOffset.MinValue);
        if (value.Resource != scope.Path) throw new InvalidOperationException("Subscription scope changed; explicit rotation required.");
        if (value.LeaseUntil > clock.GetUtcNow() && value.Owner != owner) return;
        value = value with { Owner = owner, LeaseUntil = clock.GetUtcNow().AddMinutes(2) };
        if (!await control.SaveSubscriptionAsync(value, stored?.Version, ct)) return;
        stored = await control.SubscriptionAsync(scope.Id, ct) ?? throw new InvalidOperationException("Subscription claim missing.");
        var lifecycle = await control.LifecycleAsync(scope.Id, ct);
        if (lifecycle is { Value.Handled: false })
        {
            if (lifecycle.Value.Action == "subscriptionRemoved")
            {
                value = value with { GraphId = null, CreateAttempted = false, Expires = DateTimeOffset.MinValue,
                    ClientState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
                if (!await control.SaveSubscriptionAsync(value, stored.Version, ct)) return;
                stored = await control.SubscriptionAsync(scope.Id, ct) ?? throw new InvalidOperationException("Subscription reset missing.");
            }
            else if (lifecycle.Value.Action == "reauthorizationRequired" && value.GraphId is not null)
                await graph.PostAsync($"subscriptions/{Uri.EscapeDataString(value.GraphId)}/reauthorize", new { }, ct);
            if (!await control.SaveLifecycleAsync(scope.Id, lifecycle.Value with { Handled = true }, lifecycle.Version, ct))
                throw new AppError(409, "lifecycle_conflict", "Lifecycle handling changed concurrently.");
        }
        if (value.GraphId is not null && value.Expires > clock.GetUtcNow().AddMinutes(15) &&
            value.CertificateId == options.NotificationCertificateId)
        {
            await control.RegisterLookupAsync(value, ct);
            return;
        }
        JsonElement response;
        if (value.GraphId is null && value.CreateAttempted)
        {
            string? path = "subscriptions";
            JsonElement? found = null;
            for (var page = 0; page < 20 && path is not null; page++)
            {
                GraphUrls.Validate(path, "subscriptions");
                var list = await graph.GetAsync(path, ct);
                foreach (var item in list.GetProperty("value").EnumerateArray())
                    if (item.GetProperty("resource").GetString()?.TrimStart('/') == scope.Path &&
                        item.TryGetProperty("clientState", out var state) && state.GetString() == value.ClientState)
                    {
                        if (found is not null) throw new InvalidOperationException("Duplicate exact-correlated subscriptions require explicit cleanup.");
                        found = item.Clone();
                    }
                path = list.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
            if (path is not null || found is null)
                throw new AppError(503, "subscription_creation_unknown", "Cannot prove the original subscription outcome; no replacement POST was issued.");
            response = found.Value;
        }
        else if (value.GraphId is null)
        {
            value = value with { CreateAttempted = true };
            if (!await control.SaveSubscriptionAsync(value, stored.Version, ct)) return;
            stored = await control.SubscriptionAsync(scope.Id, ct) ?? throw new InvalidOperationException("Subscription intent missing.");
            try
            {
                response = await graph.PostAsync("subscriptions", new
                {
                    resource = scope.Path, changeType = "created,updated,deleted", notificationUrl = options.NotificationUrl,
                    lifecycleNotificationUrl = options.LifecycleUrl, includeResourceData = true,
                    encryptionCertificate = options.NotificationCertificate, encryptionCertificateId = options.NotificationCertificateId,
                    clientState = value.ClientState, expirationDateTime = clock.GetUtcNow().AddMinutes(50)
                }, ct);
            }
            catch (AppError ex) when (ex.Code == "graph_capacity_exceeded" ||
                ex.Code.StartsWith("graph_4", StringComparison.Ordinal) && ex.Code != "graph_408")
            {
                if (!await control.SaveSubscriptionAsync(value with { CreateAttempted = false }, stored.Version, ct))
                    throw new AppError(409, "subscription_conflict", "Definitive creation rejection could not be checkpointed.");
                throw;
            }
        }
        else
        {
            try
            {
                response = await graph.PatchAsync($"subscriptions/{Uri.EscapeDataString(value.GraphId)}", new
                {
                    expirationDateTime = clock.GetUtcNow().AddMinutes(50), encryptionCertificate = options.NotificationCertificate,
                    encryptionCertificateId = options.NotificationCertificateId
                }, ct);
            }
            catch (AppError ex) when (ex.Code == "graph_404")
            {
                if (!await control.SaveSubscriptionAsync(value with { GraphId = null, CreateAttempted = false,
                    Expires = DateTimeOffset.MinValue }, stored.Version, ct)) throw;
                return;
            }
        }
        var id = response.GetProperty("id").GetString();
        var expires = response.GetProperty("expirationDateTime").GetDateTimeOffset();
        if (string.IsNullOrWhiteSpace(id) || expires <= clock.GetUtcNow())
            throw new JsonException("Subscription confirmation evidence is incomplete.");
        value = value with { GraphId = id, Expires = expires, CertificateId = options.NotificationCertificateId };
        if (!await control.SaveSubscriptionAsync(value, stored.Version, ct))
            throw new AppError(409, "subscription_conflict", "Subscription confirmation checkpoint failed; recover by exact correlation.");
        await control.RegisterLookupAsync(value, ct);
    }
}
public sealed class LiveControlWorker(string tenant, LiveOptions options, ILiveStateStore store,
    LiveControlStore control, SubscriptionController subscriptions, ResourceHydrator hydrator,
    IEntitlementFeed authority, IRealtime realtime, TimeProvider clock, ILogger<LiveControlWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, string> authorityCursors = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled)
        {
            logger.LogInformation("Graph control and policy ingestion are disabled pending synchronization approval.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var scope in options.SubscriptionScopes)
                await GuardAsync(() => subscriptions.StepAsync(scope, stoppingToken), "subscription", stoppingToken);
            await Parallel.ForEachAsync(options.Resources, new ParallelOptions
                { MaxDegreeOfParallelism = options.QueueConcurrency, CancellationToken = stoppingToken }, async (resource, resourceToken) =>
            {
                await GuardAsync(async () =>
                {
                    var metadata = await store.GetAsync(tenant, resource.Id, stoppingToken);
                    if (metadata is null)
                    {
                        await store.CreateAsync(new(resource.Id, tenant, resource.Title, resource.Kind, resource.Team, [], [], [],
                            CoverageReady: false, GraphChatId: resource.GraphChatId, GraphTeamId: resource.GraphTeamId,
                            GraphChannelId: resource.GraphChannelId, Epoch: options.WriterEpoch), stoppingToken);
                        metadata = await store.GetAsync(tenant, resource.Id, stoppingToken);
                    }
                    if (metadata!.Value.Epoch != options.WriterEpoch) throw new InvalidOperationException("Writer epoch requires an explicit fenced migration.");
                    if (!resource.Matches(metadata.Value)) throw new InvalidOperationException("Resource mapping requires an explicit fenced migration.");
                    if (await SubscriptionExpiryAsync(resource, stoppingToken) <= clock.GetUtcNow())
                    {
                        await store.SetCoverageAsync(new(tenant, resource.Id, false, null, null, "subscription_unavailable"), stoppingToken);
                        throw new AppError(503, "subscription_unavailable", "Hydration requires an established subscription.");
                    }
                    var grants = await authority.ReadAsync(resource, authorityCursors.GetValueOrDefault(resource.Id), stoppingToken);
                    foreach (var grant in grants.Entries)
                    {
                        await store.SetEntitlementAsync(grant, stoppingToken);
                        if (!grant.Active) await realtime.RevokeAsync(tenant, grant.ActorId, stoppingToken);
                    }
                    if (grants.Cursor is null) authorityCursors.TryRemove(resource.Id, out _);
                    else authorityCursors[resource.Id] = grants.Cursor;
                    var root = await control.RepairAsync(resource.Id, null, stoppingToken);
                    if (root is null || !root.Value.Pending && (root.Value.UpdatedAt < clock.GetUtcNow().AddMinutes(-4) ||
                        root.Value.Generation != metadata.Value.RepairGeneration))
                        await control.EnqueueRepairAsync(resource, null, null, stoppingToken);
                }, "authority/onboarding", resourceToken);
            });
            foreach (var job in await control.PendingRepairsAsync(stoppingToken))
            {
                var resource = options.Resources.SingleOrDefault(r => r.Id == job.ResourceId);
                if (resource is null) { logger.LogError("Repair resource is no longer approved; work remains quarantined."); continue; }
                await GuardAsync(async () =>
                {
                    await hydrator.StepAsync(tenant, resource, job, stoppingToken);
                    var expires = await SubscriptionExpiryAsync(resource, stoppingToken);
                    if (!await control.HasPendingRepairAsync(resource.Id, stoppingToken) && expires > clock.GetUtcNow())
                    {
                        var root = await control.RepairAsync(resource.Id, null, stoppingToken);
                        if (root?.Value.Generation is { } generation)
                            await store.SetCoverageAsync(new(tenant, resource.Id, true, clock.GetUtcNow(), null, null,
                                expires < clock.GetUtcNow().AddMinutes(5) ? expires : clock.GetUtcNow().AddMinutes(5), generation), stoppingToken);
                    }
                }, "repair", stoppingToken);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task<DateTimeOffset> SubscriptionExpiryAsync(ApprovedResource resource, CancellationToken ct)
    {
        var expires = DateTimeOffset.MinValue;
        foreach (var scope in options.SubscriptionScopes.Where(s => s.Covers(resource)))
        {
            var state = await control.SubscriptionAsync(scope.Id, ct);
            if (state is not null && state.Value.GraphId is not null && state.Value.Expires > expires) expires = state.Value.Expires;
        }
        return expires;
    }
    private async Task GuardAsync(Func<Task> action, string stage, CancellationToken ct)
    {
        try { await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AppError ex) when (ex.Status == 429)
        {
            LiveTelemetry.GraphDeferred.Add(1);
            logger.LogInformation("Live control {Stage} deferred by shared Graph capacity: {Code}; work remains pending.", stage, ex.Code);
        }
        catch (Exception ex)
        {
            LiveTelemetry.Failure("control");
            logger.LogError("Live control {Stage} failed: {ErrorType}; checkpoint retained.", stage, ex.GetType().Name);
        }
    }
}
