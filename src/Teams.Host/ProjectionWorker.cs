using Teams.Core;

namespace Teams.Host;

public sealed class ProjectionWorker(IStateStore store, IEventInbox inbox, IRealtime realtime,
    IConfiguration configuration, TimeProvider clock, ILogger<ProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tenant = configuration["App:TenantId"] ?? throw new InvalidOperationException("App:TenantId is required.");
        var nextPurge = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            InboxDelivery? delivery = null;
            try
            {
                if (clock.GetUtcNow() >= nextPurge)
                {
                    var purged = await inbox.PurgeExpiredAsync(clock.GetUtcNow(), stoppingToken);
                    if (purged > 0) logger.LogWarning("Expired synthetic dead-letter messages purged: {Count}", purged);
                    nextPurge = clock.GetUtcNow().AddMinutes(1);
                }
                delivery = await inbox.ReceiveAsync(stoppingToken);
                if (delivery is not null)
                {
                    if (delivery.Envelope.TenantId != tenant || delivery.Envelope.SchemaVersion != 1)
                        throw new InvalidOperationException("Notification tenant or schema is not configured.");
                    var applied = false;
                    for (var i = 0; i < 8 && !applied; i++)
                    {
                        var current = await store.GetAsync(tenant, delivery.Envelope.ConversationId, stoppingToken)
                            ?? throw new InvalidOperationException("Notification conversation is not registered.");
                        var value = Projection.Apply(current.Value, delivery.Envelope.Message, clock.GetUtcNow());
                        applied = value == current.Value || await store.SaveAsync(current with { Value = value }, stoppingToken);
                    }
                    if (!applied) throw new InvalidOperationException("Projection concurrency retry limit exceeded.");
                    await inbox.CompleteAsync(delivery.Receipt, stoppingToken);
                }
                foreach (var c in await store.ListAsync(tenant, stoppingToken))
                {
                    if (c.Messages.Any(m => m.CreatedAt < clock.GetUtcNow().AddDays(-7)) ||
                        (c.Tombstones ?? []).Any(t => t.At < clock.GetUtcNow().AddDays(-14)))
                    {
                        var retained = await store.GetAsync(tenant, c.Id, stoppingToken);
                        if (retained is not null)
                            await store.SaveAsync(retained with
                            {
                                Value = retained.Value with
                                {
                                    Messages = retained.Value.Messages.Where(m => m.CreatedAt >= clock.GetUtcNow().AddDays(-7)).ToList(),
                                    Tombstones = (retained.Value.Tombstones ?? []).Where(t => t.At >= clock.GetUtcNow().AddDays(-14)).ToList(),
                                    Changes = retained.Value.Changes.Where(change => change.At >= clock.GetUtcNow().AddDays(-14) ||
                                        change.Revision > retained.Value.PublishedRevision).ToList()
                                }
                            }, stoppingToken);
                    }
                    if (c.Revision <= c.PublishedRevision) continue;
                    var current = await store.GetAsync(tenant, c.Id, stoppingToken);
                    if (current is null) continue;
                    await realtime.PublishAsync(current.Value, stoppingToken);
                    await store.SaveAsync(current with { Value = current.Value with { PublishedRevision = current.Value.Revision } }, stoppingToken);
                }
                await Task.Delay(configuration["App:Store"] == "Memory" ? 250 : 3000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Projection/delivery iteration failed: {ErrorType}. Receipt owned: {HasReceipt}",
                    ex.GetType().Name, delivery is not null);
                if (delivery is not null)
                {
                    try { await inbox.AbandonAsync(delivery.Receipt, stoppingToken); }
                    catch (Exception abandonError) when (abandonError is not OperationCanceledException)
                    {
                        logger.LogError("Notification abandonment failed: {ErrorType}; broker lock expiry will recover it.", abandonError.GetType().Name);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }
}
