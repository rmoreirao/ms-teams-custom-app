using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Teams.Core;

namespace Teams.Host;

public sealed class ReadRefreshQueueValidation(ServiceBusClient client, LiveOptions options, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.SynchronizationEnabled) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var role = configuration["App:Role"]!;
        if (options.Hosts("Api", role))
        {
            await using var sender = client.CreateSender(ServiceBusReadRefresh.QueueName);
            using var batch = await sender.CreateMessageBatchAsync(deadline.Token);
        }
        if (options.Hosts("Control", role))
        {
            await using var receiver = client.CreateReceiver(ServiceBusReadRefresh.QueueName);
            await receiver.PeekMessageAsync(cancellationToken: deadline.Token);
        }
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class ServiceBusReadRefresh(ServiceBusClient client, LiveOptions options, IConfiguration configuration,
    TimeProvider clock, ILogger<ServiceBusReadRefresh> logger) : IReadRefresh, IAsyncDisposable
{
    public const string QueueName = "graph-read-refresh";
    private readonly ServiceBusSender sender = client.CreateSender(QueueName);
    public ValueTask DisposeAsync() => sender.DisposeAsync();
    public async Task RequestAsync(ReadRefreshRequest request, bool required, CancellationToken ct)
    {
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(request))
        {
            MessageId = $"{request.Key}:{request.RequestedAt.ToUnixTimeSeconds() / 2}",
            ContentType = "application/json", TimeToLive = TimeSpan.FromMinutes(5)
        };
        try
        {
            ReadRefreshValidation.Check(request, configuration["App:TenantId"]!, options, clock.GetUtcNow());
            await sender.SendMessageAsync(message, ct);
            LiveTelemetry.ReadRefreshRequests.Add(1);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LiveTelemetry.ReadRefreshFailures.Add(1);
            logger.LogError("Read refresh submission failed: {ErrorType}; no hydration success is claimed.", error.GetType().Name);
            if (required) throw new AppError(503, "read_refresh_unavailable", "History could not be loaded. Refresh is currently unavailable.");
        }
    }
}

public sealed class ReadRefreshDeferredException(DateTimeOffset retryAt) : Exception("Read refresh is already owned or deferred.")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
public static class ReadRefreshValidation
{
    public static ApprovedResource Check(ReadRefreshRequest request, string tenant, LiveOptions options, DateTimeOffset now)
    {
        var resource = options.Resources.SingleOrDefault(value => value.Id == request.ConversationId);
        if (request.SchemaVersion != 1 || request.TenantId != tenant || resource is null ||
            request.RequestedAt > now.AddMinutes(1) || request.RequestedAt < now.AddMinutes(-5) ||
            request.Parent is not null && (resource.Kind != "channel" || request.Parent.Length is < 1 or > 128 ||
                request.Parent is "." or ".." || request.Parent.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))) ||
            request.Before > now.AddMinutes(1) || request.Before < now.AddDays(-7) ||
            (request.Before is null) != (request.BeforeId is null) || request.BeforeId?.Length > 512)
            throw new AppError(400, "read_refresh_invalid", "Refresh tenant, resource, context or age is not approved.");
        return resource;
    }
}

