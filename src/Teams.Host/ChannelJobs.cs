using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Teams.Core;

namespace Teams.Host;

public static class ChannelEligibility
{
    public static bool Supported(JsonElement channel)
    {
        if (channel.ValueKind != JsonValueKind.Object ||
            !channel.TryGetProperty("membershipType", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != "standard" || !channel.TryGetProperty("isArchived", out var archived) ||
            archived.ValueKind != JsonValueKind.False || !channel.TryGetProperty("moderationSettings", out var moderation))
            return false;
        return moderation.ValueKind == JsonValueKind.Null || moderation.ValueKind == JsonValueKind.Object &&
            moderation.TryGetProperty("userNewMessageRestriction", out var posts) && posts.ValueKind == JsonValueKind.String &&
            posts.GetString() is "everyone" or "everyoneExceptGuests" &&
            moderation.TryGetProperty("replyRestriction", out var replies) && replies.ValueKind == JsonValueKind.String &&
            replies.GetString() == "everyone";
    }
}

public sealed class ChannelGraphSource(GraphBackground graph, GraphPolicyFeed policy)
{
    private static string Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text
            ? text : throw new AppError(503, "channel_source_invalid", "Teams returned incomplete channel evidence.");
    private static (JsonElement[] Items, string? Next) Page(JsonElement page, Action<string> validateNext)
    {
        if (!page.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 1000)
            throw new AppError(503, "channel_source_invalid", "Teams returned an invalid discovery page.");
        var next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        if (next is not null) validateNext(next);
        return (items.EnumerateArray().ToArray(), next);
    }
    public async Task<(DiscoveredTeam[] Items, string? Next)> TeamsAsync(Actor actor, string? continuation, CancellationToken ct)
    {
        await policy.VerifyActorAsync(actor, ct);
        var path = $"users/{Uri.EscapeDataString(actor.Id)}/joinedTeams";
        GraphUrls.Validate(continuation ?? path, path);
        var page = Page(await graph.GetAsync(continuation ?? path, ct), link => GraphUrls.Validate(link, path));
        return (page.Items.Select(item => new DiscoveredTeam(Text(item, "id"), Text(item, "displayName"))).ToArray(), page.Next);
    }
    public async Task<DiscoveredTeam> VerifyTeamAsync(Actor actor, string teamId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(teamId, "D", out _))
            throw new AppError(400, "channel_team_invalid", "Select an available team.");
        string? next = null;
        var found = false;
        for (var page = 0; page < 20; page++)
        {
            var teams = await TeamsAsync(actor, next, ct);
            if (teams.Items.Any(item => item.Id == teamId)) { found = true; break; }
            next = teams.Next;
            if (next is null) break;
        }
        if (!found)
        {
            if (next is not null) throw new AppError(503, "channel_discovery_budget", "Team membership could not be established within the discovery budget.");
            throw new AppError(404, "channel_unavailable", "This team is not available to your account.");
        }
        var metadata = await graph.GetAsync($"teams/{Uri.EscapeDataString(teamId)}?$select=id,displayName,isArchived", ct);
        if (Text(metadata, "id") != teamId || !metadata.TryGetProperty("isArchived", out var archived) ||
            archived.ValueKind != JsonValueKind.False)
            throw new AppError(403, "channel_policy_unsupported", "Archived or unverified teams are not supported in this app.");
        return new(teamId, Text(metadata, "displayName"));
    }
    public async Task<(DiscoveredChannel[] Items, string? Next)> ChannelsAsync(Actor actor, string teamId,
        string? continuation, CancellationToken ct)
    {
        var team = await VerifyTeamAsync(actor, teamId, ct);
        var page = Page(await graph.GetChannelMetadataAsync(teamId, null, continuation, ct),
            link => GraphUrls.ChannelMetadata(teamId, null, link));
        return (page.Items.Where(ChannelEligibility.Supported).Select(item => new DiscoveredChannel(Text(item, "id"), teamId,
            Text(item, "displayName"), team.Name)).ToArray(), page.Next);
    }
    public async Task<DiscoveredChannel> ChannelAsync(Actor actor, string teamId, string channelId, CancellationToken ct)
    {
        var team = await VerifyTeamAsync(actor, teamId, ct);
        if (string.IsNullOrWhiteSpace(channelId) || channelId.Length > 512)
            throw new AppError(400, "channel_selection_invalid", "Select an available channel.");
        var item = await graph.GetChannelMetadataAsync(teamId, channelId, null, ct);
        if (Text(item, "id") != channelId || !ChannelEligibility.Supported(item))
            throw new AppError(403, "channel_policy_unsupported", "Only standard channels without moderation are supported.");
        return new(channelId, teamId, Text(item, "displayName"), team.Name);
    }
    public async Task VerifyActorAsync(Actor actor, CancellationToken ct)
    {
        await policy.VerifyActorAsync(actor, ct);
        ct.ThrowIfCancellationRequested();
    }
}

