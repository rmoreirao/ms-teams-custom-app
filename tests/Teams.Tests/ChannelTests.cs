using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Messaging.WebPubSub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class ChannelTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ActorId = "22222222-2222-2222-2222-222222222222";
    private const string Team = "33333333-3333-3333-3333-333333333333";
    private const string Web = "44444444-4444-4444-4444-444444444444";
    private const string Principal = "55555555-5555-5555-5555-555555555555";
    private static readonly Actor Actor = new(Tenant, ActorId, "Member");
    private static ChannelWork Work(string kind = "teams") => new(Tenant, ActorId, "Member", Guid.NewGuid().ToString("D"),
        kind, kind == "teams" ? null : Team, kind == "open" ? "general" : null);
    private static ResourceRegistration Registration(string id, TestClock clock) => new(
        new(ResourceRegistry.ChannelId(Tenant, Team, id), "channel", id, "Engineering", null, Team, id),
        clock.GetUtcNow().AddDays(-1), false);
    private static LiveOptions Options(TestClock clock) => new()
    {
        EntitlementProvider = "Graph", WriterEpoch = 1, AdmissionEnabled = true, SynchronizationEnabled = true,
        NotificationCertificateId = "test-certificate",
        GraphPolicy = new() { ApprovalReference = "local-test-policy", WebServicePrincipalId = Principal,
            AdmittedUserIds = [ActorId] },
        ChannelDiscovery = new() { RegistryEnabled = true, Enabled = true,
            ApprovalReference = "local-test-channel-scope", HistoryFrom = clock.GetUtcNow().AddDays(-1) }
    };

    [Fact]
    public async Task RegistryIsStableSharedAndPreservesSeedHistoryAndSendingPolicy()
    {
        var clock = new TestClock();
        var documents = new MemoryPartitionDocuments();
        var options = Options(clock);
        var seed = new ApprovedResource("configured-general", "channel", "General", "Engineering", null, Team, "general");
        options.Resources = [seed];
        options.SubscriptionScopes = [new(seed.Id, seed.Path)];
        options.GraphPolicy!.HistoryFrom[seed.Id] = clock.GetUtcNow().AddDays(-2);
        options.GraphPolicy.SendEnabledResources = [seed.Id];
        var registry = new ResourceRegistry(options, Tenant, documents);
        var existing = await registry.RegisterAsync(Registration("general", clock), default);
        Assert.Equal(seed.Id, existing.Resource.Id);
        Assert.True(existing.CanSend);
        Assert.Equal(options.GraphPolicy.HistoryFrom[seed.Id], existing.HistoryFrom);
        var added = await registry.RegisterAsync(Registration("engineering", clock), default);
        var restarted = new ResourceRegistry(options, Tenant, documents);
        Assert.Equal(added, await restarted.FindAsync(added.Resource.Id, default));
        Assert.Equal(added, await restarted.RegisterAsync(Registration("engineering", clock) with { CanSend = true }, default));
        Assert.Equal(2, (await restarted.ScopesAsync(default)).Count);
        Assert.Null(await new ResourceRegistry(options, "other-tenant", documents).FindAsync(added.Resource.Id, default));
    }

    [Fact]
    public async Task RegistryReservesCapacityIncludingPendingChannelsAndRejectsRetargeting()
    {
        var clock = new TestClock();
        var options = Options(clock);
        options.SubscriptionScopes = Enumerable.Range(0, 99).Select(i => new SubscriptionScope($"seed-{i}", $"seed/{i}")).ToArray();
        var documents = new MemoryPartitionDocuments();
        var first = new ResourceRegistry(options, Tenant, documents);
        var second = new ResourceRegistry(options, Tenant, documents);
        var registration = Registration("one", clock);
        await first.RegisterAsync(registration, default);
        Assert.Equal("channel_capacity", (await Assert.ThrowsAsync<AppError>(() =>
            second.RegisterAsync(Registration("two", clock), default))).Code);
        Assert.Equal(registration, await second.RegisterAsync(registration, default));
        Assert.Equal("channel_registration_invalid", (await Assert.ThrowsAsync<AppError>(() =>
            first.RegisterAsync(registration with { Resource = registration.Resource with { GraphChannelId = "changed" } }, default))).Code);
    }

    [Fact]
    public async Task RegistryRemainsUnavailableUnlessExplicitlyEnabled()
    {
        var options = new LiveOptions();
        var registry = new ResourceRegistry(options, Tenant);
        Assert.Empty(await registry.ListAsync(default));
        Assert.Equal("channel_discovery_disabled", (await Assert.ThrowsAsync<AppError>(() =>
            registry.RegisterAsync(Registration("one", new()), default))).Code);
        options.ChannelDiscovery.Enabled = true;
        Assert.Throws<InvalidOperationException>(() => options.ChannelDiscovery.Validate(options));
    }

    [Fact]
    public async Task WorkflowIdentitySnapshotsJournalAndOutboxCommitTogetherAcrossRestart()
    {
        var clock = new TestClock();
        var documents = new MemoryPartitionDocuments();
        var workflow = new ChannelWorkflowStore(documents, Tenant, clock);
        var work = Work();
        var accepted = await workflow.CreateAsync(work, default);
        Assert.Equal(accepted, await workflow.CreateAsync(work, default));
        Assert.Equal("channel_operation_conflict", (await Assert.ThrowsAsync<AppError>(() =>
            workflow.CreateAsync(work with { Kind = "channels", TeamId = Team }, default))).Code);
        var previous = (await workflow.GetAsync(ActorId, work.OperationId, default))!;
        await workflow.SaveAsync(ActorId, previous, accepted with { State = "ready", Teams = [new(Team, "Engineering")] }, default);
        var restarted = new ChannelWorkflowStore(documents, Tenant, clock);
        Assert.Equal("ready", (await restarted.GetAsync(ActorId, work.OperationId, default))!.Value.State);
        Assert.Null(await restarted.GetAsync("unrelated-user", work.OperationId, default));
        var page = await restarted.ChangesAsync(ActorId, 0, default);
        Assert.Equal(new long[] { 1, 2 }, page.Events.Select(e => e.Sequence));
        Assert.Equal(new long[] { 1, 2 }, page.Events.Select(e => e.Revision));
        Assert.False(page.More);
        Assert.False(page.ResetRequired);
        Assert.Empty((await restarted.ChangesAsync("unrelated-user", 0, default)).Events);
        Assert.True((await restarted.ChangesAsync(ActorId, 3, default)).ResetRequired);
        Assert.Equal("channel_operation_conflict", (await Assert.ThrowsAsync<AppError>(() =>
            workflow.SaveAsync(ActorId, previous, accepted with { State = "failed" }, default))).Code);
        var outbox = await documents.QueryAsync(new(Tenant, "actor-outbox", ActorId: ActorId, Pending: true), 50, null, default);
        Assert.Equal(2, outbox.Items.Count);
        Assert.All(outbox.Items, item => Assert.DoesNotContain("body", item.Value.Json, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JournalPagesAreOrderedAndExpiredGapsRequireAnExplicitReset()
    {
        var clock = new TestClock();
        var documents = new MemoryPartitionDocuments();
        var workflow = new ChannelWorkflowStore(documents, Tenant, clock);
        for (var index = 0; index < 55; index++) await workflow.EmitAsync(ActorId,
            new("channel.catalog.changed", 0, ConversationId: "channel-general"), default);
        var page = await workflow.ChangesAsync(ActorId, 0, default);
        Assert.Equal(50, page.Events.Count);
        Assert.True(page.More);
        var last = await workflow.ChangesAsync(ActorId, page.Events[^1].Sequence, default);
        Assert.Equal(5, last.Events.Count);
        Assert.False(last.More);
        var first = (await documents.QueryAsync(new(Tenant, "channel-event", ActorId: ActorId), 1, null, default)).Items[0];
        var head = (await documents.ReadCurrentAsync(first.Value.Pk, "channel-event-head", default))!;
        await documents.MutateAsync([new(head.Value, head.Version)], [new(first.Value.Pk, first.Value.Id, first.Version)], default);
        var reset = await workflow.ChangesAsync(ActorId, 0, default);
        Assert.True(reset.ResetRequired);
        Assert.Empty(reset.Events);
        Assert.Equal(55, reset.Sequence);
    }

    [Fact]
    public async Task PendingAcceptedWorkCanRecoverAfterBrokerOrProcessLossWithoutChangingTheRequest()
    {
        var clock = new TestClock();
        var documents = new MemoryPartitionDocuments();
        var workflow = new ChannelWorkflowStore(documents, Tenant, clock);
        var work = Work("open");
        await workflow.CreateAsync(work, default);
        var previous = (await workflow.GetAsync(ActorId, work.OperationId, default))!;
        await workflow.SaveAsync(ActorId, previous, previous.Value with { Owner = "crashed", LeaseUntil = clock.GetUtcNow().AddMinutes(2) }, default);
        var queue = new WorkQueue();
        var restarted = new ChannelWorkflowStore(documents, Tenant, clock);
        await restarted.RecoverAsync(ActorId, queue, default);
        Assert.Empty(queue.Items);
        clock.Advance(TimeSpan.FromMinutes(3));
        await restarted.RecoverAsync(ActorId, queue, default);
        Assert.Equal(work, Assert.Single(queue.Items));
        var current = (await restarted.GetAsync(ActorId, work.OperationId, default))!;
        await restarted.SaveAsync(ActorId, current, current.Value with { State = "ready" }, default);
        queue.Items.Clear();
        await restarted.RecoverAsync(ActorId, queue, default);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task ActorDeliveryIsRetryableDeduplicatedAndRejectsTamperedWork()
    {
        var clock = new TestClock();
        var documents = new MemoryPartitionDocuments();
        var workflow = new ChannelWorkflowStore(documents, Tenant, clock);
        await workflow.CreateAsync(Work(), default);
        var document = Assert.Single((await documents.QueryAsync(new(Tenant, "actor-outbox"), 50, null, default)).Items);
        var work = JsonSerializer.Deserialize<ActorEventWork>(document.Value.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var realtime = new Realtime { Fail = true };
        await Assert.ThrowsAsync<IOException>(() => workflow.DeliverAsync(work, realtime, default));
        realtime.Fail = false;
        await Assert.ThrowsAsync<AppError>(() => workflow.DeliverAsync(work with { Event = work.Event with { Revision = 100 } }, realtime, default));
        await workflow.DeliverAsync(work, realtime, default);
        await new ChannelWorkflowStore(documents, Tenant, clock).DeliverAsync(work, realtime, default);
        Assert.Single(realtime.Events);
        Assert.Empty((await documents.QueryAsync(new(Tenant, "actor-outbox", Pending: true), 50, null, default)).Items);
    }

    [Fact]
    public async Task AzureEventsUseTheBrowserCamelCaseContractAndSuppressBlockedActors()
    {
        var fixture = new Fixture();
        using var transport = new EventTransport();
        var options = new WebPubSubServiceClientOptions { Transport = new HttpClientTransport(new HttpClient(transport)) };
        var client = new WebPubSubServiceClient(new Uri("https://local-test.webpubsub.azure.com"), "messaging",
            new AzureKeyCredential(Convert.ToBase64String(new byte[32])), options);
        var realtime = new AzureRealtime(client, fixture.Store);
        await realtime.PublishEventAsync(Tenant, ActorId,
            new("channel.view.ready", 0, ConversationId: "channel-general", Generation: 0), default);
        Assert.Equal("channel.view.ready", transport.Body.GetProperty("type").GetString());
        Assert.Equal(1, transport.Body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, transport.Body.GetProperty("sequence").GetInt64());
        Assert.False(transport.Body.TryGetProperty("Type", out _));
        await fixture.Store.SetBlockedAsync(Tenant, ActorId, true, default);
        await realtime.PublishEventAsync(Tenant, ActorId, new("channel.catalog.changed", 1, ConversationId: "channel-general"), default);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedChannelIsReverifiedAndPreparesKnownEmptyHistoryThroughDurableEvents(bool sending)
    {
        var fixture = new Fixture();
        fixture.Options.ChannelDiscovery.SendEnabled = sending;
        var work = Work("open");
        await fixture.Workflows.CreateAsync(work, default);
        Assert.True(await fixture.Handler.HandleAsync(work, default));
        Assert.True(await fixture.Handler.HandleAsync(work, default));
        var operation = (await fixture.Workflows.GetAsync(ActorId, work.OperationId, default))!.Value;
        Assert.Equal("ready", operation.State);
        Assert.NotNull(operation.ConversationId);
        var member = await fixture.Store.GetMemberAsync(Actor, operation.ConversationId, false, default);
        Assert.Equal(sending, member.CanSend);
        var view = await fixture.Store.ReadViewAsync(Tenant, operation.ConversationId, null, default);
        Assert.True(view!.Value.Complete);
        var catalog = await fixture.Store.CatalogAsync(Actor, null, default);
        Assert.Equal(Team, Assert.Single(catalog.Items).TeamId);
        Assert.Equal(sending, catalog.Items[0].CanPost);
        var events = await fixture.Documents.QueryAsync(new(Tenant, "outbox", ConversationId: operation.ConversationId), 50, null, default);
        var ready = JsonSerializer.Deserialize<OutboxChange>(Assert.Single(events.Items).Value.Json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(0, ready.Revision);
        Assert.Equal("channel.view.ready", ready.Event!.Type);
        var queue = new DeliveryQueue();
        await new OutboxDispatcher(Tenant, fixture.Options, queue, NullLogger<OutboxDispatcher>.Instance, fixture.Registry)
            .DispatchAsync(events.Items.Select(item => item.Value).ToArray(), default);
        var realtime = new Realtime();
        var delivery = new DeliveryHandler(Tenant, fixture.Options, fixture.Store, realtime, queue,
            fixture.Clock, NullLogger<DeliveryHandler>.Instance, fixture.Registry, fixture.Workflows);
        var deliveryWork = Assert.Single(queue.Items);
        await delivery.HandleAsync(deliveryWork, default);
        await delivery.HandleAsync(deliveryWork, default);
        Assert.Equal("channel.view.ready", Assert.Single(realtime.Events).Type);
        Assert.Contains((await fixture.Workflows.ChangesAsync(ActorId, 0, default)).Events, e => e.Type == "channel.catalog.changed");
        Assert.Equal(1, fixture.Source.SubscriptionPosts);
        Assert.Equal(0, fixture.Source.MessagePosts);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("private")]
    [InlineData("shared")]
    [InlineData("moderated")]
    [InlineData("missing-moderation")]
    [InlineData("archived-channel")]
    [InlineData("missing-archive")]
    [InlineData("reply-restricted")]
    [InlineData("unknown-moderation")]
    [InlineData("not-joined")]
    [InlineData("disabled")]
    [InlineData("guest")]
    [InlineData("unassigned")]
    public async Task UnsupportedOrUnauthorizedSelectionNeverRegistersOrHydrates(string denial)
    {
        var fixture = new Fixture(denial);
        var work = Work("open");
        await fixture.Workflows.CreateAsync(work, default);
        Assert.True(await fixture.Handler.HandleAsync(work, default));
        Assert.Equal("denied", (await fixture.Workflows.GetAsync(ActorId, work.OperationId, default))!.Value.State);
        Assert.Empty(await fixture.Registry.ListAsync(default));
        Assert.Equal(0, fixture.Source.MessageReads);
        Assert.Equal(0, fixture.Source.SubscriptionPosts);
    }

    [Fact]
    public async Task DiscoveryIsMetadataOnlyAndRejectsAnEscapingContinuation()
    {
        var fixture = new Fixture();
        var work = Work("channels");
        await fixture.Workflows.CreateAsync(work, default);
        Assert.True(await fixture.Handler.HandleAsync(work, default));
        Assert.Single((await fixture.Workflows.GetAsync(ActorId, work.OperationId, default))!.Value.Channels!);
        Assert.Empty(await fixture.Registry.ListAsync(default));
        Assert.Equal(0, fixture.Source.MessageReads);
        var wrong = Work() with { Continuation = "https://evil.invalid/v1.0/users/actor/joinedTeams" };
        await fixture.Workflows.CreateAsync(wrong, default);
        Assert.True(await fixture.Handler.HandleAsync(wrong, default));
        Assert.Equal("denied", (await fixture.Workflows.GetAsync(ActorId, wrong.OperationId, default))!.Value.State);
    }
    [Theory]
    [InlineData("")]
    [InlineData("null-moderation")]
    [InlineData("guest-posts-excluded")]
    public async Task DocumentedUnmoderatedChannelSettingsAreEligible(string settings)
    {
        var fixture = new Fixture(settings);
        var work = Work("channels");
        await fixture.Workflows.CreateAsync(work, default);
        Assert.True(await fixture.Handler.HandleAsync(work, default));
        Assert.Single((await fixture.Workflows.GetAsync(ActorId, work.OperationId, default))!.Value.Channels!);
        Assert.Contains(fixture.Source.Reads, path => path.StartsWith("https://graph.microsoft.com/beta/teams/", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Source.MessageReads);
        Assert.Equal(0, fixture.Source.SubscriptionPosts);
    }
    [Fact]
    public async Task PermanentGraphBadRequestsFailDiscoveryWithoutADeferredRetryLoop()
    {
        var fixture = new Fixture("graph_400");
        var work = Work("channels");
        await fixture.Workflows.CreateAsync(work, default);
        Assert.True(await fixture.Handler.HandleAsync(work, default));
        var operation = (await fixture.Workflows.GetAsync(ActorId, work.OperationId, default))!.Value;
        Assert.Equal("failed", operation.State);
        Assert.Contains("graph_400", operation.Error);
        Assert.Equal(1, operation.Attempts);
        Assert.Empty(await fixture.Registry.ListAsync(default));
        Assert.Equal(0, fixture.Source.MessageReads);
    }

    [Fact]
    public async Task SynchronizationOffNeverExecutesAcceptedWork()
    {
        var fixture = new Fixture();
        var work = Work("open");
        await fixture.Workflows.CreateAsync(work, default);
        fixture.Options.SynchronizationEnabled = false;
        await Assert.ThrowsAsync<AppError>(() => fixture.Handler.HandleAsync(work, default));
        Assert.Empty(fixture.Source.Reads);
        Assert.Empty(await fixture.Registry.ListAsync(default));
    }

    [Fact]
    public async Task RegisteredHistoryFloorIsAppliedBeforeWritingCosmosOrOutbox()
    {
        var fixture = new Fixture();
        var registration = await fixture.Registry.RegisterAsync(Registration("history", fixture.Clock), default);
        var resource = registration.Resource;
        await fixture.Store.CreateAsync(new(resource.Id, Tenant, resource.Title, "channel", resource.Team, [], [], [],
            GraphTeamId: Team, GraphChannelId: "history"), default);
        await fixture.Store.ApplyAsync(Tenant, resource.Id, new("too-old", ActorId, "Member", "must-not-be-projected",
            fixture.Clock.GetUtcNow().AddDays(-2), fixture.Clock.GetUtcNow()), default);
        Assert.Empty((await fixture.Documents.QueryAsync(new(Tenant, "message"), 50, null, default)).Items);
        Assert.Empty((await fixture.Documents.QueryAsync(new(Tenant, "outbox"), 50, null, default)).Items);
        Assert.Equal(0, (await fixture.Store.GetAsync(Tenant, resource.Id, default))!.Value.Revision);
    }

    [Fact]
    public async Task DynamicChannelCacheSeparatesPostsAndThreadsAndRechecksHistoryParentsAndRevocationOnHits()
    {
        var cache = new MemoryRecentMessageCache();
        var fixture = new Fixture(cache: cache);
        var work = Work("open");
        await fixture.Workflows.CreateAsync(work, default);
        await fixture.Handler.HandleAsync(work, default);
        var id = (await fixture.Workflows.GetAsync(ActorId, work.OperationId, default))!.Value.ConversationId!;
        var now = fixture.Clock.GetUtcNow();
        var root = new Message("old-root", ActorId, "Member", "Earlier post", now.AddHours(-1), now.AddHours(-1), SourceVersion: "initial");
        var recent = root with { Id = "recent-root", Body = "Recent post", CreatedAt = now.AddMinutes(-5), ModifiedAt = now.AddMinutes(-5) };
        await fixture.Store.ApplyAsync(Tenant, id, root, default);
        await fixture.Store.ApplyAsync(Tenant, id, recent, default);
        await fixture.Store.ApplyAsync(Tenant, id, recent with { Id = "reply-one", ParentId = root.Id, Body = "First thread", CreatedAt = now.AddMinutes(-2) }, default);
        await fixture.Store.ApplyAsync(Tenant, id, recent with { Id = "reply-two", ParentId = recent.Id, Body = "Second thread", CreatedAt = now.AddMinutes(-1) }, default);
        foreach (var parentId in new[] { root.Id, recent.Id })
            Assert.True(await fixture.Store.SaveReadViewAsync(Tenant, id, parentId, null, new(1, true, null, now), default));
        var messaging = new Messaging(fixture.Store, new GraphResultStub(fixture.Clock), new MemoryBudget(fixture.Clock),
            new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock), fixture.Clock);
        var posts = await messaging.ReadAsync(Actor, id, null, null, 0, default);
        Assert.Equal(new[] { "old-root", "recent-root" }, posts.Messages.Select(message => message.Id));
        Assert.Equal("reply-one", Assert.Single((await messaging.ReadAsync(Actor, id, null, root.Id, 0, default)).Messages).Id);
        Assert.Equal("reply-two", Assert.Single((await messaging.ReadAsync(Actor, id, null, recent.Id, 0, default)).Messages).Id);
        var narrowActor = Actor with { Id = "66666666-6666-6666-6666-666666666666" };
        var grant = new Entitlement(Tenant, id, narrowActor.Id, "Later member", now.AddMinutes(-10), now,
            now.AddMinutes(2), "narrow-history", true, false, 1);
        await fixture.Store.SetEntitlementAsync(grant, default);
        var evidence = new ReadEvidence("page", now);
        Assert.Equal("recent-root", Assert.Single((await messaging.ReadAsync(narrowActor, id, null, null, 0, default, evidence)).Messages).Id);
        Assert.Contains(evidence.Steps, step => step.Layer == "redis" && step.Outcome == "hit");
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => messaging.ReadAsync(narrowActor, id, null, root.Id, 0, default))).Status);
        await fixture.Store.ApplyAsync(Tenant, id, root with { Deleted = true, ModifiedAt = now, SourceVersion = "deleted" }, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => messaging.ReadAsync(Actor, id, null, root.Id, 0, default))).Status);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Store.SetEntitlementAsync(grant with { Active = false, VerifiedAt = fixture.Clock.GetUtcNow(),
            AuthorityVersion = "membership-removed" }, default);
        var reads = cache.Reads;
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => messaging.ReadAsync(narrowActor, id, null, null, 0, default))).Status);
        Assert.Equal(reads, cache.Reads);
        cache.Unavailable = true;
        var fallback = new ReadEvidence("page", fixture.Clock.GetUtcNow());
        Assert.Equal("reply-two", Assert.Single((await messaging.ReadAsync(Actor, id, null, recent.Id, 0, default, fallback)).Messages).Id);
        Assert.Contains(fallback.Steps, step => step.Layer == "redis" && step.Outcome == "unavailable");
        Assert.Contains(fallback.Steps, step => step.Layer == "cosmos" && step.Outcome == "content-query");
    }

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public MemoryPartitionDocuments Documents { get; } = new();
        public LiveOptions Options { get; }
        public LiveStateStore Store { get; }
        public ResourceRegistry Registry { get; }
        public ChannelWorkflowStore Workflows { get; }
        public Source Source { get; }
        public ChannelJobHandler Handler { get; }
        public Fixture(string denial = "", MemoryRecentMessageCache? cache = null)
        {
            Options = ChannelTests.Options(Clock);
            Registry = new(Options, Tenant, new MemoryPartitionDocuments());
            Store = new(Documents, Clock, cache: cache, registry: Registry);
            Workflows = new(Documents, Tenant, Clock);
            Source = new(Clock, denial);
            var control = new LiveControlStore(new MemoryPartitionDocuments(), Clock, Tenant);
            var graph = new GraphBackground(new HttpClient(new Transport(Source)), new Credential());
            var scoped = new ApplicationGraph(graph, Options, control, Registry);
            var policy = new GraphPolicyFeed(scoped, scoped, Store, Options, Tenant, Web, Clock,
                NullLogger<GraphPolicyFeed>.Instance, Registry);
            Handler = new(Tenant, Options, Workflows, new(graph, policy), Registry, Store, policy,
                new(scoped, control, Options, Clock),
                new(Tenant, Options, Store, control, scoped, Clock, NullLogger<ReadRefreshHandler>.Instance, Registry),
                Clock, new Realtime());
        }
    }
    private sealed class Source(TestClock clock, string denial)
    {
        public bool RejectMetadata => denial == "graph_400";
        public int SubscriptionPosts;
        public int MessagePosts;
        public int MessageReads;
        public List<string> Reads { get; } = [];
        public object Read(string path)
        {
            Reads.Add(path);
            var uri = new Uri(path);
            var route = uri.AbsolutePath.StartsWith("/beta/", StringComparison.Ordinal) ?
                GraphMessages.NormalizeResource(uri.AbsolutePath["/beta/".Length..]) :
                GraphUrls.ResourcePath(GraphUrls.Validate(path));
            if (route == $"servicePrincipals/{Principal}")
                return new { id = Principal, appId = Web, appRoleAssignmentRequired = true, accountEnabled = true };
            if (route.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal))
                return new { value = denial == "unassigned" ? [] : new[] {
                    new { resourceId = Principal, principalId = ActorId, principalType = "User", appRoleId = Guid.Empty.ToString("D") } } };
            if (route == $"users/{ActorId}") return new { id = ActorId, displayName = "Member",
                accountEnabled = denial != "disabled", userType = denial == "guest" ? "Guest" : "Member" };
            if (route.EndsWith("/joinedTeams", StringComparison.Ordinal)) return new {
                value = denial == "not-joined" ? [] : new[] { new { id = Team, displayName = "Engineering" } } };
            if (route == $"teams/{Team}") return new { id = Team, displayName = "Engineering", isArchived = denial == "archived" };
            if (route == $"teams/{Team}/channels" || route == $"teams/{Team}/channels/general")
            {
                Assert.StartsWith("/beta/", uri.AbsolutePath);
                return route.EndsWith("/channels", StringComparison.Ordinal) ? new { value = new[] { Channel() } } : Channel();
            }
            if (route.EndsWith("/messages", StringComparison.Ordinal)) { MessageReads++; return new { value = Array.Empty<object>() }; }
            throw new InvalidOperationException($"Unexpected Graph read: {path}");
        }
        private object Channel()
        {
            var channel = new Dictionary<string, object?> { ["id"] = "general", ["displayName"] = "General",
                ["membershipType"] = denial is "private" or "shared" ? denial : "standard" };
            if (denial != "missing-archive") channel["isArchived"] = denial == "archived-channel";
            if (denial != "missing-moderation") channel["moderationSettings"] = denial == "null-moderation" ? null : new {
                userNewMessageRestriction = denial == "moderated" ? "moderators" :
                    denial == "unknown-moderation" ? "unknownFutureValue" : denial == "guest-posts-excluded" ? "everyoneExceptGuests" : "everyone",
                replyRestriction = denial == "reply-restricted" ? "authorAndModerators" : "everyone"
            };
            return channel;
        }
        public object Post(string path)
        {
            if (GraphUrls.ResourcePath(GraphUrls.Validate(path)) != "subscriptions") { MessagePosts++; throw new InvalidOperationException("No background message sends."); }
            SubscriptionPosts++;
            return new { id = "subscription-id", expirationDateTime = clock.GetUtcNow().AddMinutes(50) };
        }
    }
    private sealed class Transport(Source source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.Query.Contains("moderationSettings", StringComparison.Ordinal) &&
                (request.RequestUri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal) || source.RejectMetadata)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(request.Method == HttpMethod.Get ? source.Read(request.RequestUri!.AbsoluteUri) :
                    source.Post(request.RequestUri!.AbsoluteUri))) });
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("local-test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class EventTransport : HttpMessageHandler
    {
        public JsonElement Body;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return new(HttpStatusCode.Accepted);
        }
    }
    private sealed class WorkQueue : IChannelWorkQueue
    {
        public List<ChannelWork> Items { get; } = [];
        public Task EnqueueAsync(ChannelWork work, DateTimeOffset? scheduledAt, CancellationToken ct) { Items.Add(work); return Task.CompletedTask; }
    }
    private sealed class DeliveryQueue : IDeliveryWorkQueue
    {
        public List<DeliveryWork> Items { get; } = [];
        public Task EnqueueAsync(DeliveryWork work, DateTimeOffset? scheduledAt, CancellationToken ct)
        { Items.Add(work); return Task.CompletedTask; }
    }
    private sealed class Realtime : IRealtime
    {
        public bool Fail;
        public List<RealtimeEvent> Events { get; } = [];
        public Task PublishEventAsync(string tenant, string actor, RealtimeEvent value, CancellationToken ct)
        {
            if (Fail) throw new IOException("Local transport failure.");
            Events.Add(value); return Task.CompletedTask;
        }
        public Task PublishAsync(Conversation conversation, CancellationToken ct) => throw new InvalidOperationException();
        public Task RevokeAsync(string tenant, string actor, CancellationToken ct) => Task.CompletedTask;
    }
}