public sealed class ReadRefreshHandler(string tenant, LiveOptions options, ILiveStateStore store,
    LiveControlStore control, IGraphBackground graph, TimeProvider clock, ILogger<ReadRefreshHandler> logger)
{
    public async Task HandleAsync(ReadRefreshRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var resource = ReadRefreshValidation.Check(request, tenant, options, now);
        var metadata = await store.GetAsync(tenant, resource.Id, ct)
            ?? throw new AppError(404, "resource_unknown", "Refresh requires an onboarded resource.");
        if (!resource.Matches(metadata.Value))
            throw new AppError(400, "read_refresh_invalid", "Refresh source mapping is not approved.");
        if (request.Epoch != options.WriterEpoch || metadata.Value.Epoch != request.Epoch)
            throw new AppError(409, "writer_fenced", "Refresh ownership changed.");
        LiveTelemetry.ReadRefreshAge.Record(Math.Max(0, (now - request.RequestedAt).TotalMilliseconds));
        var previous = await control.ReadRefreshAsync(request.Key, ct);
        if (previous?.Value.CompletedThrough >= request.RequestedAt)
        {
            LiveTelemetry.ReadRefreshCoalesced.Add(1);
            return;
        }
        if (previous?.Value.LeaseUntil > now || previous?.Value.RetryAt > now)
        {
            if (previous.Value.RequestedAt != request.RequestedAt)
            {
                LiveTelemetry.ReadRefreshCoalesced.Add(1);
                return;
            }
            throw new ReadRefreshDeferredException(new[] { previous.Value.LeaseUntil,
                previous.Value.RetryAt ?? DateTimeOffset.MinValue }.Max());
        }
        var owner = Guid.NewGuid().ToString("N");
        var claim = new ReadRefreshLease(owner, now.AddMinutes(2), previous?.Value.CompletedThrough,
            Attempts: previous?.Value.Attempts ?? 0, RequestedAt: request.RequestedAt);
        if (!await control.SaveReadRefreshAsync(request.Key, previous, claim, ct))
            throw new ReadRefreshDeferredException(now.AddSeconds(1));
        var lease = await control.ReadRefreshAsync(request.Key, ct);
        if (lease?.Value.Owner != owner) throw new ReadRefreshDeferredException(now.AddSeconds(1));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            await HydrateAsync(resource, request, deadline.Token);
            if (!await control.SaveReadRefreshAsync(request.Key, lease,
                claim with { Owner = "", LeaseUntil = DateTimeOffset.MinValue, CompletedThrough = clock.GetUtcNow() }, ct))
                throw new AppError(409, "read_refresh_conflict", "Refresh completion changed concurrently.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var attempts = Math.Min(10, claim.Attempts + 1);
            var retry = clock.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, attempts)) + Random.Shared.NextDouble());
            var code = error is AppError app ? app.Code : error.GetType().Name;
            if (!await control.SaveReadRefreshAsync(request.Key, lease, claim with
                { Owner = "", LeaseUntil = DateTimeOffset.MinValue, RetryAt = retry, Attempts = attempts, Failure = code }, CancellationToken.None))
                logger.LogWarning("Read refresh failure checkpoint was superseded; ownership remains conditional.");
            if (error is not AppError { Status: 429 })
            {
                LiveTelemetry.ReadRefreshFailures.Add(1);
                logger.LogError("Read refresh failed: {Code}; work and retry evidence are retained.", code);
            }
            else LiveTelemetry.GraphDeferred.Add(1);
            throw;
        }
    }

    private async Task HydrateAsync(ApprovedResource resource, ReadRefreshRequest request, CancellationToken ct)
    {
        var scoped = graph.ForResource(resource.Path);
        var previous = await store.ReadViewAsync(tenant, resource.Id, request.Parent, ct);
        var state = previous?.Value.Epoch == request.Epoch ? previous.Value : null;
        if (state?.Complete == true && !state.ParentMissing) return;
        if (request.Parent is not null)
        {
            var root = await store.CurrentMessageAsync(tenant, resource.Id, request.Parent, null, ct);
            if (root is null)
            {
                try
                {
                    root = GraphMessages.Parse(await scoped.GetAsync(
                        $"{resource.Path}/{Uri.EscapeDataString(request.Parent)}", ct));
                    if (root.Id != request.Parent || root.ParentId is not null)
                        throw new JsonException("Source parent does not match its requested context.");
                    if (!ValidateSynthetic(root, resource))
                        throw new AppError(403, "synthetic_scope_mismatch", "The source parent predates approved synthetic history.");
                    await store.ApplyAsync(tenant, resource.Id, root, ct);
                }
                catch (AppError error) when (error.Code == "graph_404")
                {
                    await SaveAsync(new(request.Epoch, true, null, clock.GetUtcNow(), ParentMissing: true));
                    return;
                }
            }
            if (root.Deleted || root.CreatedAt < clock.GetUtcNow().AddDays(-7))
            {
                await SaveAsync(new(request.Epoch, true, null, clock.GetUtcNow(), ParentMissing: true));
                return;
            }
        }
        var expected = request.Parent is null ? resource.Path :
            $"{resource.Path}/{Uri.EscapeDataString(request.Parent)}/replies";
        var path = state?.NextLink ?? $"{expected}?$top=50" +
            (resource.Kind == "chat" ? "&$orderby=createdDateTime desc" : "");
        for (var step = 0; step < 3; step++)
        {
            GraphUrls.Validate(path, expected);
            var page = await scoped.GetAsync(path, ct);
            var items = page.GetProperty("value");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 50)
                throw new JsonException("Source hydration page exceeds its bound.");
            var messages = items.EnumerateArray()
                .Where(item => !item.TryGetProperty("messageType", out var type) || type.GetString() is null or "message")
                .Select(item => GraphMessages.Parse(item, request.Parent)).ToArray();
            foreach (var message in messages)
            {
                if (!ValidateSynthetic(message, resource)) continue;
                await store.ApplyAsync(tenant, resource.Id, message, ct);
            }
            var next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            if (next is not null) GraphUrls.Validate(next, expected);
            var oldest = messages.Select(message => (DateTimeOffset?)message.CreatedAt).Min();
            if (state?.OldestAt is { } prior && (oldest is null || prior < oldest)) oldest = prior;
            if (resource.Kind == "chat" && oldest < clock.GetUtcNow().AddDays(-7)) next = null;
            state = new(request.Epoch, next is null, next, clock.GetUtcNow(), oldest);
            await SaveAsync(state);
            if (next is null || messages.Any(message => message.CreatedAt >= clock.GetUtcNow().AddDays(-7) &&
                (request.Before is null || message.CreatedAt < request.Before ||
                    message.CreatedAt == request.Before && StringComparer.Ordinal.Compare(message.Id, request.BeforeId) > 0))) return;
            path = next;
        }

        async Task SaveAsync(ReadViewState value)
        {
            if (!await store.SaveReadViewAsync(tenant, resource.Id, request.Parent, previous, value, ct))
                throw new AppError(409, "read_refresh_conflict", "View progress changed concurrently.");
            LiveTelemetry.ReadRefreshProgress.Add(1);
            previous = await store.ReadViewAsync(tenant, resource.Id, request.Parent, ct)
                ?? throw new AppError(503, "read_refresh_unavailable", "View progress is not yet visible.");
            if (previous.Value != value)
                throw new AppError(409, "read_refresh_conflict", "View progress was advanced by another owner.");
        }
    }
    private bool ValidateSynthetic(Message message, ApprovedResource resource)
    {
        if (!options.DelegatedSynthetic) return true;
        if (message.ActorId.Length > 0 && !options.SyntheticActorIds.Contains(message.ActorId))
            throw new AppError(403, "synthetic_scope_mismatch", "Refresh author is outside the approved synthetic scope.");
        return message.CreatedAt >= options.SyntheticHistoryFrom[resource.Id];
    }
}

