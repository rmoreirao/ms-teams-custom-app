using Microsoft.Azure.Cosmos;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed partial class LiveStateStore
{
    private bool CleanupExpired(CacheCleanup cleanup) =>
        !cleanup.Complete && cleanup.StartedAt.AddSeconds(CacheCleanup.RetentionSeconds) <= Now;
    private static void EnsureCacheReadable(Conversation conversation)
    {
        if (conversation.ActiveCacheCleanup is not null)
            throw new AppError(503, "cache_cleanup_in_progress", "Shared cache cleanup is in progress. Check or resume its original operation.");
    }
    private async Task<Versioned<Conversation>> DiagnosticChatAsync(Actor actor, string id, CancellationToken ct)
    {
        await GetMemberAsync(actor, id, false, ct);
        var conversation = await GetAsync(actor.TenantId, id, ct)
            ?? throw new AppError(404, "access_denied", "Conversation is not available.");
        if (conversation.Value.Kind != "chat")
            throw new AppError(400, "cache_chat_required", "Cache Diagnostics is available for chats, not channels.");
        return conversation;
    }
    private async Task<Versioned<CacheCleanup>?> CleanupRecordAsync(string tenant, string id, string operationId, CancellationToken ct)
    {
        var record = await documents.ReadCurrentAsync(Partition(tenant, id), $"cleanup:{operationId}", ct);
        return record is null ? null : new(Decode<CacheCleanup>(record.Value), record.Version);
    }
    private PartitionDocument CleanupDocument(string tenant, string id, CacheCleanup cleanup) =>
        Wrap($"cleanup:{cleanup.Id}", Partition(tenant, id), "cache-cleanup", tenant, id, cleanup, CacheCleanup.RetentionSeconds);
    private DocumentWrite ResetOutbox(Conversation conversation)
    {
        var change = new OutboxChange(Guid.NewGuid().ToString("N"), conversation.TenantId, conversation.Id, conversation.Revision, Now);
        return new(Wrap($"outbox:{change.Id}", Partition(conversation.TenantId, conversation.Id), "outbox",
            conversation.TenantId, conversation.Id, change, 14 * 86400, revision: change.Revision, pending: true), null);
    }
    public async Task<CacheSnapshot> InspectCacheAsync(Actor actor, string conversation, CancellationToken ct)
    {
        var metadata = (await DiagnosticChatAsync(actor, conversation, ct)).Value;
        int? count = 0;
        var complete = false;
        string? error = null;
        string? cursor = null;
        try
        {
            for (var page = 0; page < 10; page++)
            {
                var result = await documents.QueryAsync(new(actor.TenantId, "message", Partition(actor.TenantId, conversation),
                    ConversationId: conversation), 100, cursor, ct);
                count += result.Items.Count;
                cursor = result.Cursor;
                if (cursor is null) { complete = true; break; }
            }
        }
        catch (Exception failure) when (failure is CosmosException or AppError or TimeoutException)
        {
            error = failure is AppError app ? app.Code : "cosmos_inspection_unavailable";
            count = null;
        }
        var view = await ReadViewAsync(actor.TenantId, conversation, null, ct);
        var coverage = await ReadCoverageAsync(actor.TenantId, conversation, ct);
        var redis = cache is null ? new RedisCacheSnapshot("not-configured", null, null, false)
            : await cache.InspectAsync(CacheScope(actor.TenantId, conversation), ct);
        var cleanupId = metadata.ActiveCacheCleanup ?? metadata.LastCacheCleanup;
        var cleanup = cleanupId is null ? null : await CleanupRecordAsync(actor.TenantId, conversation, cleanupId, ct);
        await GetMemberAsync(actor, conversation, false, ct);
        return new(Now, metadata.Revision, metadata.CacheGeneration, count, complete,
            metadata.ActiveCacheCleanup is null && (metadata.CoverageReady || coverage?.Ready == true),
            metadata.ActiveCacheCleanup is null && view?.Value.CacheGeneration == metadata.CacheGeneration &&
                view.Value.Epoch == metadata.Epoch && view.Value.Complete,
            redis, cleanup is not null && !CleanupExpired(cleanup.Value) ? cleanup.Value : null, error);
    }
    public async Task<CacheCleanup> CacheCleanupAsync(Actor actor, string conversation, string operationId, CancellationToken ct)
    {
        await DiagnosticChatAsync(actor, conversation, ct);
        if (!Guid.TryParseExact(operationId, "D", out _))
            throw new AppError(400, "cache_operation_invalid", "Use the original cleanup operation ID.");
        var cleanup = (await CleanupRecordAsync(actor.TenantId, conversation, operationId, ct))?.Value
            ?? throw new AppError(404, "cache_operation_missing", "The cleanup operation was not found or its retention expired.");
        if (CleanupExpired(cleanup))
            throw new AppError(410, "cache_operation_expired", "This cleanup operation expired. Inspect storage before starting another operation.");
        return cleanup;
    }
    public async Task<CacheCleanup> CleanCacheAsync(Actor actor, string conversation, CacheCleanupCommand command, CancellationToken ct)
    {
        if (!Guid.TryParseExact(command.OperationId, "D", out _) || !command.Cosmos && !command.Redis)
            throw new AppError(400, "cache_cleanup_invalid", "Select Cosmos and/or Redis and use a valid cleanup operation ID.");
        var metadata = await DiagnosticChatAsync(actor, conversation, ct);
        var pk = Partition(actor.TenantId, conversation);
        var existing = await CleanupRecordAsync(actor.TenantId, conversation, command.OperationId, ct);
        if (existing is null)
        {
            if (command.Cosmos && metadata.Value.ActiveCacheCleanup is { } active)
            {
                var activeCleanup = await CleanupRecordAsync(actor.TenantId, conversation, active, ct);
                if (activeCleanup is not null && !CleanupExpired(activeCleanup.Value))
                    throw new AppError(409, "cache_cleanup_busy", "Resume the active cleanup before starting another Cosmos cleanup.");
            }
            var cleanup = new CacheCleanup(command.OperationId, command.Cosmos, command.Redis, metadata.Value.Revision, Now,
                command.Cosmos ? "in-progress" : "not-selected", command.Redis ? "pending" : "not-selected");
            List<DocumentWrite> writes = [new(CleanupDocument(actor.TenantId, conversation, cleanup), null)];
            if (command.Cosmos)
            {
                var next = metadata.Value with
                {
                    CacheGeneration = metadata.Value.CacheGeneration + 1, ActiveCacheCleanup = cleanup.Id, LastCacheCleanup = cleanup.Id,
                    Revision = metadata.Value.Revision + 1, CacheResetRevision = metadata.Value.Revision + 1,
                    CoverageReady = false, CoverageUntil = null, RepairGeneration = metadata.Value.RepairGeneration + 1
                };
                var coverage = await documents.ReadCurrentAsync(pk, "coverage", ct);
                var view = await documents.ReadCurrentAsync(pk, ReadViewId(null), ct);
                writes.Add(new(Wrap("conversation", pk, "conversation", actor.TenantId, conversation, next), metadata.Version));
                writes.Add(new(Wrap("coverage", pk, "coverage", actor.TenantId, conversation,
                    new Coverage(actor.TenantId, conversation, false, null, null, "cache_cleanup",
                        RepairGeneration: next.RepairGeneration)), coverage?.Version));
                writes.Add(new(Wrap(ReadViewId(null), pk, "read-view", actor.TenantId, conversation,
                    new ReadViewState(next.Epoch, false, null, Now, CacheGeneration: next.CacheGeneration)), view?.Version));
                writes.Add(ResetOutbox(next));
            }
            else writes.Add(new(Wrap("conversation", pk, "conversation", actor.TenantId, conversation,
                metadata.Value with { LastCacheCleanup = cleanup.Id }), metadata.Version));
            if (!await documents.WriteAsync(writes, ct))
                throw new AppError(409, "cache_cleanup_conflict", "Cache state changed. Check the original operation before resuming.");
            LiveTelemetry.CacheCleanups.Add(1);
        }
        var status = (await CleanupRecordAsync(actor.TenantId, conversation, command.OperationId, ct))!.Value;
        if (status.Cosmos != command.Cosmos || status.Redis != command.Redis)
            throw new AppError(409, "cache_operation_conflict", "The original cleanup operation has different store selections.");
        if (status.Complete) return status;
        if (CleanupExpired(status))
            throw new AppError(410, "cache_operation_expired", "This cleanup operation expired. Inspect storage before starting another operation.");
        if (status.CosmosState is not ("completed" or "not-selected"))
        {
            try { await ProgressCosmosCleanupAsync(actor, conversation, command.OperationId, ct); }
            catch (Exception failure) when (failure is CosmosException or AppError or TimeoutException)
            {
                await SaveCleanupOutcomeAsync(actor.TenantId, conversation, command.OperationId,
                    value => value with { CosmosState = "failed", CosmosError = failure is AppError app ? app.Code : "cosmos_cleanup_unavailable" }, ct);
            }
        }
        status = (await CleanupRecordAsync(actor.TenantId, conversation, command.OperationId, ct))!.Value;
        if (status.RedisState is not ("completed" or "not-selected"))
        {
            try
            {
                await GetMemberAsync(actor, conversation, false, ct);
                if (cache is null) throw new AppError(503, "cache_not_configured", "Redis is not configured.");
                var removed = await cache.EvictAsync(CacheScope(actor.TenantId, conversation), command.OperationId, ct);
                await SaveCleanupOutcomeAsync(actor.TenantId, conversation, command.OperationId,
                    value => value with { RedisState = "completed", RemovedPages = removed, RedisError = null }, ct);
            }
            catch (AppError failure)
            {
                await SaveCleanupOutcomeAsync(actor.TenantId, conversation, command.OperationId,
                    value => value with { RedisState = "failed", RedisError = failure.Code }, ct);
            }
        }
        await DiagnosticChatAsync(actor, conversation, ct);
        return (await CleanupRecordAsync(actor.TenantId, conversation, command.OperationId, ct))!.Value;
    }
    private async Task SaveCleanupOutcomeAsync(string tenant, string conversation, string operationId,
        Func<CacheCleanup, CacheCleanup> update, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await CleanupRecordAsync(tenant, conversation, operationId, ct)
                ?? throw new AppError(404, "cache_operation_missing", "The original cleanup operation is unavailable.");
            if (current.Value.Complete) return;
            var next = update(current.Value);
            if (current.Value.CosmosState == "completed")
                next = next with { CosmosState = "completed", CosmosError = null };
            if (current.Value.RedisState == "completed")
                next = next with { RedisState = "completed", RedisError = null };
            if (await documents.WriteAsync([new(CleanupDocument(tenant, conversation, next), current.Version)], ct)) return;
        }
        throw new AppError(409, "cache_cleanup_conflict", "Cleanup progress changed. Check the original operation.");
    }
    private async Task ProgressCosmosCleanupAsync(Actor actor, string conversation, string operationId, CancellationToken ct)
    {
        var pk = Partition(actor.TenantId, conversation);
        for (var batch = 0; batch < 4; batch++)
        {
            var metadata = await DiagnosticChatAsync(actor, conversation, ct);
            var current = (await CleanupRecordAsync(actor.TenantId, conversation, operationId, ct))!;
            if (current.Value.CosmosState == "completed") return;
            if (metadata.Value.ActiveCacheCleanup != operationId)
                throw new AppError(409, "cache_cleanup_superseded", "A different cleanup owns this projection.");
            var page = await documents.QueryAsync(new(actor.TenantId, "message", pk, ConversationId: conversation,
                ThroughRevision: current.Value.TargetRevision), 50, null, ct);
            var next = current.Value with
            {
                RemovedMessages = current.Value.RemovedMessages + page.Items.Count, CosmosError = null,
                CosmosState = page.Items.Count == 0 ? "completed" : "in-progress"
            };
            var state = metadata.Value;
            List<DocumentWrite> writes = [new(CleanupDocument(actor.TenantId, conversation, next), current.Version)];
            if (page.Items.Count == 0)
            {
                state = state with { ActiveCacheCleanup = null, Revision = state.Revision + 1,
                    CacheResetRevision = state.Revision + 1, RepairGeneration = state.RepairGeneration + 1 };
                writes.Add(ResetOutbox(state));
            }
            writes.Add(new(Wrap("conversation", pk, "conversation", actor.TenantId, conversation, state), metadata.Version));
            var deletes = page.Items.Select(item => new DocumentDelete(pk, item.Value.Id, item.Version)).ToArray();
            if (!await documents.MutateAsync(writes, deletes, ct))
                throw new AppError(409, "cache_cleanup_conflict", "Concurrent projection work changed this batch. Resume the original operation.");
            if (page.Items.Count == 0) return;
        }
    }
}