public sealed class ChannelRuntimeValidation(ServiceBusClient client, IResourceRegistry registry, LiveOptions options,
    IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await registry.ListAsync(deadline.Token);
        if (!options.SynchronizationEnabled) return;
        var role = configuration["App:Role"];
        if (role is "Api" or "Control")
        {
            await using var sender = client.CreateSender(ServiceBusChannelWork.QueueName);
            using var batch = await sender.CreateMessageBatchAsync(deadline.Token);
        }
        if (role == "Control")
        {
            await using var receiver = client.CreateReceiver(ServiceBusChannelWork.QueueName);
            await receiver.PeekMessageAsync(cancellationToken: deadline.Token);
        }
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class ServiceBusChannelWork(ServiceBusClient client) : IChannelWorkQueue, IAsyncDisposable
{
    public const string QueueName = "channel-work";
    private readonly ServiceBusSender sender = client.CreateSender(QueueName);
    public async Task EnqueueAsync(ChannelWork work, DateTimeOffset? scheduledAt, CancellationToken ct)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(work))));
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(work))
        {
            MessageId = scheduledAt is null ? id : $"{id}:{scheduledAt.Value.UtcTicks}",
            ContentType = "application/json", TimeToLive = TimeSpan.FromHours(1)
        };
        if (scheduledAt is null) await sender.SendMessageAsync(message, ct);
        else await sender.ScheduleMessageAsync(message, scheduledAt.Value, ct);
    }
    public ValueTask DisposeAsync() => sender.DisposeAsync();
}

public sealed class ChannelJobHandler(string tenant, LiveOptions options, ChannelWorkflowStore workflows,
    ChannelGraphSource source, IResourceRegistry registry, ILiveStateStore store, IEntitlementFeed authority,
    SubscriptionController subscriptions, ReadRefreshHandler hydration, TimeProvider clock, IRealtime realtime)
{
    public async Task<bool> HandleAsync(ChannelWork work, CancellationToken ct)
    {
        if (!options.ChannelDiscovery.RegistryEnabled || !options.SynchronizationEnabled ||
            work.SchemaVersion != 1 || work.TenantId != tenant ||
            !options.Admits(work.ActorId))
            throw new AppError(403, "channel_work_invalid", "Channel work is outside the admitted scope.");
        var current = await workflows.GetAsync(work.ActorId, work.OperationId, ct)
            ?? throw new AppError(400, "channel_work_invalid", "Channel work has no durable accepted request.");
        if (current.Value.Fingerprint != ChannelWorkflowStore.Fingerprint(work))
            throw new AppError(409, "channel_operation_conflict", "The queued request does not match the original operation.");
        if (current.Value.State is "ready" or "denied" or "failed" or "capacity") return true;
        if (current.Value.LeaseUntil > clock.GetUtcNow()) throw new DeliveryDeferredException(current.Value.LeaseUntil.Value);
        var owner = Guid.NewGuid().ToString("N");
        await workflows.SaveAsync(work.ActorId, current, current.Value with
        {
            State = "preparing", Owner = owner, LeaseUntil = clock.GetUtcNow().AddMinutes(2),
            Attempts = current.Value.Attempts + 1, Error = null
        }, ct);
        var claimed = await workflows.GetAsync(work.ActorId, work.OperationId, ct)
            ?? throw new AppError(503, "channel_state_invalid", "Channel ownership is missing.");
        if (claimed.Value.Owner != owner) throw new DeliveryDeferredException(clock.GetUtcNow().AddSeconds(1));
        var actor = new Actor(tenant, work.ActorId, work.ActorName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var token = timeout.Token;
            await source.VerifyActorAsync(actor, token);
            var next = claimed.Value with { State = "ready", Owner = null, LeaseUntil = null,
                FreshUntil = clock.GetUtcNow().AddMinutes(1) };
            if (work.Kind == "teams")
            {
                var page = await source.TeamsAsync(actor, work.Continuation, token);
                next = next with { Teams = page.Items, Continuation = page.Next };
            }
            else if (work.Kind == "channels")
            {
                var page = await source.ChannelsAsync(actor, work.TeamId ?? "", work.Continuation, token);
                next = next with { Channels = page.Items, Continuation = page.Next };
            }
            else if (work.Kind == "open")
            {
                var channel = await source.ChannelAsync(actor, work.TeamId ?? "", work.ChannelId ?? "", token);
                var registration = await registry.RegisterAsync(new(new(ResourceRegistry.ChannelId(tenant, channel.TeamId, channel.Id),
                    "channel", channel.Name, channel.TeamName, null, channel.TeamId, channel.Id),
                    new[] { options.ChannelDiscovery.HistoryFrom, clock.GetUtcNow().AddDays(-7) }.Max(),
                    true), token);
                var resource = registration.Resource;
                var metadata = await store.GetAsync(tenant, resource.Id, token);
                if (metadata is null)
                    await store.CreateAsync(new(resource.Id, tenant, resource.Title, "channel", resource.Team, [], [], [],
                        CoverageReady: false, GraphTeamId: resource.GraphTeamId, GraphChannelId: resource.GraphChannelId,
                        Epoch: options.WriterEpoch), token);
                var scope = (await registry.ScopesAsync(token)).Single(item => item.Path == resource.Path);
                await subscriptions.EnsureAsync(scope, token);
                var grants = await authority.ReadAsync(resource, null, token);
                foreach (var grant in grants.Entries)
                {
                    var revocation = await store.SetEntitlementAsync(grant, token);
                    if (revocation is null) continue;
                    await realtime.RevokeAsync(tenant, grant.ActorId, token);
                    await store.CompleteEntitlementRevocationAsync(revocation, token);
                }
                await store.GetMemberAsync(actor, resource.Id, false, token);
                metadata = await store.GetAsync(tenant, resource.Id, token)
                    ?? throw new AppError(503, "channel_preparing", "Channel metadata is not yet available.");
                await hydration.HandleAsync(new(tenant, resource.Id, options.WriterEpoch, null, null, null, clock.GetUtcNow(),
                    CacheGeneration: metadata.Value.CacheGeneration), token);
                var view = await store.ReadViewAsync(tenant, resource.Id, null, token);
                var member = await store.GetMemberAsync(actor, resource.Id, false, token);
                var messages = await store.MessagesAsync(actor, resource.Id, null, member.HistoryFrom, null, null, token);
                next = next with { ConversationId = resource.Id };
                if (messages.Count == 0 && view?.Value.Complete != true)
                    next = next with { State = "preparing" };
                else
                    foreach (var grant in grants.Entries.Where(item => item.Active))
                        await workflows.EmitAsync(grant.ActorId, new("channel.catalog.changed", 0, ConversationId: resource.Id), token);
            }
            else throw new AppError(400, "channel_work_invalid", "Unsupported channel work kind.");
            await workflows.SaveAsync(work.ActorId, claimed, next, ct);
            return next.State == "ready";
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = error is AppError app ? app.Code : error.GetType().Name;
            var denied = error is AppError { Status: 400 or 401 or 403 or 404 } && code != "graph_429";
            var failed = claimed.Value.Attempts >= 5 || code == "graph_400";
            var subject = work.Kind == "open" ? "Channel preparation" : work.Kind == "teams" ? "Team discovery" : "Channel discovery";
            var recovery = failed ? work.Kind == "open" ? "Reload the channel list before selecting again." :
                $"Reload {(work.Kind == "teams" ? "teams" : "channels")} to start a new discovery request." :
                "Progress will resume when the source is available. You can check the original request.";
            await workflows.SaveAsync(work.ActorId, claimed, claimed.Value with
            {
                State = code == "channel_capacity" ? "capacity" : denied ? "denied" : failed ? "failed" : "deferred",
                Owner = null, LeaseUntil = null,
                Error = denied ? "This channel is no longer available or supported." :
                    code == "channel_capacity" ? "Channel synchronization capacity has been reached. Open it in Teams." :
                    $"{subject} could not complete ({code}). {recovery}"
            }, CancellationToken.None);
            if (!denied && !failed && code != "channel_capacity") throw;
            return true;
        }
    }
}

