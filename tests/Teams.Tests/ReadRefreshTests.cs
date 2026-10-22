using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class ReadRefreshTests
{
    private readonly LiveFixture fixture = new();
    private readonly MemoryPartitionDocuments documents = new();
    private ICommandTokens? tokens;
    private LiveOptions Options => new()
    {
        WriterEpoch = 1, SynchronizationEnabled = true, AdmissionEnabled = true,
        Resources = [new("chat-empty", "chat", "Chat", null, "chat-empty", null, null),
            new("chat-handover", "chat", "Chat", null, "chat-handover", null, null),
            new("channel-general", "channel", "Channel", null, null, "team", "channel-general")]
    };
    private ReadRefreshRequest Request(string id = "chat-empty", string? parent = null) =>
        new(fixture.Tenant, id, 1, parent, null, null, fixture.Clock.GetUtcNow());
    private LiveControlStore Control => new(documents, fixture.Clock, fixture.Tenant);
    private ReadRefreshHandler Handler(Graph graph) => new(fixture.Tenant, Options, fixture.Store, Control, graph,
        fixture.Clock, NullLogger<ReadRefreshHandler>.Instance);
    private Messaging Messaging(IReadRefresh? queue = null) => new(fixture.Store, new GraphResultStub(fixture.Clock),
        new MemoryBudget(fixture.Clock), tokens ??= new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock), fixture.Clock, queue);
    private Task Cold(string id = "chat-empty") =>
        fixture.Store.SetCoverageAsync(new(fixture.Tenant, id, false, null, null, "source-gap"), default);
    private JsonElement Source(string id, string body = "Hydrated", DateTimeOffset? at = null, string version = "1") =>
        JsonSerializer.SerializeToElement(new { id, createdDateTime = at ?? fixture.Clock.GetUtcNow(),
            lastModifiedDateTime = at ?? fixture.Clock.GetUtcNow(), etag = version,
            body = new { content = body }, from = new { user = new { id = fixture.Sam.Id, displayName = "Sam" } } });
    private static JsonElement Page(JsonElement[] messages, string? next = null) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["value"] = messages, ["@odata.nextLink"] = next });

    [Fact]
    public async Task ExpiredSourceCoverageDoesNotHideHistoryOrShortenTheAuthorityLease()
    {
        await Cold("chat-handover");
        var queue = new Queue();
        var page = await Messaging(queue).ReadAsync(fixture.Alex, "chat-handover", null, null, 0, default);
        Assert.Equal(20, page.Messages.Count);
        Assert.False(page.Pending);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(5), page.Access!.ValidUntil);
        Assert.False(Assert.Single(queue.Requests).Required);
        Assert.False((await Messaging(queue).ChangesAsync(fixture.Alex, "chat-handover", page.Revision, default)).ResetRequired);
    }
    [Fact]
    public async Task TheInitiatingReadReceivesHydratedContentThroughTheExistingMergeAndOutbox()
    {
        await Cold();
        var graph = new Graph(_ => Task.FromResult(Page([Source("new")])));
        var handler = Handler(graph);
        var queue = new Queue((request, _, ct) => handler.HandleAsync(request, ct));
        var page = await Messaging(queue).ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default);
        Assert.Equal("Hydrated", Assert.Single(page.Messages).Body);
        Assert.False(page.Pending);
        Assert.Equal(1, page.Revision);
        Assert.Single(graph.Paths);
        Assert.True(Assert.Single(queue.Requests).Required);
        Assert.True((await fixture.Store.ReadViewAsync(fixture.Tenant, "chat-empty", null, default))!.Value.Complete);
        Assert.Single(await fixture.Store.PendingOutboxAsync(fixture.Tenant, default), value => value.ConversationId == "chat-empty");
        Assert.False((await fixture.Store.GetAsync(fixture.Tenant, "chat-empty", default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task KnownEmptyIsSuccessButAcceptedColdWorkIsTypedPendingNotFabricatedEmptiness()
    {
        await Cold();
        var queue = new Queue();
        var page = await Messaging(queue).ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default);
        Assert.True(page.Pending);
        Assert.NotNull(page.Refresh);
        Assert.Equal("", page.Window);
        var graph = new Graph(_ => Task.FromResult(Page([])));
        await Handler(graph).HandleAsync(page.Refresh, default);
        var available = await Messaging(queue).ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default);
        Assert.False(available.Pending);
        Assert.Empty(available.Messages);
        Assert.NotEmpty(available.Window);
        Assert.Null(available.Cursor);
        Assert.Equal(0, available.Revision);
        Assert.Single(queue.Requests);
    }
    [Fact]
    public async Task ColdSubmissionFailureCannotPretendWorkWasAccepted()
    {
        await Cold();
        var queue = new Queue((_, _, _) => throw new AppError(503, "read_refresh_unavailable", "Queue unavailable."));
        Assert.Equal("read_refresh_unavailable", (await Assert.ThrowsAsync<AppError>(() =>
            Messaging(queue).ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default))).Code);
        Assert.Null(await fixture.Store.ReadViewAsync(fixture.Tenant, "chat-empty", null, default));
    }
    [Fact]
    public async Task QueueAdapterLogsButDoesNotFailOptionalRefreshAndFailsRequiredSubmission()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["App:TenantId"] = fixture.Tenant }).Build();
        await using var queue = new ServiceBusReadRefresh(new FailedBus(), Options, configuration, fixture.Clock,
            NullLogger<ServiceBusReadRefresh>.Instance);
        await queue.RequestAsync(Request(), false, default);
        Assert.Equal("read_refresh_unavailable", (await Assert.ThrowsAsync<AppError>(() =>
            queue.RequestAsync(Request(), true, default))).Code);
    }
    [Fact]
    public async Task ConcurrentReplicasCoalesceAndAnExpiredOwnerCanBeTakenOver()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new Graph(async _ => { entered.TrySetResult(); await release.Task; return Page([Source("one")]); });
        var request = Request();
        var first = Handler(graph).HandleAsync(request, default);
        await entered.Task;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Handler(graph).HandleAsync(request with { RequestedAt = fixture.Clock.GetUtcNow() }, default)));
        Assert.Single(graph.Paths);
        release.SetResult();
        await first;
        await Handler(graph).HandleAsync(request, default);
        Assert.Single(graph.Paths);
        var abandoned = Request("chat-handover");
        Assert.True(await Control.SaveReadRefreshAsync(abandoned.Key, null,
            new("crashed", fixture.Clock.GetUtcNow().AddSeconds(1), RequestedAt: abandoned.RequestedAt), default));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await Handler(graph).HandleAsync(abandoned, default);
        Assert.Equal(2, graph.Paths.Count);
        Assert.NotNull((await Control.ReadRefreshAsync(abandoned.Key, default))!.Value.CompletedThrough);
    }
    [Fact]
    public async Task SourceFailureRetainsRetryEvidenceWithoutCertifyingAnEmptyView()
    {
        var request = Request();
        var graph = new Graph(_ => throw new AppError(503, "graph_503", "Unavailable."));
        await Assert.ThrowsAsync<AppError>(() => Handler(graph).HandleAsync(request, default));
        var failed = (await Control.ReadRefreshAsync(request.Key, default))!.Value;
        Assert.Equal("graph_503", failed.Failure);
        Assert.Null(failed.CompletedThrough);
        Assert.True(failed.RetryAt > fixture.Clock.GetUtcNow());
        Assert.Null(await fixture.Store.ReadViewAsync(fixture.Tenant, "chat-empty", null, default));
        await Assert.ThrowsAsync<ReadRefreshDeferredException>(() => Handler(graph).HandleAsync(request, default));
        Assert.Single(graph.Paths);
    }
    [Theory]
    [InlineData("tenant")]
    [InlineData("resource")]
    [InlineData("epoch")]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("parent")]
    [InlineData("cursor")]
    [InlineData("schema")]
    public async Task InvalidOrFencedWorkNeverCallsGraph(string failure)
    {
        var request = Request();
        request = failure switch
        {
            "tenant" => request with { TenantId = "other" },
            "resource" => request with { ConversationId = "unapproved" },
            "epoch" => request with { Epoch = 2 },
            "old" => request with { RequestedAt = fixture.Clock.GetUtcNow().AddMinutes(-6) },
            "future" => request with { RequestedAt = fixture.Clock.GetUtcNow().AddMinutes(2) },
            "parent" => Request("channel-general", "../other"),
            "cursor" => request with { Before = fixture.Clock.GetUtcNow() },
            _ => request with { SchemaVersion = 2 }
        };
        var graph = new Graph(_ => throw new InvalidOperationException("Graph must not be called."));
        await Assert.ThrowsAsync<AppError>(() => Handler(graph).HandleAsync(request, default));
        Assert.Empty(graph.Paths);
        Assert.Null(await Control.ReadRefreshAsync(request.Key, default));
    }
    [Fact]
    public async Task PartialPagesRemainBoundedAndCannotFalselyDeclareEndOfHistory()
    {
        await Cold();
        var graph = new Graph(_ => Task.FromResult(Page([Source("recent")],
            "chats/chat-empty/messages?$skiptoken=next")));
        await Handler(graph).HandleAsync(Request(), default);
        var queue = new Queue();
        var messaging = Messaging(queue);
        var page = await messaging.ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default);
        Assert.Single(page.Messages);
        Assert.NotNull(page.Cursor);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var nextGraph = new Graph(path =>
        {
            Assert.Contains("$skiptoken=next", path);
            return Task.FromResult(Page([Source("older", at: fixture.Clock.GetUtcNow().AddMinutes(-2))]));
        });
        var nextHandler = Handler(nextGraph);
        var coldQueue = new Queue((request, _, ct) => nextHandler.HandleAsync(request, ct));
        var older = await Messaging(coldQueue).ReadAsync(fixture.Alex, "chat-empty", page.Cursor, null, 0, default);
        Assert.Equal("older", Assert.Single(older.Messages).Id);
        Assert.Null(older.Cursor);
        Assert.Single(nextGraph.Paths);
    }
    [Fact]
    public async Task OlderPageWithTiedTimestampsHydratesPastTheCursorInTheInitiatingRead()
    {
        await Cold();
        var at = fixture.Clock.GetUtcNow();
        var graph = new Graph(path => Task.FromResult(path.Contains("$skiptoken=ties", StringComparison.Ordinal)
            ? Page([Source("z", at: at)])
            : Page([Source("a", at: at)], "chats/chat-empty/messages?$skiptoken=ties")));
        var handler = Handler(graph);
        var queue = new Queue((request, _, ct) => handler.HandleAsync(request, ct));
        var messaging = Messaging(queue);
        var cursor = tokens!.Issue(fixture.Alex, "chat-empty", "page:1:",
            JsonSerializer.Serialize(new { At = at, Id = "m" }), TimeSpan.FromMinutes(15));
        var page = await messaging.ReadAsync(fixture.Alex, "chat-empty", cursor, null, 0, default);
        Assert.False(page.Pending);
        Assert.Equal("z", Assert.Single(page.Messages).Id);
        Assert.Equal(2, graph.Paths.Count);
        Assert.Single(queue.Requests);
        Assert.Null(page.Cursor);
    }
    [Fact]
    public async Task EmptyChannelPagesDoNotStopAtASevenDayTimestampAndEachJobHasThreePageBound()
    {
        var graph = new Graph(_ => Task.FromResult(Page([Source("old", at: fixture.Clock.GetUtcNow().AddDays(-8))],
            "teams/team/channels/channel-general/messages?$skiptoken=next")));
        await Handler(graph).HandleAsync(Request("channel-general"), default);
        Assert.Equal(3, graph.Paths.Count);
        Assert.False((await fixture.Store.ReadViewAsync(fixture.Tenant, "channel-general", null, default))!.Value.Complete);
    }
    [Fact]
    public async Task MissingParentIsHydratedButKnownDeletedParentIsDeniedWithoutGraphWork()
    {
        await Cold("channel-general");
        var graph = new Graph(path => Task.FromResult(path.EndsWith("/unloaded", StringComparison.Ordinal)
            ? Source("unloaded") : Page([Source("reply")])));
        var handler = Handler(graph);
        var queue = new Queue((request, _, ct) => handler.HandleAsync(request, ct));
        var messaging = Messaging(queue);
        var page = await messaging.ReadAsync(fixture.Alex, "channel-general", null, "unloaded", 0, default);
        Assert.Equal("unloaded", page.ParentMessage!.Id);
        Assert.Equal("reply", Assert.Single(page.Messages).Id);
        await fixture.Store.ApplyAsync(fixture.Tenant, "channel-general", page.ParentMessage with
            { Deleted = true, Body = "", ModifiedAt = fixture.Clock.GetUtcNow().AddSeconds(1), SourceVersion = "delete" }, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            messaging.ReadAsync(fixture.Alex, "channel-general", null, "unloaded", 0, default))).Status);
        Assert.Equal(2, graph.Paths.Count);
    }
    [Fact]
    public async Task AParentThatAppearsAfterAnEarlierSource404DoesNotInheritFalseEmptyReplyProof()
    {
        await Cold("channel-general");
        var missing = new Graph(_ => throw new AppError(404, "graph_404", "Not found."));
        await Handler(missing).HandleAsync(Request("channel-general", "later"), default);
        Assert.True((await fixture.Store.ReadViewAsync(fixture.Tenant, "channel-general", "later", default))!.Value.ParentMissing);
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ApplyAsync(fixture.Tenant, "channel-general",
            new("later", fixture.Sam.Id, "Sam", "Parent", now, now), default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var graph = new Graph(_ => Task.FromResult(Page([Source("reply")])));
        var handler = Handler(graph);
        var page = await Messaging(new Queue((request, _, ct) => handler.HandleAsync(request, ct)))
            .ReadAsync(fixture.Alex, "channel-general", null, "later", 0, default);
        Assert.Equal("reply", Assert.Single(page.Messages).Id);
        Assert.Single(graph.Paths);
        Assert.False((await fixture.Store.ReadViewAsync(fixture.Tenant, "channel-general", "later", default))!.Value.ParentMissing);
    }
    [Fact]
    public async Task ContinuationCannotLeaveTheApprovedSourceAndStaleProgressCannotOverwriteNewerState()
    {
        var graph = new Graph(_ => Task.FromResult(Page([], "https://example.invalid/steal")));
        Assert.Equal("graph_url_invalid", (await Assert.ThrowsAsync<AppError>(() =>
            Handler(graph).HandleAsync(Request(), default))).Code);
        Assert.Null(await fixture.Store.ReadViewAsync(fixture.Tenant, "chat-empty", null, default));
        var state = new ReadViewState(1, false, "chats/chat-empty/messages?$skiptoken=one", fixture.Clock.GetUtcNow());
        Assert.True(await fixture.Store.SaveReadViewAsync(fixture.Tenant, "chat-empty", null, null, state, default));
        Assert.False(await fixture.Store.SaveReadViewAsync(fixture.Tenant, "chat-empty", null, null, state with { Complete = true }, default));
        Assert.Equal("writer_fenced", (await Assert.ThrowsAsync<AppError>(() =>
            fixture.Store.SaveReadViewAsync(fixture.Tenant, "chat-empty", null, null, state with { Epoch = 2 }, default))).Code);
        Assert.Equal(0, (await fixture.Store.GetAsync(fixture.Tenant, "chat-empty", default))!.Value.Revision);
    }
    [Fact]
    public async Task OlderHydrationCannotResurrectANewerDeletionOrEmitADuplicateMaterialChange()
    {
        var at = fixture.Clock.GetUtcNow();
        await fixture.Store.ApplyAsync(fixture.Tenant, "chat-empty", new("one", "sam", "Sam", "", at,
            at.AddSeconds(1), Deleted: true, SourceVersion: "new-delete"), default);
        var graph = new Graph(_ => Task.FromResult(Page([Source("one", "Old source body", at)])));
        await Handler(graph).HandleAsync(Request(), default);
        Assert.True((await fixture.Store.CurrentMessageAsync(fixture.Tenant, "chat-empty", "one", null, default))!.Deleted);
        Assert.Equal(1, (await fixture.Store.GetAsync(fixture.Tenant, "chat-empty", default))!.Value.Revision);
    }
    [Fact]
    public async Task CancellingOneHttpWaitDoesNotCancelSharedDurableHydration()
    {
        await Cold();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new Graph(async _ => { entered.SetResult(); await release.Task; return Page([Source("shared")]); });
        Task? shared = null;
        var handler = Handler(graph);
        var queue = new Queue((request, _, _) => { shared = handler.HandleAsync(request, CancellationToken.None); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var read = Messaging(queue).ReadAsync(fixture.Alex, "chat-empty", null, null, 0, cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        release.SetResult();
        await shared!;
        Assert.Equal("shared", Assert.Single((await Messaging(queue).ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default)).Messages).Id);
    }
    [Fact]
    public async Task ActualHistoricalCompletionCanJustifyEmptyHistoryWithoutFabricatingNewVerification()
    {
        var verified = fixture.Clock.GetUtcNow();
        await fixture.Store.SetCoverageAsync(new(fixture.Tenant, "chat-empty", true, verified, null, null,
            verified.AddMinutes(5)), default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.RenewAsync();
        var page = await Messaging().ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default);
        Assert.False(page.Pending);
        Assert.Empty(page.Messages);
        Assert.Equal(verified, (await fixture.Store.ReadCoverageAsync(fixture.Tenant, "chat-empty", default))!.VerifiedAt);
        Assert.Null(await fixture.Store.ReadViewAsync(fixture.Tenant, "chat-empty", null, default));
    }
    [Fact]
    public async Task ASourceGapPreservesRealLegacyEmptyInitializationWithoutRenewingItsVerificationTime()
    {
        var verified = fixture.Clock.GetUtcNow();
        await fixture.Store.SetCoverageAsync(new(fixture.Tenant, "chat-empty", true, verified, null, null,
            verified.AddMinutes(5)), default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Cold();
        var view = (await fixture.Store.ReadViewAsync(fixture.Tenant, "chat-empty", null, default))!.Value;
        Assert.True(view.Complete);
        Assert.Equal(verified, view.VerifiedAt);
        Assert.False((await Messaging().ReadAsync(fixture.Alex, "chat-empty", null, null, 0, default)).Pending);
        Assert.False((await fixture.Store.ReadCoverageAsync(fixture.Tenant, "chat-empty", default))!.Ready);
    }
    [Fact]
    public async Task AHistoryRestrictionObservedDuringCacheReadMustAlsoRestrictTheReturnedAccessProof()
    {
        var now = fixture.Clock.GetUtcNow();
        LiveStateStore? store = null;
        var rows = new[] { new Message("old", "sam", "Sam", "Old", now.AddHours(-2), now.AddHours(-2)),
            new Message("new", "sam", "Sam", "New", now.AddMinutes(-1), now.AddMinutes(-1)) };
        var cache = new UpdatingCache(async () =>
            await store!.SetEntitlementAsync(new(fixture.Tenant, "chat", fixture.Alex.Id, "Alex", now.AddMinutes(-5),
                now.AddSeconds(1), now.AddMinutes(5), "tightened", true, true, 1), default), rows);
        store = new(documents, fixture.Clock, cache: cache);
        await store.CreateAsync(new("chat", fixture.Tenant, "Chat", "chat", null, [], [], [],
            Epoch: 1, CoverageUntil: now.AddMinutes(5)), default);
        await store.SetEntitlementAsync(new(fixture.Tenant, "chat", fixture.Alex.Id, "Alex", now.AddDays(-1),
            now, now.AddMinutes(5), "original", true, true, 1), default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var messaging = new Messaging(store, new GraphResultStub(fixture.Clock), new MemoryBudget(fixture.Clock),
            new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock), fixture.Clock);
        var page = await messaging.ReadAsync(fixture.Alex, "chat", null, null, 0, default);
        Assert.Equal("new", Assert.Single(page.Messages).Id);
        Assert.Equal(now.AddMinutes(-5), page.Access!.HistoryFrom);
    }
    [Fact]
    public async Task RevisionLagNeverRequestsTeamsHydrationAndStaleAuthorityStillFailsClosed()
    {
        await Cold("chat-handover");
        var queue = new Queue();
        var messaging = Messaging(queue);
        Assert.Equal("projection_behind", (await Assert.ThrowsAsync<AppError>(() =>
            messaging.ReadAsync(fixture.Alex, "chat-handover", null, null, 9000, default))).Code);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("authorization_stale", (await Assert.ThrowsAsync<AppError>(() =>
            messaging.ReadAsync(fixture.Alex, "chat-handover", null, null, 0, default))).Code);
        Assert.Empty(queue.Requests);
    }
    private sealed class Queue(Func<ReadRefreshRequest, bool, CancellationToken, Task>? submit = null) : IReadRefresh
    {
        public List<(ReadRefreshRequest Request, bool Required)> Requests { get; } = [];
        public Task RequestAsync(ReadRefreshRequest request, bool required, CancellationToken ct)
        {
            Requests.Add((request, required));
            return submit?.Invoke(request, required, ct) ?? Task.CompletedTask;
        }
    }
    private sealed class Graph(Func<string, Task<JsonElement>> read) : IGraphBackground
    {
        public List<string> Paths { get; } = [];
        public Task<JsonElement> GetAsync(string path, CancellationToken ct) { Paths.Add(path); return read(path); }
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class FailedBus : ServiceBusClient
    {
        public override ServiceBusSender CreateSender(string queueOrTopicName) => new FailedSender();
    }
    private sealed class UpdatingCache(Func<Task> update, IReadOnlyList<Message> rows) : IRecentMessageCache
    {
        public async Task<IReadOnlyList<Message>?> ReadAsync(string key, CancellationToken ct) { await update(); return rows; }
        public Task WriteAsync(string key, IReadOnlyList<Message> messages, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class FailedSender : ServiceBusSender
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) =>
            throw new ServiceBusException("Unavailable.", ServiceBusFailureReason.ServiceCommunicationProblem);
    }
}
