using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
    private readonly Realtime realtime = new();
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
            new(graph, store, control, clock), authority, realtime, clock, NullLogger<LiveControlWorker>.Instance);
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
    private Entitlement Grant(bool active, string version) => new(Tenant, "chat", "alex", "Alex",
        clock.GetUtcNow().AddDays(-1), clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2), version, active, active, 1);
    [Fact]
    public async Task InactiveNonmembersDoNotCloseExistingUserConnections()
    {
        await Prepare(options.Resources[0], ready: true);
        authority.Entries = [Grant(false, "not-a-member")];
        for (var pass = 0; pass < 10; pass++)
        {
            await worker.StepAsync(default);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        authority.Entries = [Grant(false, "still-not-a-member")];
        await worker.StepAsync(default);
        Assert.Equal(0, realtime.Attempts);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            store.GetMemberAsync(new(Tenant, "alex", "Alex"), "chat", false, default))).Status);
    }
    [Fact]
    public async Task AccessRemovalClosesConnectionsOnceRatherThanOnEveryPolicyRefresh()
    {
        await Prepare(options.Resources[0], ready: true);
        authority.Entries = [Grant(true, "member")];
        await worker.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        authority.Entries = [Grant(false, "removed")];
        await worker.StepAsync(default);
        Assert.Equal((Tenant, "alex"), Assert.Single(realtime.Revoked));
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            store.GetMemberAsync(new(Tenant, "alex", "Alex"), "chat", false, default))).Status);
        await worker.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        authority.Entries = [Grant(false, "still-removed")];
        await worker.StepAsync(default);
        Assert.Equal(1, realtime.Attempts);

        clock.Advance(TimeSpan.FromSeconds(2));
        authority.Entries = [Grant(true, "restored")];
        await worker.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        authority.Entries = [Grant(false, "removed-again")];
        await worker.StepAsync(default);
        Assert.Equal(2, realtime.Attempts);
    }
    [Fact]
    public async Task FailedConnectionRevocationRemainsRetryableAfterWorkerRestart()
    {
        await Prepare(options.Resources[0], ready: true);
        authority.Entries = [Grant(true, "member")];
        await worker.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        authority.Entries = [Grant(false, "removed")];
        realtime.FailNext = true;
        await worker.StepAsync(default);
        Assert.Equal(1, realtime.Attempts);
        Assert.Empty(realtime.Revoked);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            store.GetMemberAsync(new(Tenant, "alex", "Alex"), "chat", false, default))).Status);

        var restarted = new LiveControlWorker(Tenant, options, store, control, new(graph, control, options, clock),
            new(graph, store, control, clock), authority, realtime, clock, NullLogger<LiveControlWorker>.Instance);
        await restarted.StepAsync(default);
        await restarted.StepAsync(default);
        Assert.Equal(2, realtime.Attempts);
        Assert.Single(realtime.Revoked);
    }
    [Fact]
    public async Task FailedCatalogUpdateDoesNotLoseTheConnectionRevocation()
    {
        await Prepare(options.Resources[0], ready: true);
        authority.Entries = [Grant(true, "member")];
        await worker.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(2));
        authority.Entries = [Grant(false, "removed")];
        documents.FailNextCatalog = true;
        await worker.StepAsync(default);
        Assert.Equal(0, realtime.Attempts);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            store.GetMemberAsync(new(Tenant, "alex", "Alex"), "chat", false, default))).Status);
        await worker.StepAsync(default);
        await worker.StepAsync(default);
        Assert.Equal(1, realtime.Attempts);
    }
    [Fact]
    public async Task CoverageInspectionPreservesInvalidationReasonWithoutRenewingEvidence()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        var invalidation = new Coverage(Tenant, resource.Id, false, null, null, "missed");
        await store.SetCoverageAsync(invalidation, default);
        Assert.Equal(invalidation, await store.ReadCoverageAsync(Tenant, resource.Id, default));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(invalidation, await store.ReadCoverageAsync(Tenant, resource.Id, default));
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
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
        clock.Advance(TimeSpan.FromSeconds(3));
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
    public async Task RenewalStartsWithFourMinutesRemainingForMultiPassVerification()
    {
        options.Resources = [options.Resources[0], new("other", "chat", "Other", null, "graph-other", null, null)];
        options.SubscriptionScopes = [options.SubscriptionScopes[0], new("other-scope", "chats/graph-other/messages")];
        var initial = clock.GetUtcNow();
        foreach (var resource in options.Resources)
        {
            await Prepare(resource, ready: true);
            for (var index = 0; index < 85; index++)
                await store.ApplyAsync(Tenant, resource.Id, GraphMessages.Parse(Source($"{index:D2}", initial)), default);
            await CompleteRoot(resource);
        }
        clock.Advance(TimeSpan.FromSeconds(59));
        await worker.StepAsync(default);
        Assert.Empty(graph.Reads);
        clock.Advance(TimeSpan.FromSeconds(2));
        var started = clock.GetUtcNow();
        graph.Read = path =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1300));
            return path.Contains('?', StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() })
                : Source(path[(path.LastIndexOf('/') + 1)..], initial);
        };
        for (var pass = 0; pass < 5; pass++)
        {
            await worker.StepAsync(default);
            foreach (var resource in options.Resources)
                Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
            if (pass < 4) clock.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.InRange(clock.GetUtcNow() - started, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(239));
        Assert.True(clock.GetUtcNow() < initial.AddMinutes(5));
        foreach (var resource in options.Resources)
        {
            Assert.False((await control.RepairAsync(resource.Id, null, default))!.Value.Pending);
            Assert.Equal(86, graph.Reads.Count(path => path.StartsWith(resource.Path, StringComparison.Ordinal)));
            var metadata = (await store.GetAsync(Tenant, resource.Id, default))!.Value;
            var root = (await control.RepairAsync(resource.Id, null, default))!.Value;
            Assert.Equal(root.UpdatedAt.AddMinutes(5), metadata.CoverageUntil);
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
        clock.Advance(TimeSpan.FromSeconds(3));
        await worker.StepAsync(default);
        Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.Equal(1, graph.Reads.Count(path => path.EndsWith("/00", StringComparison.Ordinal)));
        Assert.Equal(2, graph.Reads.Count(path => path.EndsWith("/01", StringComparison.Ordinal)));
        Assert.Equal(1, graph.Reads.Count(path => path.EndsWith("/02", StringComparison.Ordinal)));
    }
    [Theory]
    [InlineData(51)]
    [InlineData(101)]
    public async Task RepairAdvancesDocumentedPaginationBeyondFiftyMessages(int count)
    {
        var resource = new ApprovedResource("chat", "chat", "Chat", null, "19:Mixed@thread.v2", null, null);
        options.Resources = [resource];
        options.SubscriptionScopes = [new("chat-scope", resource.Path)];
        await Prepare(resource);
        var at = clock.GetUtcNow();
        graph.Read = path =>
        {
            if (!path.Contains('?', StringComparison.Ordinal)) return Source(path[(path.LastIndexOf('/') + 1)..], at);
            var offset = path.Contains("$skiptoken=", StringComparison.Ordinal)
                ? int.Parse(path[(path.IndexOf("$skiptoken=", StringComparison.Ordinal) + 11)..], System.Globalization.CultureInfo.InvariantCulture) : 0;
            var values = Enumerable.Range(offset, Math.Min(50, count - offset)).Select(index => Source($"{index:D3}", at)).ToArray();
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["value"] = values,
                ["@odata.nextLink"] = offset + 50 < count
                    ? $"https://graph.microsoft.com/v1.0/chats/19:Mixed@thread.v2/messages?$skiptoken={offset + 50}" : null
            }.Where(pair => pair.Value is not null).ToDictionary());
        };
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await worker.StepAsync(default);
            if ((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady) break;
        }
        Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.Equal(count, (await store.GetAsync(Tenant, resource.Id, default))!.Value.Revision);
        Assert.Equal(count + (count + 49) / 50, graph.Reads.Count);
        Assert.NotNull(await store.MessageAsync(Tenant, resource.Id, $"{count - 1:D3}", null, default));
    }
    [Fact]
    public async Task InvalidContinuationBlocksWithoutHotRetriesOrRestoringCoverage()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        graph.Read = _ => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = "https://evil.invalid/v1.0/chats/graph-chat/messages"
        });
        await worker.StepAsync(default);
        var failed = (await control.RepairAsync(resource.Id, null, default))!.Value;
        Assert.Equal("blocked", failed.Status);
        Assert.True(failed.Pending);
        Assert.Equal("graph_url_invalid", failed.Failure!.Code);
        Assert.True(await control.HasPendingRepairAsync(resource.Id, default));
        for (var attempt = 0; attempt < 5; attempt++) { clock.Advance(TimeSpan.FromMinutes(1)); await worker.StepAsync(default); }
        Assert.Single(graph.Reads);
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        await control.EnqueueRepairAsync(resource, null, null, default);
        Assert.Equal("blocked", (await control.RepairAsync(resource.Id, null, default))!.Value.Status);
    }
    [Fact]
    public async Task TransientFailureDefersWithoutExtendingProofAndResumesWithBoundedBackoff()
    {
        await Prepare(options.Resources[0]);
        graph.Read = _ => throw new AppError(503, "graph_503", "Private upstream details must not be logged.");
        await worker.StepAsync(default);
        var first = (await control.RepairAsync("chat", null, default))!.Value;
        Assert.Equal("deferred", first.Status);
        Assert.Equal(1, first.Failure!.Attempts);
        Assert.InRange(first.Failure.NextAttemptAt!.Value - clock.GetUtcNow(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        for (var attempt = 0; attempt < 3; attempt++) await worker.StepAsync(default);
        Assert.Single(graph.Reads);
        clock.Advance(TimeSpan.FromSeconds(3));
        await worker.StepAsync(default);
        var second = (await control.RepairAsync("chat", null, default))!.Value;
        Assert.Equal(2, second.Failure!.Attempts);
        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
        Assert.InRange(second.Failure.NextAttemptAt!.Value - clock.GetUtcNow(), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        graph.Read = _ => JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() });
        clock.Advance(TimeSpan.FromSeconds(5));
        await worker.StepAsync(default);
        var completed = (await control.RepairAsync("chat", null, default))!.Value;
        Assert.Equal("completed", completed.Status);
        Assert.Equal(0, completed.Failure!.Attempts);
        Assert.Equal("graph_503", completed.Failure.Code);
        Assert.Equal(clock.GetUtcNow(), completed.LastProgressAt);
        Assert.True((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task AContinuousSubscriptionGapDoesNotResetRepairGenerationOrRetryBackoff()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        var subscription = (await control.SubscriptionAsync(options.SubscriptionScopes[0].Id, default))!;
        Assert.True(await control.SaveSubscriptionAsync(subscription.Value with { Expires = clock.GetUtcNow().AddSeconds(-1) },
            subscription.Version, default));
        await control.EnqueueRepairAsync(resource, null, null, default);
        graph.Read = _ => throw new AppError(401, "graph_not_dispatched", "The retained synchronization account needs sign-in.");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await worker.StepAsync(default);
            Assert.Equal(1, (await store.GetAsync(Tenant, resource.Id, default))!.Value.RepairGeneration);
            clock.Advance(TimeSpan.FromSeconds(3));
        }
        var deferred = (await control.RepairAsync(resource.Id, null, default))!.Value;
        Assert.Equal("deferred", deferred.Status);
        Assert.Equal(4, deferred.Failure!.Attempts);
        Assert.Equal(4, graph.Reads.Count);
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);

        subscription = (await control.SubscriptionAsync(options.SubscriptionScopes[0].Id, default))!;
        Assert.True(await control.SaveSubscriptionAsync(subscription.Value with { Expires = clock.GetUtcNow().AddMinutes(30) },
            subscription.Version, default));
        graph.Read = _ => JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() });
        clock.Advance(TimeSpan.FromSeconds(61));
        await worker.StepAsync(default);
        Assert.True((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
        Assert.Equal(1, (await store.GetAsync(Tenant, resource.Id, default))!.Value.RepairGeneration);

        subscription = (await control.SubscriptionAsync(options.SubscriptionScopes[0].Id, default))!;
        Assert.True(await control.SaveSubscriptionAsync(subscription.Value with { Expires = clock.GetUtcNow().AddSeconds(-1) },
            subscription.Version, default));
        await worker.StepAsync(default);
        Assert.Equal(2, (await store.GetAsync(Tenant, resource.Id, default))!.Value.RepairGeneration);
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task ExpiredContinuationRestartsOnceAtTheApprovedBoundary()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var queued = (await control.RepairAsync("chat", null, default))!;
        Assert.True(await control.SaveRepairAsync(queued with { Value = queued.Value with
            { NextLink = resource.Path + "?$skiptoken=expired", Generation = 0 } }, default));
        graph.Read = path => path.Contains("expired", StringComparison.Ordinal)
            ? throw new AppError(503, "graph_400", "Continuation expired.")
            : JsonSerializer.SerializeToElement(new { value = Array.Empty<object>() });
        await worker.StepAsync(default);
        var restarted = (await control.RepairAsync("chat", null, default))!.Value;
        Assert.Equal(1, restarted.Recovery!.Restarts);
        Assert.Null(restarted.NextLink);
        Assert.Equal("deferred", restarted.Status);
        Assert.Equal(queued.Value.UpdatedAt, restarted.UpdatedAt);
        clock.Advance(TimeSpan.FromSeconds(3));
        await worker.StepAsync(default);
        Assert.Equal("completed", (await control.RepairAsync("chat", null, default))!.Value.Status);
        Assert.True((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task ARepeatedInvalidContinuationBlocksInsteadOfRestartingForever()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var queued = (await control.RepairAsync("chat", null, default))!;
        Assert.True(await control.SaveRepairAsync(queued with { Value = queued.Value with
            { NextLink = resource.Path + "?$skiptoken=expired", Generation = 0 } }, default));
        graph.Read = path => path.Contains("expired", StringComparison.Ordinal)
            ? throw new AppError(503, "graph_400", "Continuation expired.")
            : JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = resource.Path + "?$skiptoken=expired"
            });
        await worker.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(3));
        await worker.StepAsync(default);
        var failed = (await control.RepairAsync("chat", null, default))!.Value;
        Assert.Equal("blocked", failed.Status);
        Assert.Equal(1, failed.Recovery!.Restarts);
        Assert.Equal(3, graph.Reads.Count);
        clock.Advance(TimeSpan.FromHours(1));
        await worker.StepAsync(default);
        Assert.Equal(3, graph.Reads.Count);
        Assert.False((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task RepairDiagnosticsIdentifyTheResourceAndSuppressRepeatedPrivateDetails()
    {
        await Prepare(options.Resources[0]);
        var logger = new CaptureLogger();
        var observed = new LiveControlWorker(Tenant, options, store, control, new(graph, control, options, clock),
            new(graph, store, control, clock), authority, new Realtime(), clock, logger);
        graph.Read = _ => throw new AppError(503, "graph_503", "PRIVATE-BODY-AND-CURSOR");
        await observed.StepAsync(default);
        clock.Advance(TimeSpan.FromSeconds(3));
        await observed.StepAsync(default);
        var warning = Assert.Single(logger.Messages, message => message.Level == LogLevel.Warning).Message;
        Assert.Contains("resource=chat", warning);
        Assert.Contains("checkpoint=root", warning);
        Assert.Contains("stage=hydration", warning);
        Assert.Contains("code=graph_503", warning);
        Assert.Contains("state=deferred", warning);
        Assert.DoesNotContain("PRIVATE-BODY-AND-CURSOR", warning);
        Assert.DoesNotContain("skiptoken", warning);
    }
    [Fact]
    public async Task SupersededPendingRepairRestartsBeforeReadingItsOldContinuation()
    {
        var resource = options.Resources[0];
        await Prepare(resource);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var queued = (await control.RepairAsync("chat", null, default))!;
        Assert.True(await control.SaveRepairAsync(queued with { Value = queued.Value with
            { NextLink = resource.Path + "?$skiptoken=old-generation", Generation = 0 } }, default));
        await store.SetCoverageAsync(new(Tenant, "chat", false, null, null, "source_gap"), default);
        await worker.StepAsync(default);
        Assert.DoesNotContain(graph.Reads, path => path.Contains("old-generation", StringComparison.Ordinal));
        var completed = (await control.RepairAsync("chat", null, default))!.Value;
        Assert.Equal(1, completed.Generation);
        Assert.Equal("repair_generation_superseded", completed.Recovery!.Reason);
        Assert.True((await store.GetAsync(Tenant, "chat", default))!.Value.CoverageReady);
    }
    [Fact]
    public async Task BlockedRepliesStillFenceACompletedRoot()
    {
        var resource = new ApprovedResource("channel", "channel", "Channel", "Team", null, "team", "channel");
        options.Resources = [resource];
        options.SubscriptionScopes = [new("channel-scope", resource.Path)];
        await Prepare(resource);
        await CompleteRoot(resource);
        await control.EnqueueRepairAsync(resource, null, "parent", default);
        var reply = (await control.RepairAsync(resource.Id, "parent", default))!;
        Assert.NotNull(await control.FailRepairAsync(resource, reply, 0, "graph_cursor_invalid", true, false, default));
        await worker.StepAsync(default);
        Assert.Empty(graph.Reads);
        Assert.True(await control.HasPendingRepairAsync(resource.Id, default));
        Assert.False((await store.GetAsync(Tenant, resource.Id, default))!.Value.CoverageReady);
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
        public bool FailNextCatalog;
        public int FailedCoverageWrites;
        public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct) => inner.ReadAsync(pk, id, ct);
        public Task<Versioned<PartitionDocument>?> ReadCurrentAsync(string pk, string id, CancellationToken ct) => inner.ReadCurrentAsync(pk, id, ct);
        public Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct) =>
            inner.QueryAsync(filter, limit, cursor, ct);
        public Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
        {
            if (FailNextCatalog && writes.Any(write => write.Value.Kind == "catalog"))
            {
                FailNextCatalog = false;
                return Task.FromResult(false);
            }
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
    private sealed class CaptureLogger : ILogger<LiveControlWorker>
    {
        public readonly ConcurrentQueue<(LogLevel Level, string Message)> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue((logLevel, formatter(state, exception)));
    }
    private sealed class Authority : IEntitlementFeed
    {
        public Action? Checked;
        public IReadOnlyList<Entitlement> Entries = [];
        public Task<AuthorityPage> ReadAsync(ApprovedResource resource, string? cursor, CancellationToken ct)
        {
            Checked?.Invoke();
            return Task.FromResult(new AuthorityPage(Tenant, resource.Id, Entries, null));
        }
    }
    private sealed class Realtime : IRealtime
    {
        public readonly ConcurrentQueue<(string Tenant, string Actor)> Revoked = [];
        public int Attempts;
        public bool FailNext;
        public Task PublishAsync(Conversation conversation, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeAsync(string tenant, string actorId, CancellationToken ct)
        {
            Attempts++;
            if (FailNext)
            {
                FailNext = false;
                throw new AppError(503, "realtime_unavailable", "Connection revocation is temporarily unavailable.");
            }
            Revoked.Enqueue((tenant, actorId));
            return Task.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}