public sealed class ChannelJobWorker(ServiceBusClient client, ChannelJobHandler handler, IChannelWorkQueue queue,
    LiveOptions options, ChannelWorkflowStore workflows, ILogger<ChannelJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled) return;
        await using var processor = client.CreateProcessor(ServiceBusChannelWork.QueueName, new ServiceBusProcessorOptions
        { AutoCompleteMessages = false, MaxConcurrentCalls = 2, MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5) });
        processor.ProcessErrorAsync += error =>
        {
            logger.LogError("Channel work transport failed: {ErrorType}.", error.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        processor.ProcessMessageAsync += async args =>
        {
            ChannelWork? work = null;
            try
            {
                work = args.Message.Body.ToObjectFromJson<ChannelWork>() ?? throw new JsonException("Channel work is empty.");
                var complete = await handler.HandleAsync(work, args.CancellationToken);
                if (!complete) await queue.EnqueueAsync(work, DateTimeOffset.UtcNow.AddSeconds(2), args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (DeliveryDeferredException deferred) when (work is not null)
            {
                await queue.EnqueueAsync(work, deferred.RetryAt, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Channel work failed: {Code}; original work retained.",
                    error is AppError app ? app.Code : error.GetType().Name);
                if (work is not null && error is not AppError { Status: 400 or 403 or 404 })
                {
                    await queue.EnqueueAsync(work, DateTimeOffset.UtcNow.AddSeconds(30), args.CancellationToken);
                    await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                }
                else await args.DeadLetterMessageAsync(args.Message, "channel_work_invalid",
                    error.GetType().Name, args.CancellationToken);
            }
        };
        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            using var recovery = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                foreach (var actor in options.GraphPolicy!.AdmittedUserIds)
                    try { await workflows.RecoverAsync(actor, queue, stoppingToken); }
                    catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    { logger.LogError("Channel workflow recovery failed: {ErrorType}; accepted work retained.", error.GetType().Name); }
            } while (await recovery.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await processor.StopProcessingAsync(); }
    }
}
