using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class LiveControlTests
{
    private const string Tenant = "tenant";
    private readonly Clock clock = new();
    private readonly Documents documents = new();
    private readonly Graph graph = new();
    private readonly Authority authority = new();
    private readonly LiveOptions options = new()
    {
        SynchronizationEnabled = true, WriterEpoch = 1,
        Resources = [new("chat", "chat", "Chat", null, "graph-chat", null, null)],
        SubscriptionScopes = [new("chat-scope", "chats/graph-chat/messages")]
    };
    private readonly LiveStateStore store;
    private readonly LiveControlStore control;
    private readonly LiveControlWorker worker;

    public LiveControlTests()
    {
        store = new(documents, clock);
        control = new(documents, clock, Tenant);
        worker = new(Tenant, options, store, control, new(graph, control, options, clock),
            new(graph, store, control, clock), authority, new Realtime(), clock, NullLogger<LiveControlWorker>.Instance);
    }
    private async Task Prepare(ApprovedResource resource, bool ready = false)
    {
        await store.CreateAsync(new(resource.Id, Tenant, resource.Title, resource.Kind, resource.Team, [], [], [],
            CoverageReady: ready, GraphChatId: resource.GraphChatId, GraphTeamId: resource.GraphTeamId,
            GraphChannelId: resource.GraphChannelId, Epoch: options.WriterEpoch,
            CoverageUntil: ready ? clock.GetUtcNow().AddMinutes(5) : null), default);
        var scope = options.SubscriptionScopes.Single(scope => scope.Covers(resource));
        await control.SaveSubscriptionAsync(new(scope.Id, scope.Path, "state", $"subscription-{resource.Id}",
            clock.GetUtcNow().AddMinutes(50), "cert", true, "another-controller", clock.GetUtcNow().AddHours(1)), null, default);
    }
    private async Task CompleteRoot(ApprovedResource resource, DateTimeOffset? at = null, long generation = 0)
    {
        await control.EnqueueRepairAsync(resource, null, null, default);
        var root = (await control.RepairAsync(resource.Id, null, default))!;
        Assert.True(await control.SaveRepairAsync(root with { Value = root.Value with
        {
            Pending = false, Verifying = true, Generation = generation, UpdatedAt = at ?? clock.GetUtcNow()
        } }, default));
    }
    [Fact]
    public async Task CompletedRepairRecoversAFailedCoverageCommitWithoutRepeatingGraphReads()
    {
        await Prepare(options.Resources[0]);
        documents.FailNextCoverage = true;
        await worker.StepAsync(default);
        Assert.False((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
        Assert.False((await control.RepairAsync("chat", null, default))!.Value.Pending);
        Assert.Single(graph.Reads);

        clock.Advance(TimeSpan.FromSeconds(2));
        await worker.StepAsync(default);
        Assert.True((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
        Assert.Single(graph.Reads);
        Assert.Equal(1, documents.FailedCoverageWrites);
    }
    [Fact]
    public async Task RecoveryDoesNotExtendTheOriginalProofLeaseOrRequireANewMessage()
    {
        await Prepare(options.Resources[0]);
        var verifiedAt = clock.GetUtcNow();
        await CompleteRoot(options.Resources[0]);
        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.StepAsync(default);
        var metadata = (await store.GetAsync(Tenant, "chat", default))!.Value;
        Assert.True(metadata.CoverageReady);
        Assert.Equal(verifiedAt.AddMinutes(5), metadata.CoverageUntil);
        Assert.Empty(graph.Reads);
        Assert.Equal(0, metadata.Revision);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrSupersededProofCannotRestoreCoverage(bool superseded)
    {
        await Prepare(options.Resources[0]);
        await CompleteRoot(options.Resources[0], superseded ? null : clock.GetUtcNow().AddMinutes(-6));
        if (superseded)
            await store.SetCoverageAsync(new(Tenant, "chat", false, null, null, "missed_notifications"), default);
        graph.Read = _ => throw new AppError(503, "graph_unavailable", "Source verification is unavailable.");
        await worker.StepAsync(default);
        Assert.False((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
        Assert.True((await control.RepairAsync("chat", null, default))!.Value.Pending);
        Assert.Single(graph.Reads);
    }
    [Fact]
    public async Task PendingReplyVerificationPreventsRestoringRootCoverage()
    {
        var resource = new ApprovedResource("channel", "channel", "Channel", "Team", null, "team", "channel");
        options.Resources = [resource];
        options.SubscriptionScopes = [new("channel-scope", resource.Path)];
        await Prepare(resource);
        await CompleteRoot(resource);
        await control.EnqueueRepairAsync(resource, $"{resource.Path}/parent/replies?$top=50", "parent", default);
        graph.Read = _ => throw new AppError(429, "graph_capacity_exceeded", "Deferred.");
        await worker.StepAsync(default);
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.True(await control.HasPendingRepairAsync(resource.Id, default));
        Assert.False((await control.RepairAsync(resource.Id, null, default))!.Value.Pending);
        Assert.All(graph.Reads, path => Assert.Contains("/parent/replies", path));
        graph.Read = _ => JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() });
        await worker.StepAsync(default);
        Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.False(await control.HasPendingRepairAsync(resource.Id, default));
        Assert.False((await control.RepairAsync(resource.Id, null, default))!.Value.Pending);
        Assert.All(graph.Reads, path => Assert.Contains("/parent/replies", path));
    }
    [Fact]
    public async Task RecoveredCoverageCannotOutliveItsSubscription()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        await CompleteRoot(resource);
        var subscription = (await control.SubscriptionAsync(options.SubscriptionScopes[0].Id, default))!;
        var expires = clock.GetUtcNow().AddMinutes(1);
        Assert.True(await control.SaveSubscriptionAsync(subscription.Value with { Expires = expires }, subscription.Version, default));
        graph.Read = _ => throw new AppError(429, "graph_capacity_exceeded", "Deferred.");
        await worker.StepAsync(default);
        var metadata = (await store.GetAsync(Tenant, resource.Id, default))!.Value;
        Assert.True(metadata.CoverageReady);
        Assert.Equal(expires, metadata.CoverageUntil);
        clock.Advance(TimeSpan.FromSeconds(61));
        await worker.StepAsync(default);
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task RenewalFinishesBoundedVerificationAcrossChatsBeforeExistingCoverageExpires()
    {
        options.Resources = [
            options.Resources[0], new("other", "chat", "Other", null, "graph-other", null, null)
        ];
        options.SubscriptionScopes = [
            options.SubscriptionScopes[0], new("other-scope", "chats/graph-other/messages")
        ];
        var initial = clock.GetUtcNow();
        foreach (var resource in options.Resources)
        {
            await Prepare(resource, ready: true);
            for (var index = 0; index < 25; index++)
                await store.ApplyAsync(Tenant, resource.Id, GraphMessages.Parse(Source($"{index:D2}", initial)), default);
            await CompleteRoot(resource);
        }
        clock.Advance(TimeSpan.FromMinutes(3));
        var started = clock.GetUtcNow();
        authority.Checked = () => clock.Advance(TimeSpan.FromSeconds(10));
        graph.Read = path =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1300));
            return path.Contains('?', StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() })
                : Source(path[(path.LastIndexOf('/') + 1)..], initial);
        };

        await worker.StepAsync(default);
        foreach (var resource in options.Resources)
        {
            Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
            Assert.True((await control.RepairAsync(resource.Id, null, default))!.Value.Pending);
            Assert.Equal(20, graph.Reads.Count(path => path.StartsWith(resource.Path, StringComparison.Ordinal)));
        }
        await worker.StepAsync(default);
        Assert.True(clock.GetUtcNow() - started < TimeSpan.FromMinutes(2));
        foreach (var resource in options.Resources)
        {
            Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
            Assert.False((await control.RepairAsync(resource.Id, null, default))!.Value.Pending);
            Assert.Equal(26, graph.Reads.Count(path => path.StartsWith(resource.Path, StringComparison.Ordinal)));
        }
    }
    [Fact]
    public async Task CapacityDeferralRetainsTheVerifiedCursorAndDoesNotPretendRepairIsComplete()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        var at = clock.GetUtcNow();
        for (var index = 0; index < 3; index++)
            await store.ApplyAsync(Tenant, resource.Id, GraphMessages.Parse(Source($"{index:D2}", at)), default);
        graph.Read = path => graph.Reads.Count == 3
            ? throw new AppError(429, "graph_capacity_exceeded", "Deferred.")
            : path.Contains('?', StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() })
                : Source(path[(path.LastIndexOf('/') + 1)..], at);
        await worker.StepAsync(default);
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.True((await control.RepairAsync(resource.Id, null, default))!.Value.Pending);
        Assert.NotNull((await control.RepairAsync(resource.Id, null, default))!.Value.ScanCursor);
        await worker.StepAsync(default);
        Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.Equal(1, graph.Reads.Count(path => path.EndsWith("/00", StringComparison.Ordinal)));
        Assert.Equal(2, graph.Reads.Count(path => path.EndsWith("/01", StringComparison.Ordinal)));
        Assert.Equal(1, graph.Reads.Count(path => path.EndsWith("/02", StringComparison.Ordinal)));
    }
    [Fact]
    public async Task ManyReplyJobsShareOneBoundedResourceBudgetPerPass()
    {
        var resource = new ApprovedResource("channel", "channel", "Channel", "Team", null, "team", "channel");
        options.Resources = [resource];
        options.SubscriptionScopes = [new("channel-scope", resource.Path)];
        await Prepare(resource);
        await CompleteRoot(resource);
        for (var index = 0; index < 21; index++)
            await control.EnqueueRepairAsync(resource, $"{resource.Path}/parent-{index}/replies?$top=50", $"parent-{index}", default);
        await worker.StepAsync(default);
        Assert.Equal(10, graph.Reads.Count);
        Assert.True(await control.HasPendingRepairAsync(resource.Id, default));
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        await worker.StepAsync(default);
        await worker.StepAsync(default);
        Assert.False(await control.HasPendingRepairAsync(resource.Id, default));
        Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task BackgroundAuthorityRefreshContinuesWhileRepairIsWaitingOnGraph()
    {
        await Prepare(options.Resources[0]);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        authority.Checked = () =>
        {
            if (Interlocked.Increment(ref checks) >= 2) refreshed.TrySetResult(true);
        };
        graph.ReadAsync = async (_, ct) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() });
        };
        await worker.StartAsync(default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((await control.RepairAsync("chat", null, default))!.Value.Pending);
            Assert.True(Volatile.Read(ref checks) >= 2);
        }
        finally
        {
            release.TrySetResult(true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(deadline.Token);
            worker.Dispose();
        }
    }
    private static JsonElement Source(string id, DateTimeOffset at) => JsonSerializer.SerializeToElement(new
    {
        id, createdDateTime = at, lastModifiedDateTime = at,
        body = new { content = id, contentType = "text" }
    });
    private sealed class Documents : IPartitionDocuments
    {
        private readonly MemoryPartitionDocuments inner = new();
        public bool FailNextCoverage;
        public int FailedCoverageWrites;
        public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct) => inner.ReadAsync(pk, id, ct);
        public Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct) =>
            inner.QueryAsync(filter, limit, cursor, ct);
        public Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
        {
            if (FailNextCoverage && writes.Any(write => write.Value.Kind == "coverage"))
            {
                FailNextCoverage = false; FailedCoverageWrites++;
                return Task.FromResult(false);
            }
            return inner.WriteAsync(writes, ct);
        }
    }
    private sealed class Graph : IGraphBackground
    {
        public readonly ConcurrentQueue<string> Reads = [];
        public Func<string, JsonElement> Read = _ => JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() });
        public Func<string, CancellationToken, Task<JsonElement>>? ReadAsync;
        public Task<JsonElement> GetAsync(string path, CancellationToken ct)
        {
            Reads.Enqueue(path);
            return ReadAsync?.Invoke(path, ct) ?? Task.FromResult(Read(path));
        }
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Unexpected POST.");
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Unexpected PATCH.");
    }
    private sealed class Authority : IEntitlementFeed
    {
        public Action? Checked;
        public Task<AuthorityPage> ReadAsync(ApprovedResource resource, string? cursor, CancellationToken ct)
        {
            Checked?.Invoke();
            return Task.FromResult(new AuthorityPage(Tenant, resource.Id, [], null));
        }
    }
    private sealed class Realtime : IRealtime
    {
        public Task PublishAsync(Conversation conversation, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeAsync(string tenant, string actorId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}
