using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class OutboxDeliveryTests
{
    private readonly Clock clock = new();
    private readonly MemoryPartitionDocuments documents = new();
    private readonly LiveStateStore store;
    private readonly Queue queue = new();
    private readonly Realtime realtime = new();
    private readonly LiveOptions options = new() { Resources = [new("chat", "chat", "Chat", null, "graph", null, null)] };
    public OutboxDeliveryTests() => store = new(documents, clock);
    private DeliveryHandler Handler(IRealtime? gateway = null) => new("tenant", options, store, gateway ?? realtime,
        queue, clock, NullLogger<DeliveryHandler>.Instance);
    private async Task<OutboxChange> Prepare(int members = 1)
    {
        await store.CreateAsync(new("chat", "tenant", "Chat", "chat", null, [], [], [], CoverageUntil: clock.GetUtcNow().AddMinutes(5)), default);
        for (var i = 0; i < members; i++)
            await store.SetEntitlementAsync(new("tenant", "chat", $"actor-{i}", "Member", clock.GetUtcNow().AddDays(-7),
                clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5), "source", true, true, 0), default);
        await store.ApplyAsync("tenant", "chat", new("message", "actor-0", "Member", "Body",
            clock.GetUtcNow(), clock.GetUtcNow()), default);
        return Assert.Single(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task LargeFanoutUsesDurableBoundedPagesAndCompletesAtomically()
    {
        var change = await Prepare(250);
        var handler = Handler();
        await handler.HandleAsync(new(change), default);
        Assert.Equal(100, realtime.Users.Count);
        Assert.Single(await store.PendingOutboxAsync("tenant", default));
        Assert.Equal(1, (await store.DeliveryAsync(change, default)).Value.Page);
        await handler.HandleAsync(queue.Items.Dequeue(), default);
        Assert.Equal(200, realtime.Users.Count);
        await handler.HandleAsync(queue.Items.Dequeue(), default);
        Assert.Equal(250, realtime.Users.Distinct().Count());
        Assert.Empty(await store.PendingOutboxAsync("tenant", default));
        Assert.True((await store.DeliveryAsync(change, default)).Value.Complete);
        await handler.HandleAsync(new(change), default);
        Assert.Equal(250, realtime.Users.Count);
    }
    [Fact]
    public async Task LostContinuationEnqueueRecoversWithoutRepeatingCompletedPage()
    {
        var change = await Prepare(150);
        queue.FailOnce = true;
        var handler = Handler();
        await Assert.ThrowsAsync<HttpRequestException>(() => handler.HandleAsync(new(change), default));
        Assert.Equal(100, realtime.Users.Count);
        Assert.Equal(1, (await store.DeliveryAsync(change, default)).Value.Page);
        await handler.HandleAsync(new(change), default);
        Assert.Equal(100, realtime.Users.Count);
        await handler.HandleAsync(queue.Items.Dequeue(), default);
        Assert.Equal(150, realtime.Users.Count);
        Assert.Empty(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task ConcurrentConsumersCannotAdvanceTheSamePage()
    {
        var change = await Prepare();
        var gateway = new BlockingRealtime();
        var handler = Handler(gateway);
        var first = handler.HandleAsync(new(change), default);
        await gateway.Entered.Task;
        await Assert.ThrowsAsync<DeliveryDeferredException>(() => handler.HandleAsync(new(change), default));
        gateway.Release.SetResult();
        await first;
        Assert.Empty(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task FailedPublishRetainsCursorAndReleasesClaimForRetry()
    {
        var change = await Prepare();
        var handler = Handler(new FailingRealtime());
        await Assert.ThrowsAsync<HttpRequestException>(() => handler.HandleAsync(new(change), default));
        var progress = (await store.DeliveryAsync(change, default)).Value;
        Assert.Equal(0, progress.Page);
        Assert.Equal("", progress.Owner);
        Assert.False(progress.Complete);
        await Handler().HandleAsync(new(change), default);
        Assert.Single(realtime.Users);
    }
    [Fact]
    public async Task ExpiredOrRevokedRecipientsAreNeverPublished()
    {
        var change = await Prepare(2);
        await store.SetBlockedAsync("tenant", "actor-0", true, default);
        clock.Advance(TimeSpan.FromMinutes(6));
        await Handler().HandleAsync(new(change), default);
        Assert.Empty(realtime.Users);
        Assert.Empty(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task ForgedFuturePageAndWrongIntentAreRejected()
    {
        var change = await Prepare();
        await Assert.ThrowsAsync<AppError>(() => Handler().HandleAsync(new(change, Page: 2), default));
        await Assert.ThrowsAsync<AppError>(() => Handler().HandleAsync(new(change with { Revision = 999 }), default));
        Assert.Empty(realtime.Users);
        Assert.Single(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task CompletedRevisionCoalescesOtherPendingChangesWithoutRepeatingFanout()
    {
        var first = await Prepare();
        await store.ApplyAsync("tenant", "chat", new("second", "actor-0", "Member", "Second",
            clock.GetUtcNow(), clock.GetUtcNow()), default);
        var second = (await store.PendingOutboxAsync("tenant", default)).Single(c => c.Revision == 2);
        await Handler().HandleAsync(new(first), default);
        Assert.Equal(2, (await store.GetAsync("tenant", "chat", default))!.Value.PublishedRevision);
        await Handler().HandleAsync(new(second), default);
        Assert.Single(realtime.Users);
        Assert.Empty(await store.PendingOutboxAsync("tenant", default));
    }
    [Fact]
    public async Task ExpiredDeliveryClaimCanResumeAndOldClaimCannotOverwriteCompletion()
    {
        var change = await Prepare();
        var initial = await store.DeliveryAsync(change, default);
        Assert.True(await store.SaveDeliveryAsync(change, initial,
            initial.Value with { Owner = "crashed", LeaseUntil = clock.GetUtcNow().AddSeconds(30) }, default));
        var stale = await store.DeliveryAsync(change, default);
        await Assert.ThrowsAsync<DeliveryDeferredException>(() => Handler().HandleAsync(new(change), default));
        clock.Advance(TimeSpan.FromSeconds(31));
        await Handler().HandleAsync(new(change), default);
        Assert.False(await store.SaveDeliveryAsync(change, stale, stale.Value with { Page = 1 }, default));
        Assert.Single(realtime.Users);
        Assert.True((await store.DeliveryAsync(change, default)).Value.Complete);
    }
    [Fact]
    public async Task FeedDispatchRequiresDurableQueueAcceptanceAndFiltersOtherDocuments()
    {
        await Prepare();
        var page = await documents.QueryAsync(new("tenant", "outbox"), 10, null, default);
        var dispatcher = new OutboxDispatcher("tenant", options, queue, NullLogger<OutboxDispatcher>.Instance);
        queue.FailOnce = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(page.Items.Select(v => v.Value).ToArray(), default));
        Assert.Empty(queue.Items);
        await dispatcher.DispatchAsync(page.Items.Select(v => v.Value).ToArray(), default);
        Assert.Single(queue.Items);
        await dispatcher.DispatchAsync(page.Items.Select(v => v.Value with { Kind = "message" }).ToArray(), default);
        Assert.Single(queue.Items);
    }
    private sealed class Queue : IDeliveryWorkQueue
    {
        public Queue<DeliveryWork> Items { get; } = new();
        public bool FailOnce { get; set; }
        public Task EnqueueAsync(DeliveryWork work, DateTimeOffset? scheduledAt, CancellationToken ct)
        {
            if (FailOnce) { FailOnce = false; throw new HttpRequestException("Synthetic enqueue loss"); }
            Items.Enqueue(work);
            return Task.CompletedTask;
        }
    }
    private sealed class Realtime : IRealtime
    {
        public List<string> Users { get; } = [];
        public Task PublishAsync(Conversation conversation, CancellationToken ct)
        {
            Users.AddRange(conversation.Members.Select(m => m.Id));
            return Task.CompletedTask;
        }
        public Task RevokeAsync(string tenant, string actorId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class BlockingRealtime : IRealtime
    {
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource Release { get; } = new();
        public async Task PublishAsync(Conversation conversation, CancellationToken ct) { Entered.SetResult(); await Release.Task; }
        public Task RevokeAsync(string tenant, string actorId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class FailingRealtime : IRealtime
    {
        public Task PublishAsync(Conversation conversation, CancellationToken ct) => throw new HttpRequestException("Synthetic publication failure");
        public Task RevokeAsync(string tenant, string actorId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
