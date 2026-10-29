using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class GraphPolicyTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Web = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string WebPrincipal = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private static readonly string[] Actors = [
        "22222222-2222-2222-2222-222222222222",
        "33333333-3333-3333-3333-333333333333",
        "44444444-4444-4444-4444-444444444444"
    ];
    [Fact]
    public async Task SourceBackedDecisionsNeedNoInteractiveUserContextAndPreserveExactPairs()
    {
        var fixture = new Fixture();
        var page = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        Assert.Equal(3, page.Entries.Count);
        Assert.Equal(2, page.Entries.Count(entry => entry.Active && entry.CanSend));
        Assert.False(page.Entries.Single(entry => entry.ActorId == Actors[2]).Active);
        Assert.Equal(Actors.Take(2).Order(), page.DirectChat!.MemberIds.Order());
        Assert.All(page.Entries, entry => Assert.Equal(fixture.Clock.GetUtcNow().AddDays(-1), entry.HistoryFrom));
        Assert.All(fixture.Source.Reads, path => Assert.DoesNotContain("token", path, StringComparison.OrdinalIgnoreCase));
        fixture.Clock.Advance(TimeSpan.FromHours(9));
        var refreshed = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        Assert.Equal(2, refreshed.Entries.Count(entry => entry.Active));
        Assert.True(refreshed.Entries[0].VerifiedAt > page.Entries[0].VerifiedAt);
        Assert.True(refreshed.Entries[0].FreshUntil <= fixture.Clock.GetUtcNow().AddMinutes(2));
    }
    [Fact]
    public async Task CachedPolicyDoesNotExtendItsEvidenceWindowOrRefetchEachMaintenancePass()
    {
        var fixture = new Fixture();
        var first = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        var requests = fixture.Source.Reads.Count;
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        var cached = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        Assert.Same(first, cached);
        Assert.Equal(requests, fixture.Source.Reads.Count);
        Assert.Equal(first.Entries[0].FreshUntil, cached.Entries[0].FreshUntil);
    }
    [Theory]
    [InlineData("disabled")]
    [InlineData("unassigned")]
    [InlineData("guest")]
    [InlineData("deleted")]
    [InlineData("revoked")]
    [InlineData("web-disabled")]
    public async Task IdentityOrAdmissionRemovalCannotBecomeAnActiveGrant(string denial)
    {
        var fixture = new Fixture();
        var first = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        foreach (var entry in first.Entries) await fixture.Store.SetEntitlementAsync(entry, default);
        fixture.Source.Denial = denial;
        if (denial == "revoked") await fixture.Store.SetBlockedAsync(Tenant, Actors[0], true, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        var next = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        var denied = next.Entries.Single(entry => entry.ActorId == Actors[0]);
        Assert.False(denied.Active);
        Assert.False(denied.CanSend);
        await fixture.Store.SetEntitlementAsync(denied, default);
        var error = await Assert.ThrowsAsync<AppError>(() => fixture.Store.GetMemberAsync(new(Tenant, Actors[0], ""), fixture.Direct.Id, false, default));
        Assert.Contains(error.Status, new[] { 401, 404 });
    }
    [Fact]
    public async Task UserHistoryAndExplicitSendPolicyAreIndependentOfApplicationReadAccess()
    {
        var fixture = new Fixture();
        fixture.Options.GraphPolicy!.SendEnabledResources = [];
        var page = await fixture.Feed.ReadAsync(fixture.Group, null, default);
        Assert.All(page.Entries, entry => Assert.True(entry.Active));
        Assert.All(page.Entries, entry => Assert.False(entry.CanSend));
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(-2), page.Entries.Single(entry => entry.ActorId == Actors[2]).HistoryFrom);
        fixture.Source.Denial = "history";
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("policy_history_unproven", (await Assert.ThrowsAsync<AppError>(() =>
            fixture.Feed.ReadAsync(fixture.Group, null, default))).Code);
    }
    [Theory]
    [InlineData("wrong-pair")]
    [InlineData("wrong-chat-type")]
    [InlineData("foreign-member")]
    public async Task ChangedDirectResourceConditionsDenyInsteadOfFabricatingPairAuthority(string denial)
    {
        var fixture = new Fixture();
        fixture.Source.Denial = denial;
        var page = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
        Assert.Null(page.DirectChat);
        Assert.All(page.Entries, entry => Assert.False(entry.Active));
    }
    [Theory]
    [InlineData("archived")]
    [InlineData("private-channel")]
    [InlineData("moderated")]
    [InlineData("archived-channel")]
    [InlineData("missing-moderation")]
    [InlineData("reply-restricted")]
    public async Task UnsupportedChannelPoliciesStayDenied(string denial)
    {
        var fixture = new Fixture();
        fixture.Source.Denial = denial;
        var page = await fixture.Feed.ReadAsync(fixture.Channel, null, default);
        Assert.All(page.Entries, entry => Assert.False(entry.Active));
    }
    [Fact]
    public async Task ChannelPolicyRenewsExpiredCatalogAuthorityUsingMetadataNotMessageContents()
    {
        var fixture = new Fixture();
        var actor = new Actor(Tenant, Actors[0], "");
        var first = await fixture.Feed.ReadAsync(fixture.Channel, null, default);
        foreach (var grant in first.Entries) await fixture.Store.SetEntitlementAsync(grant, default);
        Assert.Single((await fixture.Store.CatalogAsync(actor, null, default)).Items);
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("authorization_stale", (await Assert.ThrowsAsync<AppError>(() =>
            fixture.Store.CatalogAsync(actor, null, default))).Code);
        var renewed = await fixture.Feed.ReadAsync(fixture.Channel, null, default);
        foreach (var grant in renewed.Entries) await fixture.Store.SetEntitlementAsync(grant, default);
        Assert.Single((await fixture.Store.CatalogAsync(actor, null, default)).Items);
        Assert.True(renewed.Entries[0].VerifiedAt > first.Entries[0].VerifiedAt);
        Assert.Contains(fixture.Source.Reads, path => path.StartsWith("https://graph.microsoft.com/beta/teams/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Source.Reads, path => path.Contains("/messages", StringComparison.Ordinal));
    }
    [Fact]
    public async Task BetaMetadataPolicyReaderCannotLeaveTheRegisteredChannel()
    {
        var fixture = new Fixture();
        var requests = new List<Uri>();
        var transport = new GraphBackground(new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        })), new Credential());
        var app = new ApplicationGraph(transport, fixture.Options, new(new MemoryPartitionDocuments(), fixture.Clock, Tenant));
        await app.ReadChannelAsync(fixture.Channel, default);
        Assert.StartsWith("/beta/teams/", Assert.Single(requests).AbsolutePath);
        await Assert.ThrowsAsync<AppError>(() => app.ReadChannelAsync(fixture.Direct, default));
        await Assert.ThrowsAsync<AppError>(() => app.ReadChannelAsync(fixture.Channel with { GraphChannelId = "other" }, default));
        await Assert.ThrowsAsync<AppError>(() => app.ReadChannelAsync(fixture.Channel with { Id = "unregistered" }, default));
        Assert.Single(requests);
    }
    [Theory]
    [InlineData("invalid-user")]
    [InlineData("invalid-app")]
    [InlineData("assignment-disabled")]
    [InlineData("policy-unavailable")]
    public async Task MissingOrMismatchedEvidenceIsAnExplicitFailure(string denial)
    {
        var fixture = new Fixture();
        fixture.Source.Denial = denial;
        await Assert.ThrowsAsync<AppError>(() => fixture.Feed.ReadAsync(fixture.Direct, null, default));
    }
    [Fact]
    public void ServiceRuntimeDoesNotRegisterBackgroundGraphOrBorrowedContextsInApi()
    {
        var fixture = new Fixture();
        foreach (var role in new[] { "Api", "Receiver", "Projector", "Delivery", "Control" })
        {
            var services = new ServiceCollection();
            services.AddLiveRuntime(fixture.Options, Tenant, role, new Credential());
            Assert.Equal(role == "Control", services.Any(descriptor => descriptor.ServiceType == typeof(IGraphBackground)));
            Assert.Equal(role == "Control", services.Any(descriptor => descriptor.ServiceType == typeof(IEntitlementFeed)));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SyntheticUserContexts));
        }
    }
    [Fact]
    public async Task ApplicationGraphCannotReadOtherPolicyIdentitiesOrSendUserMessages()
    {
        var fixture = new Fixture();
        var requests = new List<string>();
        var transport = new GraphBackground(new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };
        })), new Credential());
        var app = new ApplicationGraph(transport, fixture.Options, new(new MemoryPartitionDocuments(), fixture.Clock, Tenant));
        var scoped = app.ForResource(fixture.Direct.Path);
        await scoped.GetAsync("chats/19:direct@thread.v2/messages?$skiptoken=opaque%2Bvalue", default);
        Assert.Single(requests);
        await Assert.ThrowsAsync<AppError>(() => scoped.GetAsync(fixture.Group.Path, default));
        await Assert.ThrowsAsync<AppError>(() => scoped.PostAsync(fixture.Direct.Path, new { }, default));
        await Assert.ThrowsAsync<AppError>(() => app.ReadAsync("users/not-admitted", default));
        await Assert.ThrowsAsync<AppError>(() => app.ReadAsync("servicePrincipals/not-the-web-app/appRoleAssignedTo", default));
        await Assert.ThrowsAsync<AppError>(() => scoped.PostAsync("subscriptions", new
        {
            resource = fixture.Group.Path, notificationUrl = "https://unexpected.invalid",
            lifecycleNotificationUrl = "https://unexpected.invalid", includeResourceData = true, encryptionCertificateId = "cert"
        }, default));
        Assert.Single(requests);
        Assert.Throws<AppError>(() => app.ForResource(fixture.Direct.Path + "?$skiptoken=client-supplied"));
    }
    [Fact]
    public async Task WorkloadAuthenticationFailureCannotBecomeAnUnknownSubscriptionCreation()
    {
        var fixture = new Fixture();
        fixture.Options.NotificationUrl = "https://example.invalid/callbacks/graph/notifications";
        fixture.Options.LifecycleUrl = "https://example.invalid/callbacks/graph/lifecycle";
        fixture.Options.NotificationCertificateId = "cert";
        fixture.Options.NotificationCertificate = "public-fixture-certificate";
        var requests = 0;
        var transport = new GraphBackground(new HttpClient(new Handler(_ =>
        {
            requests++;
            throw new InvalidOperationException("No Graph request can precede a valid workload token.");
        })), new UnavailableCredential());
        var control = new LiveControlStore(new MemoryPartitionDocuments(), fixture.Clock, Tenant);
        var graph = new ApplicationGraph(transport, fixture.Options, control);
        var subscription = new SubscriptionScope("source", fixture.Direct.Path);
        var error = await Assert.ThrowsAsync<AppError>(() =>
            new SubscriptionController(graph, control, fixture.Options, fixture.Clock).StepAsync(subscription, default));
        Assert.Equal("graph_not_dispatched", error.Code);
        Assert.Equal(0, requests);
        Assert.False((await control.SubscriptionAsync(subscription.Id, default))!.Value.CreateAttempted);
        Assert.DoesNotContain("PRIVATE", error.Message);
    }
    [Fact]
    public void RoleIdentityAndWebSyncApplicationBoundariesAreExplicit()
    {
        var fixture = new Fixture();
        fixture.Options.RoleIdentityClientIds = new()
        {
            ["Api"] = "10000000-0000-0000-0000-000000000001",
            ["Receiver"] = "10000000-0000-0000-0000-000000000002",
            ["Projector"] = "10000000-0000-0000-0000-000000000003",
            ["Delivery"] = "10000000-0000-0000-0000-000000000004",
            ["Control"] = "10000000-0000-0000-0000-000000000005"
        };
        fixture.Options.SyncClientId = "10000000-0000-0000-0000-000000000006";
        fixture.Options.ValidateIdentityBindings(Web, fixture.Options.RoleIdentityClientIds["Api"], "Api");
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Options.ValidateIdentityBindings(Web, fixture.Options.RoleIdentityClientIds["Control"], "Api"));
        fixture.Options.SyncClientId = Web;
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Options.ValidateIdentityBindings(Web, fixture.Options.RoleIdentityClientIds["Api"], "Api"));
    }
    [Fact]
    public void UnsupportedPolicyConfigurationIsNotSilentlyIgnored()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GraphPolicyOptions>(
            """{"approvalReference":"reviewed","assumeAllUsersCanRead":true}""", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    [Fact]
    public void CompleteGraphPolicyConfigurationSupportsAllFiveIsolatedRolesAndRejectsLegacyOwners()
    {
        var fixture = new Fixture();
        var options = fixture.Options;
        var roles = new[] { "Api", "Receiver", "Projector", "Delivery", "Control" };
        for (var index = 0; index < roles.Length; index++)
            options.RoleIdentityClientIds[roles[index]] = $"10000000-0000-0000-0000-{index + 1:D12}";
        options.ApprovalReference = "approved-dedicated-scope";
        options.SyncClientId = "10000000-0000-0000-0000-000000000006";
        options.ControlIdentityClientId = options.RoleIdentityClientIds["Control"];
        options.ReadConsistency = "Session";
        options.NotificationUrl = "https://example.invalid/callbacks/graph/notifications";
        options.LifecycleUrl = "https://example.invalid/callbacks/graph/lifecycle";
        options.NotificationCertificateId = "cert";
        options.NotificationKeys = new() { ["cert"] = "https://test.vault.azure.net/keys/notifications/version" };
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=offline-policy-contract", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        options.NotificationCertificate = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
        options.SubscriptionScopes = options.Resources.Select(resource => new SubscriptionScope(resource.Id, resource.Path)).ToArray();
        foreach (var role in roles)
        {
            options.Validate(Tenant, false, false, role);
            options.ValidateIdentityBindings(Web, options.RoleIdentityClientIds[role], role);
        }
        options.HostedRoles = ["Api", "Control"];
        Assert.Throws<InvalidOperationException>(() => options.Validate(Tenant, false, false, "Api"));
        options.HostedRoles = [];
        options.SyntheticActorIds = Actors;
        Assert.Throws<InvalidOperationException>(() => options.Validate(Tenant, false, false, "Control"));
        options.SyntheticActorIds = [];
        options.GraphPolicy!.ApprovalReference = "PENDING-APPROVAL";
        Assert.Throws<InvalidOperationException>(() => options.Validate(Tenant, false, false, "Control"));
    }
    [Fact]
    public async Task ServiceStorageDiagnosticCannotUseAContentContainer()
    {
        var fixture = new Fixture();
        using var services = new ServiceCollection().AddSingleton<ILiveStateStore>(fixture.Store).BuildServiceProvider();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["App:Role"] = "Api", ["LiveDiagnostics:Container"] = "live-state" }).Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveDiagnostics.RunAsync(services, configuration, fixture.Options, Tenant, "SessionCheck"));
        Assert.Contains("isolated synthetic validation container", error.Message);
    }
    [Theory]
    [InlineData("Control")]
    [InlineData("Receiver")]
    [InlineData("Projector")]
    [InlineData("Delivery")]
    public async Task ServiceStorageDiagnosticCannotRunUnderAnotherRole(string role)
    {
        var fixture = new Fixture();
        using var services = new ServiceCollection().BuildServiceProvider();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["App:Role"] = role, ["LiveDiagnostics:Container"] = "session-primary-validation-isolated" }).Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveDiagnostics.RunAsync(services, configuration, fixture.Options, Tenant, "SessionCheck"));
        Assert.Contains("SessionCheck require Api", error.Message);
    }
    [Fact]
    public async Task ProjectionAcceptanceReadsEveryAdmittedSnapshotAndDeniesExcludedPairs()
    {
        var fixture = new Fixture();
        await PrepareProjectionCheckAsync(fixture);
        var result = await ProjectionChecks.RunAsync(fixture.Store, fixture.Options, Tenant, fixture.Clock,
            NullLogger<LiveDiscovery>.Instance, default);
        Assert.Equal(new ProjectionCheckResult(3, 8, 1, 2), result);
    }
    [Theory]
    [InlineData("coverage")]
    [InlineData("expired-policy")]
    [InlineData("extra-pair-admission")]
    public async Task ProjectionAcceptanceCannotConcealUnavailableOrExcessAccess(string failure)
    {
        var fixture = new Fixture();
        await PrepareProjectionCheckAsync(fixture);
        if (failure == "coverage")
            await fixture.Store.SetCoverageAsync(new(Tenant, fixture.Direct.Id, false, null, null, "acceptance-test"), default);
        else if (failure == "expired-policy") fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        else
        {
            var page = await fixture.Feed.ReadAsync(fixture.Direct, null, default);
            var original = page.Entries.Single(entry => entry.ActorId == Actors[2]);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Store.SetEntitlementAsync(original with { Active = true, CanSend = true,
                VerifiedAt = fixture.Clock.GetUtcNow(), AuthorityVersion = "unexpected-pair-grant" }, default);
        }
        Task<ProjectionCheckResult> Check() => ProjectionChecks.RunAsync(fixture.Store, fixture.Options, Tenant,
            fixture.Clock, NullLogger<LiveDiscovery>.Instance, default);
        if (failure == "extra-pair-admission")
            await Assert.ThrowsAsync<InvalidOperationException>(Check);
        else
        {
            var error = await Assert.ThrowsAsync<AppError>(Check);
            Assert.Equal(failure == "coverage" ? "projection_unhealthy" : "authorization_stale", error.Code);
        }
    }
    private static async Task PrepareProjectionCheckAsync(Fixture fixture)
    {
        fixture.Options.SynchronizationEnabled = true;
        fixture.Options.AdmissionEnabled = true;
        foreach (var resource in fixture.Options.Resources)
        {
            var page = await fixture.Feed.ReadAsync(resource, null, default);
            foreach (var entry in page.Entries) await fixture.Store.SetEntitlementAsync(entry, default);
            if (page.DirectChat is not null) await fixture.Store.SetDirectChatAuthorityAsync(page.DirectChat, default);
            await fixture.Store.ApplyAsync(Tenant, resource.Id, new("projection-check-message", Actors[0], "Actor",
                "body-must-not-be-logged", fixture.Clock.GetUtcNow().AddMinutes(-1), fixture.Clock.GetUtcNow()), default);
            await fixture.Store.SetCoverageAsync(new(Tenant, resource.Id, true, fixture.Clock.GetUtcNow(), null, null,
                fixture.Clock.GetUtcNow().AddMinutes(5), 0), default);
        }
    }
    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public ApprovedResource Direct { get; } = new("direct", "chat", "Direct", null, "19:direct@thread.v2", null, null);
        public ApprovedResource Group { get; } = new("group", "chat", "Group", null, "19:group@thread.v2", null, null);
        public ApprovedResource Channel { get; } = new("channel", "channel", "General", "Team", null, "team", "channel");
        public LiveOptions Options { get; }
        public LiveStateStore Store { get; }
        public Source Source { get; }
        public GraphPolicyFeed Feed { get; }
        public Fixture()
        {
            Options = new()
            {
                AuthorityMode = "Policy", EntitlementProvider = "Graph", Resources = [Direct, Group, Channel], WriterEpoch = 1,
                DirectChats = [new(Direct.Id, Actors[0], Actors[1])],
                GraphPolicy = new()
                {
                    ApprovalReference = "approved-membership-and-delegated-send-boundary", WebServicePrincipalId = WebPrincipal,
                    AdmittedUserIds = Actors.ToArray(), SendEnabledResources = [Direct.Id, Group.Id, Channel.Id],
                    HistoryFrom = new() { [Direct.Id] = Clock.GetUtcNow().AddDays(-1), [Group.Id] = Clock.GetUtcNow().AddDays(-1),
                        [Channel.Id] = Clock.GetUtcNow().AddDays(-1) }
                }
            };
            Store = new(new MemoryPartitionDocuments(), Clock);
            foreach (var resource in Options.Resources)
                Store.CreateAsync(new(resource.Id, Tenant, resource.Title, resource.Kind, resource.Team, [], [], [], Epoch: 1), default).GetAwaiter().GetResult();
            Source = new(Clock);
            Feed = new(Source, Source, Store, Options, Tenant, Web, Clock, NullLogger<GraphPolicyFeed>.Instance);
        }
    }
    private sealed class Source(Clock clock) : IGraphBackground, IGraphPolicyReader
    {
        public string Denial = "";
        public readonly List<string> Reads = [];
        public Task<JsonElement> GetAsync(string path, CancellationToken ct) => ReadAsync(path, ct);
        public Task<JsonElement> ReadChannelAsync(ApprovedResource resource, CancellationToken ct) =>
            ReadAsync(GraphUrls.ChannelMetadata(resource.GraphTeamId!, resource.GraphChannelId!).AbsoluteUri, ct);
        public Task<JsonElement> ReadAsync(string path, CancellationToken ct)
        {
            Reads.Add(path);
            var uri = new Uri(path, UriKind.RelativeOrAbsolute);
            var route = uri.IsAbsoluteUri && uri.AbsolutePath.StartsWith("/beta/", StringComparison.Ordinal) ?
                GraphMessages.NormalizeResource(uri.AbsolutePath["/beta/".Length..]) :
                GraphUrls.ResourcePath(GraphUrls.Validate(path));
            if (Denial == "policy-unavailable") throw new AppError(503, "graph_503", "Source unavailable.");
            object value;
            if (route == $"servicePrincipals/{WebPrincipal}")
                value = new { id = WebPrincipal, appId = Denial == "invalid-app" ? "other" : Web,
                    appRoleAssignmentRequired = Denial != "assignment-disabled", accountEnabled = Denial != "web-disabled" };
            else if (route.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal))
                value = new { value = Actors.Where(actor => Denial != "unassigned" || actor != Actors[0]).Select(actor => new
                    { resourceId = WebPrincipal, principalId = actor, principalType = "User", appRoleId = Guid.Empty.ToString("D") }).ToArray() };
            else if (route.StartsWith("users/", StringComparison.Ordinal))
            {
                var actor = route["users/".Length..];
                if (actor == Actors[0] && Denial == "deleted") throw new AppError(404, "graph_404", "Account absent.");
                value = new { id = Denial == "invalid-user" ? "other" : actor, displayName = actor,
                    accountEnabled = actor != Actors[0] || Denial != "disabled", userType = actor == Actors[0] && Denial == "guest" ? "Guest" : "Member" };
            }
            else if (route == "teams/team") value = new { id = "team", isArchived = Denial == "archived" };
            else if (route == "teams/team/channels/channel")
            {
                Assert.StartsWith("/beta/", uri.AbsolutePath);
                var channel = new Dictionary<string, object?> { ["id"] = "channel",
                    ["membershipType"] = Denial == "private-channel" ? "private" : "standard",
                    ["isArchived"] = Denial == "archived-channel" };
                if (Denial != "missing-moderation") channel["moderationSettings"] = new {
                    userNewMessageRestriction = Denial == "moderated" ? "moderators" : "everyone",
                    replyRestriction = Denial == "reply-restricted" ? "authorAndModerators" : "everyone"
                };
                value = channel;
            }
            else if (route.EndsWith("/members", StringComparison.Ordinal))
            {
                var direct = route.StartsWith("chats/19%3Adirect", StringComparison.Ordinal);
                var ids = direct ? Actors.Take(2) : Actors;
                if (direct && Denial == "wrong-pair") ids = Actors.Skip(1);
                value = new { value = ids.Select(actor => new { userId = actor,
                    tenantId = Denial == "foreign-member" ? "other-tenant" : Tenant,
                    visibleHistoryStartDateTime = Denial == "history" ? (DateTimeOffset?)null :
                        actor == Actors[2] ? clock.GetUtcNow().AddHours(-2) : clock.GetUtcNow().AddDays(-1) }).ToArray() };
            }
            else value = new { id = route.Contains("direct", StringComparison.Ordinal) ? "19:direct@thread.v2" : "19:group@thread.v2",
                chatType = route.Contains("direct", StringComparison.Ordinal) && Denial != "wrong-chat-type" ? "oneOnOne" : "group",
                createdDateTime = clock.GetUtcNow().AddDays(-2) };
            return Task.FromResult(JsonSerializer.SerializeToElement(value));
        }
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("No background sends.");
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("No unscoped mutation.");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-only-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class UnavailableCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new Azure.Identity.CredentialUnavailableException("PRIVATE token preparation details.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromException<AccessToken>(new Azure.Identity.CredentialUnavailableException("PRIVATE token preparation details."));
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
