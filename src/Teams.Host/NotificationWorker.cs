using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Teams.Core;

namespace Teams.Host;

public sealed class ServiceBusNotificationInbox(ServiceBusClient client) : IEncryptedNotificationInbox, IAsyncDisposable
{
    private readonly ServiceBusSender sender = client.CreateSender("graph-notifications");
    public ValueTask DisposeAsync() => sender.DisposeAsync();
    public async Task EnqueueAsync(IReadOnlyList<EncryptedNotification> notifications, CancellationToken ct)
    {
        var batches = new List<ServiceBusMessageBatch>();
        try
        {
            var batch = await sender.CreateMessageBatchAsync(ct);
            batches.Add(batch);
            foreach (var notification in notifications)
            {
                var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(notification))
                {
                    MessageId = Guid.NewGuid().ToString("N"), ContentType = "application/json", TimeToLive = TimeSpan.FromHours(1)
                };
                if (batch.TryAddMessage(message)) continue;
                batch = await sender.CreateMessageBatchAsync(ct);
                batches.Add(batch);
                if (!batch.TryAddMessage(message))
                    throw new AppError(503, "notification_too_large", "Encrypted notification exceeds the broker's approved size; durable acceptance failed.");
            }
            foreach (var accepted in batches.Where(b => b.Count > 0)) await sender.SendMessagesAsync(accepted, ct);
        }
        finally { foreach (var batch in batches) batch.Dispose(); }
    }
}
public sealed class NotificationProjector(LiveOptions options, string tenant, ILiveStateStore store, LiveControlStore control,
    NotificationDecryptor decryptor, IEncryptedNotificationInbox? inbox = null)
{
    public async Task ApplyAsync(EncryptedNotification notification, CancellationToken ct)
    {
        if (notification.SchemaVersion != 1)
            throw new AppError(400, "schema_unsupported", "Unsupported encrypted notification schema.");
        if (notification.TenantId != tenant) throw new AppError(403, "tenant_mismatch", "Notification belongs to another tenant.");
        if (notification.LifecycleEvent is not null && notification.ResourceId.StartsWith("scope:", StringComparison.Ordinal))
        {
            var scope = options.SubscriptionScopes.SingleOrDefault(s => $"scope:{s.Id}" == notification.ResourceId);
            if (scope is null || scope.Path != notification.ResourcePath || inbox is null)
                throw new AppError(403, "scope_mismatch", "Lifecycle scope is not configured.");
            var subscription = await control.SubscriptionAsync(scope.Id, ct);
            if (notification.SubscriptionId is not null && subscription?.Value.GraphId != notification.SubscriptionId) return;
            var current = await control.LifecycleAsync(scope.Id, ct);
            if (current is null || notification.ReceivedAt > current.Value.At)
                if (!await control.SaveLifecycleAsync(scope.Id, new(notification.LifecycleEvent, notification.ReceivedAt),
                    current?.Version, ct)) throw new AppError(409, "lifecycle_conflict", "Lifecycle state changed concurrently.");
            var resources = options.Resources.Where(scope.Covers).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            if (notification.Offset < 0 || notification.Offset > resources.Length) throw new JsonException("Invalid lifecycle continuation.");
            foreach (var affected in resources.Skip(notification.Offset).Take(25))
            {
                if (await store.GetAsync(tenant, affected.Id, ct) is not null)
                    await store.SetCoverageAsync(new(tenant, affected.Id, false, null, null, notification.LifecycleEvent), ct);
                await control.EnqueueRepairAsync(affected, null, null, ct);
            }
            if (notification.Offset + 25 < resources.Length)
                await inbox.EnqueueAsync([notification with { Offset = notification.Offset + 25 }], ct);
            return;
        }
        var resource = options.Resources.SingleOrDefault(r => r.Id == notification.ResourceId)
            ?? throw new AppError(403, "resource_unknown", "Notification resource is not approved.");
        if (notification.LifecycleEvent is not null)
        {
            await store.SetCoverageAsync(new(notification.TenantId, resource.Id, false, null, null, notification.LifecycleEvent), ct);
            await control.EnqueueRepairAsync(resource, null, null, ct);
            return;
        }
        var path = GraphMessages.NormalizeResource(notification.ResourcePath);
        if (!path.StartsWith(resource.Path + "/", StringComparison.Ordinal) || notification.EncryptedContent is null)
            throw new AppError(403, "resource_mismatch", "Notification does not match its approved resource.");
        var parts = path[(resource.Path.Length + 1)..].Split('/');
        if (parts.Length != 1 && (parts.Length != 3 || parts[1] != "replies" || resource.Kind != "channel"))
            throw new AppError(400, "resource_mismatch", "Invalid message path.");
        var parent = parts.Length == 3 ? Uri.UnescapeDataString(parts[0]) : null;
        var data = await decryptor.DecryptAsync(notification.EncryptedContent.Value, ct);
        if (data.TryGetProperty("messageType", out var type) && type.ValueKind == JsonValueKind.String &&
            type.GetString() is not (null or "message"))
        {
            await store.SetCoverageAsync(new(notification.TenantId, resource.Id, false, null, null, "source_control_changed"), ct);
            await control.EnqueueRepairAsync(resource, null, null, ct);
            return;
        }
        var message = GraphMessages.Parse(data, parent);
        if (options.DelegatedSynthetic)
        {
            if (message.CreatedAt < options.SyntheticHistoryFrom[resource.Id]) return;
            if (message.ActorId.Length > 0 && !options.SyntheticActorIds.Contains(message.ActorId))
            {
                await store.SetCoverageAsync(new(notification.TenantId, resource.Id, false, null, null, "synthetic_author_changed"), ct);
                throw new AppError(403, "synthetic_scope_mismatch", "The message author is outside the approved test identities.");
            }
        }
        if (message.Id != Uri.UnescapeDataString(parts[^1]) ||
            notification.ChangeType == "deleted" && !message.Deleted)
            throw new AppError(400, "resource_mismatch", "Decrypted message identity or deletion does not match its envelope.");
        if (resource.Kind == "chat" && data.TryGetProperty("chatId", out var chat) && chat.GetString() != resource.GraphChatId)
            throw new AppError(400, "resource_mismatch", "Decrypted chat context does not match.");
        if (resource.Kind == "channel" && data.TryGetProperty("channelIdentity", out var channel) &&
            (channel.GetProperty("teamId").GetString() != resource.GraphTeamId ||
                channel.GetProperty("channelId").GetString() != resource.GraphChannelId))
            throw new AppError(400, "resource_mismatch", "Decrypted channel context does not match.");
        await store.ApplyAsync(notification.TenantId, resource.Id, message, ct);
        var projected = await store.MessageAsync(notification.TenantId, resource.Id, message.Id, message.ParentId, ct);
        var metadata = (await store.GetAsync(notification.TenantId, resource.Id, ct))!.Value;
        if (projected?.SourceVersion == message.SourceVersion)
            await control.SaveReceiptAsync(new(resource.Id, message.Id, message.ParentId, metadata.Revision,
                message.SourceVersion, notification.ReceivedAt, DateTimeOffset.UtcNow), ct);
        if (!metadata.CoverageReady)
            await control.EnqueueRepairAsync(resource, null, null, ct);
    }
}
public sealed class NotificationWorker(ServiceBusClient client, NotificationProjector projector,
    ILiveStateStore store, LiveControlStore control, LiveOptions options, IEncryptedNotificationInbox inbox,
    IConfiguration configuration, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled)
        {
            logger.LogInformation("Graph notification consumption is disabled pending synchronization approval.");
            return;
        }
        var tenant = configuration["App:TenantId"] ?? throw new InvalidOperationException("App:TenantId is required.");
        await using var processor = client.CreateProcessor("graph-notifications", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, MaxConcurrentCalls = options.QueueConcurrency,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5)
        });
        processor.ProcessErrorAsync += error =>
        {
            logger.LogError("Graph notification broker error: {Source}, {ErrorType}", error.ErrorSource, error.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        processor.ProcessMessageAsync += async args =>
        {
            EncryptedNotification? notification = null;
            try
            {
                notification = args.Message.Body.ToObjectFromJson<EncryptedNotification>()
                    ?? throw new JsonException("Encrypted envelope is required.");
                if (DateTimeOffset.UtcNow - notification.ReceivedAt > TimeSpan.FromHours(1))
                    throw new AppError(410, "notification_expired", "Expired notification requires authoritative repair.");
                await projector.ApplyAsync(notification, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LiveTelemetry.Failure("notification");
                logger.LogError("Graph notification apply failed: {ErrorType}; delivery {Delivery}", ex.GetType().Name, args.Message.DeliveryCount);
                var resource = notification?.TenantId != tenant ? null : options.Resources.SingleOrDefault(r => r.Id == notification.ResourceId);
                if (resource is not null)
                {
                    await store.SetCoverageAsync(new(notification!.TenantId, resource.Id, false, null, null, "notification_failed"), args.CancellationToken);
                    await control.EnqueueRepairAsync(resource, null, null, args.CancellationToken);
                }
                if (args.Message.DeliveryCount >= 5)
                    await args.DeadLetterMessageAsync(args.Message, "projection_failed", ex.GetType().Name, args.CancellationToken);
                else await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
            }
        };
        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await using var deadLetters = client.CreateReceiver("graph-notifications",
                new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var expired = await deadLetters.ReceiveMessagesAsync(25, TimeSpan.FromSeconds(1), stoppingToken);
                    foreach (var message in expired)
                    {
                        if (message.EnqueuedTime > DateTimeOffset.UtcNow.AddHours(-1))
                        {
                            await deadLetters.AbandonMessageAsync(message, cancellationToken: stoppingToken);
                            continue;
                        }
                        var repair = options.SubscriptionScopes.Select(scope => new EncryptedNotification(
                            tenant, $"scope:{scope.Id}", scope.Path, "", null, DateTimeOffset.UtcNow, "missed")).ToArray();
                        await inbox.EnqueueAsync(repair, stoppingToken);
                        await deadLetters.CompleteMessageAsync(message, stoppingToken);
                        logger.LogWarning("Expired graph dead-letter removed after durable scope-repair scheduling.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Graph dead-letter retention pass failed: {ErrorType}; work retained.", ex.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await processor.StopProcessingAsync(CancellationToken.None); }
    }
}