public sealed class ReadRefreshWorker(ServiceBusClient client, ReadRefreshHandler handler, LiveOptions options,
    ILogger<ReadRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SynchronizationEnabled) return;
        await using var processor = client.CreateProcessor(ServiceBusReadRefresh.QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, MaxConcurrentCalls = Math.Min(2, options.QueueConcurrency),
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(3)
        });
        processor.ProcessErrorAsync += args =>
        {
            LiveTelemetry.ReadRefreshFailures.Add(1);
            logger.LogError("Read refresh broker error: {ErrorType}.", args.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var request = args.Message.Body.ToObjectFromJson<ReadRefreshRequest>()
                    ?? throw new JsonException("Refresh request is required.");
                while (true)
                {
                    try { await handler.HandleAsync(request, args.CancellationToken); break; }
                    catch (ReadRefreshDeferredException deferred)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(
                            (deferred.RetryAt - DateTimeOffset.UtcNow).TotalMilliseconds, 100, 120000)), args.CancellationToken);
                    }
                }
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                if (error is not AppError { Status: 429 })
                    logger.LogError("Read refresh delivery failed: {ErrorType}; work is not acknowledged as hydrated.", error.GetType().Name);
                else logger.LogInformation("Read refresh is deferred after Graph throttling exhausted its retry budget.");
                if (error is JsonException or AppError { Status: 400 or 403 or 404 } or AppError { Code: "writer_fenced" } ||
                    args.Message.DeliveryCount >= 5)
                    await args.DeadLetterMessageAsync(args.Message, "read_refresh_failed", error.GetType().Name, args.CancellationToken);
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, args.Message.DeliveryCount)) +
                        Random.Shared.NextDouble()), args.CancellationToken);
                    await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                }
            }
        };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await processor.StopProcessingAsync(CancellationToken.None); }
    }
}
