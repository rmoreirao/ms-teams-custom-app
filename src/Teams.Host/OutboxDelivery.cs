using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public sealed class ServiceBusDeliveryQueue(ServiceBusClient client) : IDeliveryWorkQueue, IAsyncDisposable
{
    private readonly ServiceBusSender sender = client.CreateSender("graph-delivery");
    public ValueTask DisposeAsync() => sender.DisposeAsync();
    public async Task EnqueueAsync(DeliveryWork work, DateTimeOffset? scheduledAt, CancellationToken ct)
    {
        var key = JsonSerializer.Serialize(new { work.Change.TenantId, work.Change.ConversationId, work.Change.Id, work.Page,
            work.RecoveryId, ActorId = work.ActorEvent?.ActorId });
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(work))
        {
            MessageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
            ContentType = "application/json", TimeToLive = TimeSpan.FromHours(1)
        };
        if (scheduledAt is not null) await sender.ScheduleMessageAsync(message, scheduledAt.Value, ct);
        else await sender.SendMessageAsync(message, ct);
    }
}
public sealed class OutboxDispatcher(string tenant, LiveOptions options, IDeliveryWorkQueue queue,
    ILogger<OutboxDispatcher> logger, IResourceRegistry? registry = null)
{
    public async Task DispatchAsync(IReadOnlyCollection<PartitionDocument> documents, CancellationToken ct)
    {
        foreach (var document in documents.Where(d => d.Kind == "actor-outbox" && d.Pending))
        {
            var item = JsonSerializer.Deserialize<ActorEventWork>(document.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException("Actor event is invalid.");
            if (!options.ChannelDiscovery.RegistryEnabled || document.TenantId != tenant ||
                item.TenantId != tenant || item.ActorId != document.ActorId || !options.Admits(item.ActorId))
                throw new AppError(403, "channel_event_invalid", "Actor event does not match the admitted scope.");
            await queue.EnqueueAsync(new(new(item.Id, tenant, "", item.Event.Sequence, item.CreatedAt), ActorEvent: item), null, ct);
        }
        foreach (var document in documents.Where(d => d.Kind == "outbox" && d.Pending))
        {
            if (document.SchemaVersion != 1 || document.TenantId != tenant)
                throw new InvalidOperationException("Outbox schema/tenant is not configured; checkpoint retained.");
            var change = JsonSerializer.Deserialize<OutboxChange>(document.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException("Outbox change is invalid.");
            if (change.TenantId != tenant || change.ConversationId != document.ConversationId || change.Revision != document.Revision)
                throw new InvalidOperationException("Outbox identity does not match its document.");
            if (registry is null ? !options.Resources.Any(r => r.Id == change.ConversationId) :
                await registry.FindAsync(change.ConversationId, ct) is null)
            {
                logger.LogWarning("Outbox resource is no longer approved; suppressing dispatch until an approved resource cleanup.");
                continue;
            }
            await queue.EnqueueAsync(new(change), null, ct);
        }
    }
}
public sealed class OutboxFeedWorker(CosmosClient cosmos, OutboxDispatcher dispatcher, LiveOptions options,
    ILogger<OutboxFeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled) return;
        var processor = cosmos.GetContainer("teams", "live-state")
            .GetChangeFeedProcessorBuilder<PartitionDocument>("live-outbox-v1", dispatcher.DispatchAsync)
            .WithInstanceName($"{Environment.MachineName}-{Guid.NewGuid():N}")
            .WithLeaseContainer(cosmos.GetContainer("teams", "live-leases"))
            .WithStartTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc))
            .WithMaxItems(100)
            .WithPollInterval(TimeSpan.FromSeconds(1))
            .WithErrorNotification((_, error) =>
            {
                logger.LogError("Outbox change-feed processing failed: {ErrorType}; publication checkpoint is retained.", error.GetType().Name);
                return Task.CompletedTask;
            }).Build();
        await processor.StartAsync();
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await processor.StopAsync(); }
    }
}
public sealed class DeliveryDeferredException(DateTimeOffset retryAt) : Exception("Another consumer owns this delivery page.")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
public sealed class DeliveryHandler(string tenant, LiveOptions options, ILiveStateStore store, IRealtime realtime,
    IDeliveryWorkQueue queue, TimeProvider clock, ILogger<DeliveryHandler> logger,
    IResourceRegistry? registry = null, ChannelWorkflowStore? channelWorkflows = null)
{
    public async Task HandleAsync(DeliveryWork work, CancellationToken ct)
    {
        using var measurement = LiveTelemetry.Measure("delivery-page");
        if (work.ActorEvent is { } actorEvent)
        {
            if (!options.ChannelDiscovery.RegistryEnabled || channelWorkflows is null || !options.Admits(actorEvent.ActorId))
                throw new AppError(403, "channel_event_invalid", "Actor-event delivery is not enabled for this scope.");
            await channelWorkflows.DeliverAsync(actorEvent, realtime, ct);
            return;
        }
        if (work.SchemaVersion != 1 || work.Change.TenantId != tenant || work.Page < 0 ||
            work.Change.CreatedAt > clock.GetUtcNow().AddMinutes(1) || work.Change.CreatedAt < clock.GetUtcNow().AddDays(-14) ||
            (registry is null ? !options.Resources.Any(r => r.Id == work.Change.ConversationId) :
                await registry.FindAsync(work.Change.ConversationId, ct) is null))
            throw new AppError(400, "delivery_invalid", "Delivery schema, tenant, age or resource is not approved.");
        var progress = await store.DeliveryAsync(work.Change, ct);
        if (progress.Value.Complete) return;
        if (work.Page < progress.Value.Page)
        {
            await queue.EnqueueAsync(work with { Page = progress.Value.Page }, null, ct);
            return;
        }
        if (work.Page != progress.Value.Page) throw new AppError(409, "delivery_page_invalid", "Delivery work is ahead of its durable checkpoint.");
        if (progress.Value.LeaseUntil > clock.GetUtcNow())
            throw new DeliveryDeferredException(progress.Value.LeaseUntil);
        var metadata = await store.GetAsync(tenant, work.Change.ConversationId, ct)
            ?? throw new AppError(503, "delivery_resource_missing", "Outbox conversation is not available.");
        if (metadata.Value.Revision < work.Change.Revision)
            throw new AppError(503, "delivery_revision_unavailable", "Committed projection revision is not available.");
        var owner = Guid.NewGuid().ToString("N");
        var claimed = progress.Value with { Owner = owner, LeaseUntil = clock.GetUtcNow().AddMinutes(2),
            TargetRevision = progress.Value.TargetRevision == 0 ? metadata.Value.Revision : progress.Value.TargetRevision };
        if (!await store.SaveDeliveryAsync(work.Change, progress, claimed, ct))
            throw new DeliveryDeferredException(clock.GetUtcNow().AddSeconds(5));
        var lease = await store.DeliveryAsync(work.Change, ct);
        if (lease.Value.Owner != owner) throw new DeliveryDeferredException(clock.GetUtcNow().AddSeconds(5));
        try
        {
            if (work.Change.Event is null && metadata.Value.PublishedRevision >= work.Change.Revision)
            {
                if (!await store.SaveDeliveryAsync(work.Change, lease, lease.Value with { Complete = true,
                    TargetRevision = metadata.Value.PublishedRevision, Owner = "", LeaseUntil = DateTimeOffset.MinValue }, ct))
                    throw new AppError(409, "delivery_checkpoint_conflict", "Coalesced completion changed concurrently.");
                return;
            }
            var page = await store.RecipientsAsync(tenant, work.Change.ConversationId, lease.Value.Cursor, ct);
            List<Member> members = [];
            foreach (var grant in page.Items)
            {
                try { members.Add(await store.GetMemberAsync(new(tenant, grant.ActorId, grant.Name), work.Change.ConversationId, false, ct)); }
                catch (AppError ex) when (ex.Status is 401 or 403 or 404 || ex.Code == "authorization_stale")
                {
                    logger.LogInformation("Delivery recipient omitted after current authorization denial: {Code}.", ex.Code);
                }
            }
            if (members.Count > 0)
            {
                if (work.Change.Event is { } viewEvent)
                {
                    foreach (var member in members)
                        await realtime.PublishEventAsync(tenant, member.Id, viewEvent, ct);
                }
                else await realtime.PublishAsync(metadata.Value with { Members = members, Revision = lease.Value.TargetRevision }, ct);
                LiveTelemetry.DeliveryRecipients.Add(members.Count);
            }
            var next = lease.Value with { Page = lease.Value.Page + 1, Cursor = page.Cursor, Complete = page.Cursor is null,
                Owner = "", LeaseUntil = DateTimeOffset.MinValue };
            if (!await store.SaveDeliveryAsync(work.Change, lease, next, ct))
                throw new AppError(409, "delivery_checkpoint_conflict", "Delivery checkpoint changed; hints may repeat but the page is not skipped.");
            LiveTelemetry.DeliveryPages.Add(1);
            if (!next.Complete) await queue.EnqueueAsync(work with { Page = next.Page }, null, ct);
        }
        catch
        {
            var released = await store.SaveDeliveryAsync(work.Change, lease,
                lease.Value with { Owner = "", LeaseUntil = DateTimeOffset.MinValue }, CancellationToken.None);
            if (!released) logger.LogWarning("Delivery lease release was superseded; recovery uses the current durable checkpoint.");
            throw;
        }
    }
}
public sealed class DeliveryQueueWorker(ServiceBusClient client, DeliveryHandler handler, IDeliveryWorkQueue queue,
    LiveOptions options, IConfiguration configuration, ILogger<DeliveryQueueWorker> logger, IResourceRegistry? registry = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled) return;
        var tenant = configuration["App:TenantId"] ?? throw new InvalidOperationException("App:TenantId is required.");
        await using var processor = client.CreateProcessor("graph-delivery", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, MaxConcurrentCalls = options.QueueConcurrency,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5)
        });
        processor.ProcessErrorAsync += error =>
        {
            logger.LogError("Delivery broker processing failed: {ErrorType}.", error.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var work = args.Message.Body.ToObjectFromJson<DeliveryWork>() ?? throw new JsonException("Delivery work is empty.");
                try { await handler.HandleAsync(work, args.CancellationToken); }
                catch (DeliveryDeferredException deferred)
                {
                    await queue.EnqueueAsync(work with { RecoveryId = Guid.NewGuid().ToString("N") },
                        deferred.RetryAt, args.CancellationToken);
                }
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LiveTelemetry.Failure("delivery");
                logger.LogError("Delivery page failed: {ErrorType}; durable progress is retained.", ex.GetType().Name);
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
            }
        };
        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await using var deadLetters = client.CreateReceiver("graph-delivery", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var message in await deadLetters.ReceiveMessagesAsync(25, TimeSpan.FromSeconds(1), stoppingToken))
                    {
                        DeliveryWork? work = null;
                        try { work = message.Body.ToObjectFromJson<DeliveryWork>(); }
                        catch (JsonException) { logger.LogWarning("Malformed body-free delivery dead letter is quarantined until retention expiry."); }
                        if (work is null || work.Change is null || work.SchemaVersion != 1 || work.Change.TenantId != tenant ||
                            (work.ActorEvent is not null ? !options.ChannelDiscovery.RegistryEnabled || !options.Admits(work.ActorEvent.ActorId) :
                                registry is null ? !options.Resources.Any(r => r.Id == work.Change.ConversationId) :
                                await registry.FindAsync(work.Change.ConversationId, stoppingToken) is null))
                        {
                            if (message.EnqueuedTime > DateTimeOffset.UtcNow.AddHours(-1))
                                await deadLetters.AbandonMessageAsync(message, cancellationToken: stoppingToken);
                            else
                            {
                                await deadLetters.CompleteMessageAsync(message, stoppingToken);
                                logger.LogWarning("Invalid or unapproved body-free delivery removed after quarantine retention.");
                            }
                            continue;
                        }
                        if (work.Change.CreatedAt > DateTimeOffset.UtcNow.AddDays(-14))
                            await queue.EnqueueAsync(work with { RecoveryId = Guid.NewGuid().ToString("N") },
                                DateTimeOffset.UtcNow.AddMinutes(1), stoppingToken);
                        else logger.LogWarning("Expired body-free delivery evidence removed; clients must use authorized snapshot recovery.");
                        await deadLetters.CompleteMessageAsync(message, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Delivery dead-letter recovery failed: {ErrorType}; work retained.", ex.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await processor.StopProcessingAsync(CancellationToken.None); }
    }
}
