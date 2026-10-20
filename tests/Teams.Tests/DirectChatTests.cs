using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class DirectChatTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly string[] Actors = [
        "22222222-2222-2222-2222-222222222222",
        "33333333-3333-3333-3333-333333333333",
        "44444444-4444-4444-4444-444444444444"
    ];
    private static readonly string[] Names = ["Alex", "Sam", "Jordan"];
    private readonly TestClock clock = new();
    private readonly LiveStateStore store;
    private readonly LiveOptions options = new() { AdmissionEnabled = true, SynchronizationEnabled = true };
    public DirectChatTests()
    {
        store = new(new MemoryPartitionDocuments(), clock);
        var resources = new List<ApprovedResource>();
        var pairs = new List<ApprovedDirectChat>();
        for (var first = 0; first < 3; first++)
            for (var second = first + 1; second < 3; second++)
            {
                var id = $"approved-{first}-{second}";
                resources.Add(new(id, "chat", "Approved direct pair", null, $"graph-{id}", null, null));
                pairs.Add(new(id, Actors[first], Actors[second]));
            }
        options.Resources = resources.ToArray();
        options.DirectChats = pairs.ToArray();
    }
    private LiveDiscovery Service => new(store, options, Tenant, NullLogger<LiveDiscovery>.Instance, clock);
    private async Task PrepareAsync()
    {
        foreach (var pair in options.DirectChats)
        {
            var resource = options.Resources.Single(r => r.Id == pair.ResourceId);
            await store.CreateAsync(new(resource.Id, Tenant, resource.Title, "chat", null, [], [], [],
                GraphChatId: resource.GraphChatId, CoverageUntil: clock.GetUtcNow().AddMinutes(5)), default);
            foreach (var actor in new[] { pair.FirstActorId, pair.SecondActorId })
                await store.SetEntitlementAsync(new(Tenant, resource.Id, actor, Names[Array.IndexOf(Actors, actor)],
                    clock.GetUtcNow().AddHours(-1), clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5),
                    $"source-{resource.Id}-{actor}", true, true, 0), default);
            await store.SetDirectChatAuthorityAsync(new(Tenant, resource.Id, resource.GraphChatId!, "oneOnOne",
                [pair.FirstActorId, pair.SecondActorId], clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5),
                $"verified-{resource.Id}"), default);
        }
    }
    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 0)]
    public async Task EveryApprovedPairReusesItsExistingMappingAndDeniesTheThirdUser(int first, int second, int excluded)
    {
        await PrepareAsync();
        var actor = new Actor(Tenant, Actors[first], Names[first]);
        var other = new Actor(Tenant, Actors[second], Names[second]);
        var opened = await Service.OpenAsync(actor, other.Id, default);
        Assert.Equal(opened.Id, (await Service.OpenAsync(other, actor.Id, default)).Id);
        Assert.Equal(Names[second], opened.Title);
        var outsider = new Actor(Tenant, Actors[excluded], Names[excluded]);
        Assert.DoesNotContain((await Service.CatalogAsync(outsider, null, default)).Items, chat => chat.Id == opened.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            store.GetMemberAsync(outsider, opened.Id, false, default))).Status);
        Assert.Empty(await store.PendingOutboxAsync(Tenant, default));
        Assert.Empty(await store.MessagesAsync(actor, opened.Id, null, clock.GetUtcNow().AddDays(-7), null, null, default));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EachPersonDiscoversOnlyTheTwoOtherApprovedContacts(int index)
    {
        await PrepareAsync();
        var actor = new Actor(Tenant, Actors[index], Names[index]);
        var people = await Service.PeopleAsync(actor, default);
        Assert.Equal(Actors.Where(id => id != actor.Id).Order(), people.Select(person => person.Id).Order());
        Assert.All(people, person => Assert.Null(person.Email));
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, actor.Id, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() => Service.OpenAsync(actor, Guid.NewGuid().ToString(), default))).Status);
    }
    [Fact]
    public async Task ExpiredPairEvidenceNeverCreatesAReplacementOrFabricatesAnEmptyChat()
    {
        await PrepareAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        var actor = new Actor(Tenant, Actors[0], Names[0]);
        Assert.Equal("authorization_stale", (await Assert.ThrowsAsync<AppError>(() =>
            Service.OpenAsync(actor, Actors[1], default))).Code);
        Assert.Empty(await store.PendingOutboxAsync(Tenant, default));
    }
}
