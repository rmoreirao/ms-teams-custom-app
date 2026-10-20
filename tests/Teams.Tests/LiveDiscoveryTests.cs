using System.Net;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class LiveDiscoveryTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Alex = "22222222-2222-2222-2222-222222222222";
    private const string Sam = "33333333-3333-3333-3333-333333333333";
    private const string Jordan = "44444444-4444-4444-4444-444444444444";
    private readonly Clock clock = new();
    private readonly LiveStateStore store;
    private readonly LiveOptions options = new()
    {
        AdmissionEnabled = true, SynchronizationEnabled = true,
        Resources = [new("approved-pair", "chat", "Approved pair", null, "actual-chat", null, null)],
        DirectChats = [new("approved-pair", Alex, Sam)]
    };
    private readonly Actor actor = new(Tenant, Alex, "Alex");
    private LiveDiscovery Service => new(store, options, Tenant, NullLogger<LiveDiscovery>.Instance, clock);
    public LiveDiscoveryTests() => store = new(new MemoryPartitionDocuments(), clock);
    private async Task Prepare(bool ready = true, bool verifiedPair = true)
    {
        await store.CreateAsync(new("approved-pair", Tenant, "Approved pair", "chat", null, [], [], [],
            GraphChatId: "actual-chat", CoverageReady: ready, CoverageUntil: clock.GetUtcNow().AddMinutes(5)), default);
        foreach (var (id, name) in new[] { (Alex, "Alex"), (Sam, "Sam") })
            await store.SetEntitlementAsync(new(Tenant, "approved-pair", id, name, clock.GetUtcNow().AddDays(-7),
                clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), "verified-authority", true, true, 0), default);
        if (verifiedPair) await store.SetDirectChatAuthorityAsync(PairEvidence(), default);
    }
    private DirectChatAuthority PairEvidence() => new(Tenant, "approved-pair", "actual-chat", "oneOnOne",
        [Alex, Sam], clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), "verified-exact-pair");
    [Fact]
    public async Task ApprovedContactsUseCurrentAuthorityAndOpeningNeverCreatesOrSends()
    {
        await Prepare();
        var person = Assert.Single(await Service.PeopleAsync(actor, default));
        Assert.Equal(Sam, person.Id);
        Assert.Null(person.Email);
        var chat = await Service.OpenAsync(actor, Sam, default);
        Assert.Equal("approved-pair", chat.Id);
        Assert.Equal("Sam", chat.Title);
        Assert.True(chat.IsDirect);
        Assert.Empty(await store.PendingOutboxAsync(Tenant, default));
        Assert.Empty(await store.MessagesAsync(actor, chat.Id, null, clock.GetUtcNow().AddDays(-7), null, null, default));
    }
    [Theory]
    [InlineData(Alex, "Sam")]
    [InlineData(Sam, "Alex")]
    public async Task CatalogShowsOnlyTheOtherPersonAndMatchesTheOpenedChat(string actorId, string expectedTitle)
    {
        await Prepare();
        var viewer = actor with { Id = actorId, Name = "Not used to identify the other person" };
        var chat = Assert.Single((await Service.CatalogAsync(viewer, null, default)).Items);
        Assert.Equal(expectedTitle, chat.Title);
        Assert.True(chat.IsDirect);
        var otherId = actorId == Alex ? Sam : Alex;
        Assert.Equal((await Service.OpenAsync(viewer, otherId, default)).Title, chat.Title);
        Assert.Equal("Approved pair", (await store.GetAsync(Tenant, chat.Id, default))!.Value.Title);
    }
    [Theory]
    [InlineData("Alex")]
    [InlineData("Alex and Sam")]
    public async Task CatalogUsesParticipantIdsWithoutEditingTheOtherPersonsName(string otherName)
    {
        await Prepare();
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SetEntitlementAsync(new(Tenant, "approved-pair", Sam, otherName, clock.GetUtcNow().AddDays(-7),
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), "updated-name", true, true, 0), default);
        Assert.Equal(otherName, Assert.Single((await Service.CatalogAsync(actor, null, default)).Items).Title);
    }
    [Fact]
    public async Task CatalogPreservesGroupChatAndChannelTitles()
    {
        await Prepare();
        foreach (var (id, kind, title) in new[] {
            ("group", "chat", "Alex and Sam project group"), ("channel", "channel", "Alex and Sam updates")
        })
        {
            await store.CreateAsync(new(id, Tenant, title, kind, kind == "channel" ? "Team" : null, [], [], []), default);
            await store.SetEntitlementAsync(new(Tenant, id, Alex, "Alex", clock.GetUtcNow().AddDays(-7),
                clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), "verified-authority", true, true, 0), default);
        }
        var page = await Service.CatalogAsync(actor, null, default);
        Assert.Equal("Sam", page.Items.Single(chat => chat.Id == "approved-pair").Title);
        Assert.Equal("Alex and Sam project group", page.Items.Single(chat => chat.Id == "group").Title);
        Assert.Equal("Alex and Sam updates", page.Items.Single(chat => chat.Id == "channel").Title);
        Assert.Equal("Team", page.Items.Single(chat => chat.Id == "channel").Team);
        Assert.False(page.Items.Single(chat => chat.Id == "group").IsDirect);
        Assert.False(page.Items.Single(chat => chat.Id == "channel").IsDirect);
        options.DirectChats = [];
        var unverified = (await Service.CatalogAsync(actor, null, default)).Items.Single(chat => chat.Id == "approved-pair");
        Assert.Equal("Approved pair", unverified.Title);
        Assert.False(unverified.IsDirect);
    }
    [Fact]
    public async Task CatalogCannotLabelAGroupAsOneToOneUsingOnlyTwoIndividualGrants()
    {
        await Prepare(verifiedPair: false);
        Assert.Equal("direct_chat_authority_required",
            (await Assert.ThrowsAsync<AppError>(() => Service.CatalogAsync(actor, null, default))).Code);
    }
    [Fact]
    public async Task ArbitraryRecipientsAndNonmembersCannotDiscoverOrOpenChats()
    {
        await Prepare();
        Assert.Empty(await Service.PeopleAsync(actor with { Id = Jordan }, default));
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Jordan, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor with { TenantId = "other" }, Sam, default))).Status);
    }
    [Fact]
    public async Task OffboardedRecipientIsOmittedWithoutExpiringTheCallersSession()
    {
        await Prepare();
        await store.SetBlockedAsync(Tenant, Sam, true, default);
        Assert.Empty(await Service.PeopleAsync(actor, default));
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Sam, default))).Status);
        Assert.False(await store.IsBlockedAsync(Tenant, Alex, default));
    }
    [Fact]
    public async Task StaleAuthorityFailsExplicitlyInsteadOfInventingDirectoryResults()
    {
        await Prepare();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("authorization_stale", (await Assert.ThrowsAsync<AppError>(() => Service.PeopleAsync(actor, default))).Code);
        Assert.Equal("authorization_stale", (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Sam, default))).Code);
    }
    [Fact]
    public async Task UncoveredChatCannotPretendToBeAnEmptyReadyComposer()
    {
        await Prepare(false);
        Assert.Equal("projection_preparing", (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Sam, default))).Code);
        options.AdmissionEnabled = false;
        Assert.Equal("admission_disabled", (await Assert.ThrowsAsync<AppError>(() => Service.PeopleAsync(actor, default))).Code);
    }
    [Fact]
    public async Task ConfigurationAndTwoIndividualGrantsCannotProveAChatIsPrivate()
    {
        await Prepare(verifiedPair: false);
        Assert.Equal("direct_chat_authority_required", (await Assert.ThrowsAsync<AppError>(() => Service.PeopleAsync(actor, default))).Code);
        Assert.Equal("direct_chat_authority_required", (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Sam, default))).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetDirectChatAuthorityAsync(PairEvidence() with { ChatType = "group" }, default));
        await store.SetDirectChatAuthorityAsync(PairEvidence() with { MemberIds = [Alex, Jordan] }, default);
        Assert.Equal("direct_chat_authority_required", (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Sam, default))).Code);
    }
    [Fact]
    public async Task PairAuthorityCannotRenewStaleEvidenceOrRetargetTheGraphChat()
    {
        await Prepare();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetDirectChatAuthorityAsync(PairEvidence() with { FreshUntil = clock.GetUtcNow().AddMinutes(2) }, default));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetDirectChatAuthorityAsync(PairEvidence() with { GraphChatId = "different-chat" }, default));
    }
    [Fact]
    public async Task ExpiredPairEvidenceFailsEvenWhileBothIndividualGrantsRemainFresh()
    {
        await Prepare(verifiedPair: false);
        await store.SetDirectChatAuthorityAsync(PairEvidence() with { FreshUntil = clock.GetUtcNow().AddSeconds(30) }, default);
        clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal("direct_chat_authority_required", (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Sam, default))).Code);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("group")]
    [InlineData("wrong-member")]
    [InlineData("wrong-resource")]
    [InlineData("wrong-graph")]
    public async Task PolicyHttpAdapterRejectsInvalidPairAssertionsBeforePersistence(string failure)
    {
        var evidence = failure switch
        {
            "missing" => null,
            "group" => PairEvidence() with { ChatType = "group" },
            "wrong-member" => PairEvidence() with { MemberIds = [Alex, Jordan] },
            "wrong-resource" => PairEvidence() with { ResourceId = "other" },
            "wrong-graph" => PairEvidence() with { GraphChatId = "other" },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal("direct_chat_authority_required", (await Assert.ThrowsAsync<AppError>(() =>
            Feed(evidence).ReadAsync(options.Resources[0], null, default))).Code);
        Assert.Null(await store.DirectChatAuthorityAsync(Tenant, "approved-pair", default));
    }
    [Fact]
    public async Task PolicyHttpAdapterAcceptsOnlyTheBoundAuthorityResourceAndTokenScope()
    {
        var page = await Feed(PairEvidence()).ReadAsync(options.Resources[0], null, default);
        Assert.Equal("oneOnOne", page.DirectChat!.ChatType);
        Assert.Equal([Alex, Sam], page.DirectChat.MemberIds);
        await store.SetDirectChatAuthorityAsync(page.DirectChat, default);
        Assert.NotNull(await store.DirectChatAuthorityAsync(Tenant, "approved-pair", default));
    }
    private EntitlementFeed Feed(DirectChatAuthority? evidence)
    {
        options.EntitlementAuthority = "https://policy.invalid/base";
        options.EntitlementScope = "api://policy/.default";
        return new(new HttpClient(new PolicyHandler(new(Tenant, "approved-pair", [], null, evidence))),
            new PolicyCredential(), options, Tenant);
    }
    [Fact]
    public void PairConfigurationRejectsUnknownResourcesDuplicatePairsAndSelfChat()
    {
        options.ValidateDirectChats();
        foreach (var pairs in new ApprovedDirectChat[][] {
            [new("unknown", Alex, Sam)], [new("approved-pair", Alex, Alex)],
            [new("approved-pair", Alex, Sam), new("approved-pair", Sam, Alex)],
            [new("approved-pair", "invalid", Sam)]
        })
        {
            options.DirectChats = pairs;
            Assert.Throws<InvalidOperationException>(options.ValidateDirectChats);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    private sealed class PolicyHandler(AuthorityPage page) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://policy.invalid/base/resources/approved-pair/entitlements", request.RequestUri!.AbsoluteUri);
            Assert.Equal("controlled-policy-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) });
        }
    }
    private sealed class PolicyCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            Assert.Equal(["api://policy/.default"], context.Scopes);
            return new("controlled-policy-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            ValueTask.FromResult(GetToken(context, ct));
    }
}
