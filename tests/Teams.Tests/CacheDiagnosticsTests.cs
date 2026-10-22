using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class CacheDiagnosticsTests
{
    [Fact]
    public async Task InspectionDoesNotWarmPagesAndMissingRedisIsNotReportedAsEmpty()
    {
        var fixture = new LiveFixture();
        var snapshot = await fixture.Store.InspectCacheAsync(fixture.Alex, "chat-handover", default);
        Assert.Equal(45, snapshot.CosmosMessages);
        Assert.True(snapshot.CosmosCountComplete);
        Assert.Equal("not-configured", snapshot.Redis.Status);
        Assert.Null(snapshot.Redis.Pages);
        Assert.Null(snapshot.Cleanup);
        var cache = new MemoryRecentMessageCache();
        fixture = new(cache: cache);
        snapshot = await fixture.Store.InspectCacheAsync(fixture.Alex, "chat-handover", default);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, cache.Writes);
        Assert.Equal(0, snapshot.Redis.Pages);
        cache.Unavailable = true;
        snapshot = await fixture.Store.InspectCacheAsync(fixture.Alex, "chat-handover", default);
        Assert.Equal("unavailable", snapshot.Redis.Status);
        Assert.Null(snapshot.Redis.Pages);
    }
    [Fact]
    public async Task PageReadsDistinguishMissHitUnavailableAndEmptyHitWhileDeltaNeverConsultsRedis()
    {
        var cache = new MemoryRecentMessageCache();
        var fixture = new LiveFixture(cache: cache);
        async Task<ReadEvidence> Read(string id)
        {
            var evidence = new ReadEvidence("page", fixture.Clock.GetUtcNow());
            await fixture.Store.MessagesAsync(fixture.Alex, id, null, fixture.Clock.GetUtcNow().AddDays(-7), null, null, default, evidence);
            return evidence;
        }
        var miss = await Read("chat-handover");
        Assert.Contains(miss.Steps, step => step.Layer == "redis" && step.Outcome == "miss");
        Assert.Contains(miss.Steps, step => step.Layer == "cosmos" && step.Outcome == "content-query");
        var hit = await Read("chat-handover");
        Assert.Contains(hit.Steps, step => step.Layer == "redis" && step.Outcome == "hit");
        Assert.DoesNotContain(hit.Steps, step => step.Layer == "cosmos");
        await Read("chat-empty");
        Assert.Contains((await Read("chat-empty")).Steps, step => step.Outcome == "hit" && step.Count == 0);
        cache.Unavailable = true;
        Assert.Contains((await Read("chat-handover")).Steps, step => step.Outcome == "unavailable");
        var reads = cache.Reads;
        var delta = new ReadEvidence("delta", fixture.Clock.GetUtcNow());
        await fixture.Store.ChangesAsync(fixture.Alex, "chat-handover", 44, default, delta);
        Assert.Equal(reads, cache.Reads);
        Assert.All(delta.Steps, step => Assert.Equal("cosmos", step.Layer));
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndependentCleanupPreservesOtherChatsAuthorityOperationsTombstonesAndWriterEpoch(bool cosmos, bool redis)
    {
        var documents = new MemoryPartitionDocuments();
        var cache = new MemoryRecentMessageCache();
        var fixture = new LiveFixture(documents: documents, cache: cache);
        var store = fixture.Store;
        var now = fixture.Clock.GetUtcNow();
        var op = new SendOperation(Guid.NewGuid().ToString(), fixture.Tenant, "chat-handover", fixture.Alex.Id, "fingerprint", "",
            null, OperationState.Unknown, now, Epoch: 1);
        Assert.True(await store.CreateOperationAsync(op, default));
        await store.ApplyAsync(fixture.Tenant, "chat-handover", new("deleted", fixture.Sam.Id, "Sam", "", now, now, Deleted: true), default);
        await store.MessagesAsync(fixture.Alex, "chat-handover", null, now.AddDays(-7), null, null, default);
        await store.MessagesAsync(fixture.Alex, "chat-empty", null, now.AddDays(-7), null, null, default);
        var before = await store.InspectCacheAsync(fixture.Alex, "chat-handover", default);
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), cosmos, redis);
        var result = await store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.True(result.Complete);
        Assert.Equal(cosmos ? 46 : 0, result.RemovedMessages);
        Assert.Equal(redis ? 1 : 0, result.RemovedPages);
        var snapshot = await store.InspectCacheAsync(fixture.Alex, "chat-handover", default);
        Assert.Equal(cosmos ? 0 : 46, snapshot.CosmosMessages);
        Assert.Equal(redis ? 0 : 1, snapshot.Redis.Pages);
        Assert.Equal(1, (await store.InspectCacheAsync(fixture.Alex, "chat-empty", default)).Redis.Pages);
        Assert.Equal(op, (await store.GetOperationAsync(fixture.Tenant, "chat-handover", op.Id, default))!.Value);
        Assert.NotNull(await store.GetMemberAsync(fixture.Sam, "chat-handover", false, default));
        Assert.Single((await documents.QueryAsync(new(fixture.Tenant, "tombstone", ConversationId: "chat-handover"), 100, null, default)).Items);
        Assert.NotNull(await store.CurrentMessageAsync(fixture.Tenant, "channel-general", "root", null, default));
        var metadata = (await store.GetAsync(fixture.Tenant, "chat-handover", default))!.Value;
        Assert.Equal(1, metadata.Epoch);
        Assert.Null(metadata.ActiveCacheCleanup);
        if (cosmos)
        {
            Assert.True(metadata.Revision > before.Revision);
            Assert.False(snapshot.CoverageComplete);
            Assert.False(snapshot.ReadViewComplete);
            Assert.True((await store.ChangesAsync(fixture.Alex, "chat-handover", before.Revision, default)).CacheReset);
            var trace = new ReadEvidence("page", now);
            Assert.Empty(await store.MessagesAsync(fixture.Alex, "chat-handover", null, now.AddDays(-7), null, null, default, trace));
            Assert.Contains(trace.Steps, step => step.Outcome == "miss");
            await Assert.ThrowsAsync<AppError>(() => store.SaveReadViewAsync(fixture.Tenant, "chat-handover", null, null,
                new(1, true, null, now), default));
        }
        else Assert.Equal(before.Revision, metadata.Revision);
        Assert.Equal(result, await store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default));
    }
    [Fact]
    public async Task CleanupIsBoundedResumableAndDoesNotDeletePostStartRefills()
    {
        var fixture = new LiveFixture();
        var now = fixture.Clock.GetUtcNow();
        for (var index = 0; index < 200; index++)
            await fixture.Store.ApplyAsync(fixture.Tenant, "chat-handover", new($"extra-{index}", "sam", "Sam", "extra", now, now), default);
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, false);
        var first = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.False(first.Complete);
        Assert.Equal(200, first.RemovedMessages);
        await fixture.Store.ApplyAsync(fixture.Tenant, "chat-handover", new("new-refill", "sam", "Sam", "refilled", now, now), default);
        var final = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.True(final.Complete);
        Assert.Equal(245, final.RemovedMessages);
        Assert.Equal("refilled", (await fixture.Store.CurrentMessageAsync(fixture.Tenant, "chat-handover", "new-refill", null, default))!.Body);
    }
    [Theory]
    [InlineData("empty-delta")]
    [InlineData("change-query")]
    [InlineData("content-read")]
    public async Task ConcurrentCleanupResetsDeltaEvenWhenThereAreNoChangesOrContentWasDeleted(string interruption)
    {
        var documents = new InterceptingDocuments();
        var fixture = new LiveFixture(documents: documents);
        var revision = (await fixture.Store.GetAsync(fixture.Tenant, "chat-handover", default))!.Value.Revision;
        async Task Clean(CancellationToken ct)
        {
            documents.AfterQuery = null; documents.AfterRead = null;
            var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, false);
            Assert.True((await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, ct)).Complete);
        }
        if (interruption == "content-read")
            documents.AfterRead = async (id, ct) => { if (id.StartsWith("message:", StringComparison.Ordinal)) await Clean(ct); };
        else documents.AfterQuery = async (filter, ct) => { if (filter.Kind == "change") await Clean(ct); };
        var delta = await fixture.Store.ChangesAsync(fixture.Alex, "chat-handover",
            interruption == "empty-delta" ? revision : revision - 1, default);
        Assert.True(delta.CacheReset);
        Assert.True(delta.ResetRequired);
        Assert.Empty(delta.Changes);
        Assert.Empty(delta.Messages!);
        Assert.Equal((await fixture.Store.GetAsync(fixture.Tenant, "chat-handover", default))!.Value.Revision, delta.Revision);
    }
    [Fact]
    public async Task ConcurrentProjectionRejectsTheWholeDeleteBatchAndResumePreservesTheNewerMessage()
    {
        var documents = new InterceptingDocuments();
        var fixture = new LiveFixture(documents: documents);
        var original = (await fixture.Store.CurrentMessageAsync(fixture.Tenant, "chat-handover", "message-00", null, default))!;
        documents.AfterQuery = async (filter, ct) =>
        {
            if (filter.Kind != "message" || filter.ThroughRevision is null) return;
            documents.AfterQuery = null;
            await fixture.Store.ApplyAsync(fixture.Tenant, "chat-handover",
                original with { Body = "Concurrent content", ModifiedAt = original.ModifiedAt.AddSeconds(1), SourceVersion = "concurrent" }, ct);
        };
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, false);
        var rejected = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.Equal("failed", rejected.CosmosState);
        Assert.Equal("cache_cleanup_conflict", rejected.CosmosError);
        Assert.Equal(0, rejected.RemovedMessages);
        Assert.Equal(45, (await fixture.Store.InspectCacheAsync(fixture.Alex, "chat-handover", default)).CosmosMessages);
        var resumed = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.True(resumed.Complete);
        Assert.Equal(44, resumed.RemovedMessages);
        Assert.Equal("Concurrent content", (await fixture.Store.CurrentMessageAsync(fixture.Tenant, "chat-handover", original.Id, null, default))!.Body);
    }
    [Fact]
    public async Task RedisFailureIsPartialAndOriginalRetryDoesNotClearRefilledCosmosAgain()
    {
        var cache = new MemoryRecentMessageCache { FailEvictions = true };
        var fixture = new LiveFixture(cache: cache);
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, true);
        var first = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.Equal("completed", first.CosmosState);
        Assert.Equal("failed", first.RedisState);
        Assert.Equal("cache_eviction_unavailable", first.RedisError);
        Assert.Equal(first, await fixture.Store.CacheCleanupAsync(fixture.Alex, "chat-handover", command.OperationId, default));
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ApplyAsync(fixture.Tenant, "chat-handover", new("refill", "sam", "Sam", "kept", now, now), default);
        cache.FailEvictions = false;
        var resumed = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default);
        Assert.True(resumed.Complete);
        Assert.Equal(45, resumed.RemovedMessages);
        Assert.NotNull(await fixture.Store.CurrentMessageAsync(fixture.Tenant, "chat-handover", "refill", null, default));
        await Assert.ThrowsAsync<AppError>(() => fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command with { Redis = false }, default));
    }
    [Fact]
    public async Task ExpiredAbandonedCleanupCanBeReplacedButCannotBeResumedAgainstNewRefills()
    {
        var fixture = new LiveFixture();
        var now = fixture.Clock.GetUtcNow();
        for (var index = 0; index < 200; index++)
            await fixture.Store.ApplyAsync(fixture.Tenant, "chat-handover", new($"extra-{index}", "sam", "Sam", "extra", now, now), default);
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, false);
        Assert.False((await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default)).Complete);
        fixture.Clock.Advance(TimeSpan.FromSeconds(CacheCleanup.RetentionSeconds + 1));
        await fixture.RenewAsync();
        Assert.Null((await fixture.Store.InspectCacheAsync(fixture.Alex, "chat-handover", default)).Cleanup);
        Assert.Equal("cache_operation_expired", (await Assert.ThrowsAsync<AppError>(() =>
            fixture.Store.CacheCleanupAsync(fixture.Alex, "chat-handover", command.OperationId, default))).Code);
        Assert.Equal("cache_operation_expired", (await Assert.ThrowsAsync<AppError>(() =>
            fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default))).Code);
        var replacement = await fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover",
            command with { OperationId = Guid.NewGuid().ToString() }, default);
        Assert.True(replacement.Complete);
        Assert.Equal(45, replacement.RemovedMessages);
        Assert.Null((await fixture.Store.GetAsync(fixture.Tenant, "chat-handover", default))!.Value.ActiveCacheCleanup);
    }
    [Fact]
    public async Task UnauthorizedChannelsAndInvalidSelectionsNeverCleanData()
    {
        var fixture = new LiveFixture();
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, false);
        foreach (var actor in new[] { fixture.Jordan, fixture.Alex with { TenantId = "other" } })
        {
            await Assert.ThrowsAsync<AppError>(() => fixture.Store.InspectCacheAsync(actor, "chat-handover", default));
            await Assert.ThrowsAsync<AppError>(() => fixture.Store.CleanCacheAsync(actor, "chat-handover", command, default));
        }
        await Assert.ThrowsAsync<AppError>(() => fixture.Store.InspectCacheAsync(fixture.Alex, "channel-general", default));
        await Assert.ThrowsAsync<AppError>(() => fixture.Store.CleanCacheAsync(fixture.Alex, "channel-general", command, default));
        await Assert.ThrowsAsync<AppError>(() => fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command with { Cosmos = false }, default));
        await fixture.Store.SetBlockedAsync(fixture.Tenant, fixture.Alex.Id, true, default);
        await Assert.ThrowsAsync<AppError>(() => fixture.Store.CleanCacheAsync(fixture.Alex, "chat-handover", command, default));
        Assert.NotNull(await fixture.Store.CurrentMessageAsync(fixture.Tenant, "chat-handover", "message-00", null, default));
    }
    private sealed class InterceptingDocuments : IPartitionDocuments
    {
        private readonly MemoryPartitionDocuments inner = new();
        public Func<DocumentFilter, CancellationToken, Task>? AfterQuery { get; set; }
        public Func<string, CancellationToken, Task>? AfterRead { get; set; }
        public Task<Versioned<PartitionDocument>?> ReadCurrentAsync(string pk, string id, CancellationToken ct) =>
            inner.ReadCurrentAsync(pk, id, ct);
        public async Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct)
        {
            var result = await inner.ReadAsync(pk, id, ct);
            if (AfterRead is { } action) await action(id, ct);
            return result;
        }
        public async Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct)
        {
            var result = await inner.QueryAsync(filter, limit, cursor, ct);
            if (AfterQuery is { } action) await action(filter, ct);
            return result;
        }
        public Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct) => inner.WriteAsync(writes, ct);
        public Task<bool> MutateAsync(IReadOnlyList<DocumentWrite> writes, IReadOnlyList<DocumentDelete> deletes, CancellationToken ct) =>
            inner.MutateAsync(writes, deletes, ct);
    }
}
