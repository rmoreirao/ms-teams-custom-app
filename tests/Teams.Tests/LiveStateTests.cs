using Microsoft.AspNetCore.DataProtection;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class LiveStateTests
{
    private readonly Clock clock = new();
    private readonly MemoryPartitionDocuments documents = new();
    private readonly LiveStateStore store;
    private readonly Actor actor = new("tenant", "alex", "Alex");
    public LiveStateTests() => store = new(documents, clock);
    private async Task Prepare()
    {
        await store.CreateAsync(new("chat", "tenant", "Approved", "chat", null, [], [], [],
            GraphChatId: "graph-chat"), default);
        await store.SetEntitlementAsync(new("tenant", "chat", "alex", "Alex", clock.GetUtcNow().AddDays(-7),
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), "authority-1", true, true, 0), default);
    }
    private Message Message(string id, string body = "Content", DateTimeOffset? at = null) =>
        new(id, actor.Id, actor.Name, body, at ?? clock.GetUtcNow(), clock.GetUtcNow(), SourceVersion: $"version-{body}");
    [Fact]
    public async Task ProjectionAndOutboxAreAtomicAndDuplicatesDoNotIncrementRevision()
    {
        await Prepare();
        var message = Message("one");
        await store.ApplyAsync("tenant", "chat", message, default);
        await store.ApplyAsync("tenant", "chat", message, default);
        Assert.Equal(1, (await store.GetAsync("tenant", "chat", default))!.Value.Revision);
        var change = Assert.Single(await store.PendingOutboxAsync("tenant", default));
        Assert.Equal(1, change.Revision);
        await store.CompleteOutboxAsync(change, default);
        Assert.Empty(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task DeleteTombstonePreventsResurrectionAfterMessageEviction()
    {
        await Prepare();
        var message = Message("one");
        await store.ApplyAsync("tenant", "chat", message with { Deleted = true, Body = "", SourceVersion = "deleted" }, default);
        await store.ApplyAsync("tenant", "chat", message with { ModifiedAt = clock.GetUtcNow().AddHours(1) }, default);
        Assert.True((await store.MessageAsync("tenant", "chat", "one", null, default))!.Deleted);
        Assert.Single(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task ExpiredAuthorityDoesNotBecomeFreshWhenRead()
    {
        await Prepare();
        await store.GetMemberAsync(actor, "chat", false, default);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("authorization_stale", (await Assert.ThrowsAsync<AppError>(() =>
            store.GetMemberAsync(actor, "chat", false, default))).Code);
    }
    [Fact]
    public async Task EqualTimestampMessagesPageWithoutAggregateLimit()
    {
        await Prepare();
        for (var i = 0; i < 250; i++) await store.ApplyAsync("tenant", "chat", Message($"{i:D4}"), default);
        var messaging = new Messaging(store, new LiveTransport(), new MemoryBudget(clock),
            new CommandTokens(new EphemeralDataProtectionProvider(), clock), clock);
        List<string> ids = [];
        string? cursor = null;
        do
        {
            var page = await messaging.ReadAsync(actor, "chat", cursor, null, 0, default);
            ids.AddRange(page.Messages.Select(m => m.Id));
            cursor = page.Cursor;
        } while (cursor is not null);
        Assert.Equal(250, ids.Count);
        Assert.Equal(250, ids.Distinct().Count());
        Assert.Empty((await store.GetAsync("tenant", "chat", default))!.Value.Messages);
    }
    [Fact]
    public async Task ConcurrentConfirmationHasOneOutboxAndBodyFreeOperation()
    {
        await Prepare();
        var messaging = new Messaging(store, new LiveTransport(), new MemoryBudget(clock),
            new CommandTokens(new EphemeralDataProtectionProvider(), clock), clock);
        var page = await messaging.ReadAsync(actor, "chat", null, null, 0, default);
        var command = new SendCommand(Guid.NewGuid().ToString(), page.Window, "Body", null);
        var result = await messaging.SendAsync(actor, "chat", command, default);
        var replay = await messaging.SendAsync(actor, "chat", command, default);
        Assert.Equal(OperationState.Confirmed, result.State);
        Assert.Equal(result, replay);
        Assert.Equal("", (await store.GetOperationAsync("tenant", "chat", command.OperationId, default))!.Value.Body);
        Assert.Single(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task ConflictingOpaqueVersionsRequireRepairRatherThanGuessedOrdering()
    {
        await Prepare();
        await store.ApplyAsync("tenant", "chat", Message("one", "first"), default);
        await store.ApplyAsync("tenant", "chat", Message("one", "second"), default);
        Assert.False((await store.GetAsync("tenant", "chat", default))!.Value.CoverageReady);
        Assert.Equal("first", (await store.MessageAsync("tenant", "chat", "one", null, default))!.Body);
    }
    [Fact]
    public async Task PartitionBatchRejectsCrossConversationAndRollsBackConflicts()
    {
        var first = new PartitionDocument("a", "pk", "test", "tenant", "", "", clock.GetUtcNow(), 0, -1, "{}");
        Assert.True(await documents.WriteAsync([new(first, null)], default));
        Assert.False(await documents.WriteAsync([new(first with { Json = "changed" }, "wrong"), new(first with { Id = "b" }, null)], default));
        Assert.Null(await documents.ReadAsync("pk", "b", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.WriteAsync([
            new(first, "wrong"), new(first with { Pk = "other" }, null)], default));
    }
    private sealed class LiveTransport : IGraphTransport
    {
        public bool Simulated => false;
        public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            Task.FromResult(new WriteResult(OperationState.Confirmed, new(operation.Id, actor.Id, actor.Name, operation.Body,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            Task.FromResult<WriteResult?>(null);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
