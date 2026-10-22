using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Teams.Core;

namespace Teams.Host;

public sealed record AuthorityPage(string TenantId, string ResourceId, IReadOnlyList<Entitlement> Entries, string? Cursor,
    DirectChatAuthority? DirectChat = null);
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
        var pair = options.DirectChats.SingleOrDefault(pair => pair.ResourceId == resource.Id);
        if (pair is not null && (page.DirectChat is not { } direct || direct.TenantId != tenant ||
            direct.ResourceId != resource.Id || direct.GraphChatId != resource.GraphChatId || direct.ChatType != "oneOnOne" ||
            direct.MemberIds.Count != 2 || !direct.MemberIds.Contains(pair.FirstActorId) || !direct.MemberIds.Contains(pair.SecondActorId)))
            throw new AppError(503, "direct_chat_authority_required", "The policy source has not verified this resource as the approved exact one-to-one pair.");
        if (pair is null && page.DirectChat is not null)
            throw new JsonException("Unapproved direct-chat authority was returned.");
        return page;
    }
}
public sealed class ResourceHydrator(IGraphBackground graph, ILiveStateStore store, LiveControlStore control, TimeProvider clock,
    LiveOptions? options = null)
{
    public async Task StepAsync(string tenant, ApprovedResource resource, RepairState pending, CancellationToken ct)
    {
        var scopedGraph = graph.ForResource(resource.Path);
        var state = await control.RepairAsync(resource.Id, pending.ParentId, ct);
        if (state is null || !state.Value.IsRunnable(clock.GetUtcNow())) return;
        var value = state.Value;
        var current = await store.GetAsync(tenant, resource.Id, ct)
            ?? throw new AppError(503, "resource_preparing", "Repair awaits resource onboarding.");
        if (value.Generation is not null && value.Generation != current.Value.RepairGeneration)
        {
            if (!await control.RestartRepairAsync(resource, state, current.Value.RepairGeneration, "repair_generation_superseded", ct))
                throw new AppError(409, "repair_conflict", "Repair generation changed concurrently.");
            return;
        }
        if (value.Generation is null)
        {
            value = value with { Generation = current.Value.RepairGeneration };
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
                try { await store.ApplyVerifiedAsync(tenant, resource.Id, GraphMessages.Parse(await scopedGraph.GetAsync(path, ct), message.ParentId),
                    message.SourceVersion, ct); }
                catch (AppError ex) when (ex.Code == "graph_404")
                {
                    await store.ApplyVerifiedAsync(tenant, resource.Id, message with { Deleted = true, Body = "",
                        ModifiedAt = clock.GetUtcNow(), SourceVersion = $"verified-delete:{Guid.NewGuid():N}" }, message.SourceVersion, ct);
                }
            }
            if (!await control.SaveRepairAsync(state with { Value = (value with { ScanCursor = scan.Cursor,
                Pending = scan.Cursor is not null }).Progress(clock.GetUtcNow()) }, ct))
                throw new AppError(409, "repair_conflict", "Verification checkpoint changed concurrently.");
            return;
        }
        var expected = value.ParentId is null ? resource.Path : $"{resource.Path}/{Uri.EscapeDataString(value.ParentId)}/replies";
        var pathToRead = value.NextLink ?? value.Path;
        GraphUrls.Validate(pathToRead, expected);
        var page = await scopedGraph.GetAsync(pathToRead, ct);
        var items = page.GetProperty("value");
        if (items.GetArrayLength() > 50) throw new JsonException("Graph hydration page exceeds the requested bound.");
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("messageType", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() is not (null or "message")) continue;
            var message = GraphMessages.Parse(item, value.ParentId);
            if (options?.DelegatedSynthetic == true)
            {
                if (message.CreatedAt < options.SyntheticHistoryFrom[resource.Id]) continue;
                if (message.ActorId.Length > 0 && !options.SyntheticActorIds.Contains(message.ActorId))
                    throw new AppError(403, "synthetic_scope_mismatch", "The test message author left the approved synthetic scope.");
            }
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
        if (!await control.SaveRepairAsync(state with { Value = (value with { NextLink = next,
            Verifying = next is null }).Progress(clock.GetUtcNow()) }, ct))
            throw new AppError(409, "repair_conflict", "Hydration checkpoint changed concurrently.");
    }
}
public sealed class SubscriptionController(IGraphBackground graph, LiveControlStore control,
    LiveOptions options, TimeProvider clock)
{
    private readonly string owner = Guid.NewGuid().ToString("N");
    public async Task StepAsync(SubscriptionScope scope, CancellationToken ct)
    {
        var scopedGraph = graph.ForResource(scope.Path);
        async Task<JsonElement> RenewAsync(string id)
        {
            var result = await scopedGraph.PatchAsync($"subscriptions/{Uri.EscapeDataString(id)}", new
            {
                expirationDateTime = clock.GetUtcNow().AddMinutes(50), encryptionCertificate = options.NotificationCertificate,
                encryptionCertificateId = options.NotificationCertificateId
            }, ct);
            if (result.GetProperty("id").GetString() != id ||
                result.GetProperty("expirationDateTime").GetDateTimeOffset() <= clock.GetUtcNow())
                throw new JsonException("Subscription renewal returned mismatched or expired confirmation.");
            return result;
        }
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
            var removed = lifecycle.Value.Action == "subscriptionRemoved";
            if (lifecycle.Value.Action == "reauthorizationRequired" && value.GraphId is not null)
            {
                try
                {
                    var renewed = await RenewAsync(value.GraphId);
                    value = value with { Expires = renewed.GetProperty("expirationDateTime").GetDateTimeOffset(),
                        CertificateId = options.NotificationCertificateId };
                    if (!await control.SaveSubscriptionAsync(value, stored.Version, ct))
                        throw new AppError(409, "subscription_conflict", "Subscription reauthorization confirmation changed concurrently.");
                    stored = await control.SubscriptionAsync(scope.Id, ct) ?? throw new InvalidOperationException("Subscription renewal checkpoint is missing.");
                }
                catch (AppError ex) when (ex.Code == "graph_404") { removed = true; }
            }
            if (removed)
            {
                value = value with { GraphId = null, CreateAttempted = false, Expires = DateTimeOffset.MinValue,
                    ClientState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
                if (!await control.SaveSubscriptionAsync(value, stored.Version, ct)) return;
                stored = await control.SubscriptionAsync(scope.Id, ct) ?? throw new InvalidOperationException("Subscription reset missing.");
            }
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
                var list = await scopedGraph.GetAsync(path, ct);
                foreach (var item in list.GetProperty("value").EnumerateArray())
                    if (GraphMessages.NormalizeResource(item.GetProperty("resource").GetString() ?? "") == scope.Path &&
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
                response = await scopedGraph.PostAsync("subscriptions", new
                {
                    resource = GraphUrls.SubscriptionResource(scope.Path), changeType = "created,updated,deleted", notificationUrl = options.NotificationUrl,
                    lifecycleNotificationUrl = options.LifecycleUrl, includeResourceData = true,
                    encryptionCertificate = options.NotificationCertificate, encryptionCertificateId = options.NotificationCertificateId,
                    clientState = value.ClientState, expirationDateTime = clock.GetUtcNow().AddMinutes(50)
                }, ct);
            }
            catch (AppError ex) when (ex.Code is "graph_capacity_exceeded" or "graph_not_dispatched" or "graph_scope_denied" or "graph_url_invalid" ||
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
                response = await RenewAsync(value.GraphId);
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
    private readonly ConcurrentDictionary<string, byte> subscriptionGaps = new();
    private readonly ConcurrentDictionary<string, (string Code, DateTimeOffset At, int Suppressed)> loggedFailures = new();
    private DateTimeOffset repairHealthAt = DateTimeOffset.MinValue;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled)
        {
            logger.LogInformation("Graph control and policy ingestion are disabled pending synchronization approval.");
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var loops = new[] { RunAsync(RefreshAsync, cancellation.Token), RunAsync(RepairAsync, cancellation.Token) };
        await Task.WhenAny(loops);
        await cancellation.CancelAsync();
        await Task.WhenAll(loops);
    }
    public async Task StepAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        await RepairAsync(ct);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await action(ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
    private async Task RefreshAsync(CancellationToken ct)
    {
        foreach (var scope in options.SubscriptionScopes)
            await GuardAsync(() => subscriptions.StepAsync(scope, ct), "subscription", ct, scope.Id);
        await Parallel.ForEachAsync(options.Resources, new ParallelOptions
            { MaxDegreeOfParallelism = options.QueueConcurrency, CancellationToken = ct }, async (resource, resourceToken) =>
        {
            await GuardAsync(async () =>
            {
                var metadata = await store.GetAsync(tenant, resource.Id, resourceToken);
                if (metadata is null)
                {
                    await store.CreateAsync(new(resource.Id, tenant, resource.Title, resource.Kind, resource.Team, [], [], [],
                        CoverageReady: false, GraphChatId: resource.GraphChatId, GraphTeamId: resource.GraphTeamId,
                        GraphChannelId: resource.GraphChannelId, Epoch: options.WriterEpoch), resourceToken);
                    metadata = await store.GetAsync(tenant, resource.Id, resourceToken);
                }
                if (metadata!.Value.Epoch != options.WriterEpoch) throw new InvalidOperationException("Writer epoch requires an explicit fenced migration.");
                if (!resource.Matches(metadata.Value)) throw new InvalidOperationException("Resource mapping requires an explicit fenced migration.");
                var expires = await SubscriptionExpiryAsync(resource, resourceToken);
                if (expires <= clock.GetUtcNow())
                {
                    if (subscriptionGaps.TryAdd(resource.Id, 0))
                    {
                        var invalidated = false;
                        try
                        {
                            await store.SetCoverageAsync(new(tenant, resource.Id, false, null, null, "subscription_unavailable"), resourceToken);
                            invalidated = true;
                        }
                        finally { if (!invalidated) subscriptionGaps.TryRemove(resource.Id, out _); }
                    }
                    throw new AppError(503, "subscription_unavailable", "Hydration requires an established subscription.");
                }
                subscriptionGaps.TryRemove(resource.Id, out _);
                var grants = await authority.ReadAsync(resource, authorityCursors.GetValueOrDefault(resource.Id), resourceToken);
                if (grants.DirectChat is not null)
                    await store.SetDirectChatAuthorityAsync(grants.DirectChat, resourceToken);
                foreach (var grant in grants.Entries)
                {
                    await store.SetEntitlementAsync(grant, resourceToken);
                    if (!grant.Active) await realtime.RevokeAsync(tenant, grant.ActorId, resourceToken);
                }
                if (grants.Cursor is null) authorityCursors.TryRemove(resource.Id, out _);
                else authorityCursors[resource.Id] = grants.Cursor;
                var root = await control.RepairAsync(resource.Id, null, resourceToken);
                var coverageUntil = metadata.Value.CoverageUntil;
                if (!metadata.Value.CoverageReady && root?.Value.Generation == metadata.Value.RepairGeneration)
                    coverageUntil = await CompleteCoverageAsync(resource, root.Value, expires, resourceToken) ?? coverageUntil;
                if ((root is null || !root.Value.Pending && (root.Value.Generation != metadata.Value.RepairGeneration ||
                    coverageUntil is null || coverageUntil < clock.GetUtcNow().AddMinutes(4))) &&
                    !await control.HasPendingRepairAsync(resource.Id, resourceToken))
                    await control.EnqueueRepairAsync(resource, null, null, resourceToken);
            }, "authority/onboarding", resourceToken, resource.Id);
        });
    }
    private async Task RepairAsync(CancellationToken ct)
    {
        if (clock.GetUtcNow() - repairHealthAt >= TimeSpan.FromMinutes(1))
        {
            repairHealthAt = clock.GetUtcNow();
            await Parallel.ForEachAsync(options.Resources, new ParallelOptions
                { MaxDegreeOfParallelism = options.QueueConcurrency, CancellationToken = ct }, async (resource, token) =>
                await GuardAsync(async () =>
                {
                    var unresolved = await control.UnresolvedRepairsAsync(resource.Id, token);
                    var stalled = unresolved.Count(job => job.Pending &&
                        (job.Failure?.Blocked == true ||
                         clock.GetUtcNow() - (job.LastProgressAt ?? job.UpdatedAt) >= TimeSpan.FromMinutes(
                             job.Failure?.Code is "graph_capacity_exceeded" or "graph_429" ? 5 : 2)));
                    LiveTelemetry.RepairStalled.Record(stalled);
                }, "repair-health", token, resource.Id));
        }
        var jobs = await control.PendingRepairsAsync(ct);
        await Parallel.ForEachAsync(jobs.GroupBy(job => job.ResourceId), new ParallelOptions
            { MaxDegreeOfParallelism = options.QueueConcurrency, CancellationToken = ct }, async (resourceJobs, resourceToken) =>
        {
            var resource = options.Resources.SingleOrDefault(r => r.Id == resourceJobs.Key);
            if (resource is null) { logger.LogError("Repair resource is no longer approved; work remains quarantined."); return; }
            var steps = 0;
            foreach (var job in resourceJobs)
            {
                if (steps >= 20) break;
                Versioned<RepairState>? attempted = null;
                try
                {
                    while (steps < 20)
                    {
                        attempted = await control.RepairAsync(resource.Id, job.ParentId, resourceToken);
                        if (attempted?.Value.IsRunnable(clock.GetUtcNow()) != true) break;
                        steps++;
                        await hydrator.StepAsync(tenant, resource, job, resourceToken);
                        if ((await control.RepairAsync(resource.Id, job.ParentId, resourceToken))?.Value.IsRunnable(clock.GetUtcNow()) != true) break;
                    }
                    var root = await control.RepairAsync(resource.Id, null, resourceToken);
                    if (root is not null)
                        await CompleteCoverageAsync(resource, root.Value, await SubscriptionExpiryAsync(resource, resourceToken), resourceToken);
                }
                catch (OperationCanceledException) when (resourceToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (error is AppError { Status: 429 }) LiveTelemetry.GraphDeferred.Add(1);
                    else LiveTelemetry.Failure("control");
                    var code = error is AppError appError ? appError.Code : error.GetType().Name;
                    var failed = await control.RepairAsync(resource.Id, job.ParentId, resourceToken);
                    var stage = failed?.Value.Verifying == true ? "verification" : "hydration";
                    var recorded = failed?.Value;
                    if (failed is { Value.Pending: true } && attempted is not null &&
                        failed.Value.Path == attempted.Value.Path && failed.Value.NextLink == attempted.Value.NextLink &&
                        failed.Value.ScanCursor == attempted.Value.ScanCursor &&
                        failed.Value.LastProgressAt == attempted.Value.LastProgressAt &&
                        failed.Value.Recovery == attempted.Value.Recovery &&
                        (attempted.Value.Generation is null || failed.Value.Generation == attempted.Value.Generation))
                    {
                        var metadata = await store.GetAsync(tenant, resource.Id, resourceToken)
                            ?? throw new AppError(503, "resource_preparing", "Repair recovery awaits resource metadata.");
                        var restart = code is "graph_400" or "graph_410" && failed.Value.NextLink is not null &&
                            !failed.Value.Verifying && (failed.Value.Recovery?.Restarts ?? 0) == 0;
                        var blocked = error is AppError { Status: 400 or 403 or 404 or 410 } ||
                            code is "graph_url_invalid" or "graph_cursor_invalid" or "notification_resource_invalid" or
                                "graph_400" or "graph_403" or "graph_404" or "graph_410" or
                                "synthetic_scope_denied" or "synthetic_scope_mismatch";
                        recorded = await control.FailRepairAsync(resource, failed, metadata.Value.RepairGeneration,
                            code, blocked && !restart, restart, resourceToken);
                        if (recorded is null)
                            logger.LogInformation("Repair failure checkpoint changed concurrently; resource={ResourceId}, stage={Stage}, code={Code}.",
                                resource.Id, stage, code);
                    }
                    if (ShouldLog("repair:" + resource.Id, code, out var suppressed))
                        logger.LogWarning("Live control repair failed; resource={ResourceId}, checkpoint={Checkpoint}, stage={Stage}, code={Code}, generation={Generation}, " +
                            "state={State}, attempts={Attempts}, lastProgress={LastProgress}, nextAttempt={NextAttempt}, restarts={Restarts}, suppressed={Suppressed}.",
                            resource.Id, LiveControlStore.RepairId(job.ParentId), stage, code, recorded?.Generation, recorded?.Status,
                            recorded?.Failure?.Attempts ?? 0, recorded?.LastProgressAt ?? recorded?.UpdatedAt,
                            recorded?.Failure?.NextAttemptAt, recorded?.Recovery?.Restarts ?? 0, suppressed);
                }
            }
        });
    }
    private async Task<DateTimeOffset?> CompleteCoverageAsync(ApprovedResource resource, RepairState root,
        DateTimeOffset expires, CancellationToken ct)
    {
        var freshUntil = root.UpdatedAt.AddMinutes(5);
        if (expires < freshUntil) freshUntil = expires;
        if (root.Pending || !root.Verifying || root.Generation is null || freshUntil <= clock.GetUtcNow() ||
            await control.HasPendingRepairAsync(resource.Id, ct)) return null;
        await store.SetCoverageAsync(new(tenant, resource.Id, true, root.UpdatedAt, null, null,
            freshUntil, root.Generation), ct);
        return freshUntil;
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
    private bool ShouldLog(string key, string code, out int suppressed)
    {
        var now = clock.GetUtcNow();
        var previous = loggedFailures.GetValueOrDefault(key);
        var emit = previous.Code != code || now - previous.At >= TimeSpan.FromMinutes(1);
        loggedFailures[key] = (code, emit ? now : previous.At, emit ? 0 : previous.Suppressed + 1);
        suppressed = previous.Suppressed;
        return emit;
    }
    private async Task GuardAsync(Func<Task> action, string stage, CancellationToken ct, string resourceId)
    {
        try { await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AppError ex) when (ex.Status == 429)
        {
            LiveTelemetry.GraphDeferred.Add(1);
            if (ShouldLog(stage + ":" + resourceId, ex.Code, out var suppressed))
                logger.LogInformation("Live control {Stage} deferred; resource={ResourceId}, code={Code}, suppressed={Suppressed}; work remains pending.",
                    stage, resourceId, ex.Code, suppressed);
        }
        catch (AppError ex)
        {
            LiveTelemetry.Failure("control");
            if (ShouldLog(stage + ":" + resourceId, ex.Code, out var suppressed))
                logger.LogWarning("Live control {Stage} failed; resource={ResourceId}, code={Code}, suppressed={Suppressed}; checkpoint retained.",
                    stage, resourceId, ex.Code, suppressed);
        }
        catch (Exception ex)
        {
            LiveTelemetry.Failure("control");
            if (ShouldLog(stage + ":" + resourceId, ex.GetType().Name, out var suppressed))
                logger.LogError("Live control {Stage} failed; resource={ResourceId}, errorType={ErrorType}, suppressed={Suppressed}; checkpoint retained.",
                    stage, resourceId, ex.GetType().Name, suppressed);
        }
    }
}
