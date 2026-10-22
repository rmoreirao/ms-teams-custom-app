using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan elapsed) => now += elapsed;
}

public sealed class LiveFixture
{
    public TestClock Clock { get; } = new();
    public LiveStateStore Store { get; }
    public string Tenant { get; }
    public Actor Alex { get; }
    public Actor Sam { get; }
    public Actor Jordan { get; }
    public LiveFixture(string tenant = "tenant", string alex = "alex", string sam = "sam", string jordan = "jordan",
        IPartitionDocuments? documents = null, IRecentMessageCache? cache = null)
    {
        Tenant = tenant;
        Alex = new(tenant, alex, "Alex", "alex-session");
        Sam = new(tenant, sam, "Sam", "sam-session");
        Jordan = new(tenant, jordan, "Jordan", "jordan-session");
        Store = new(documents ?? new MemoryPartitionDocuments(), Clock, cache: cache);
        InitializeAsync().GetAwaiter().GetResult();
    }
    private async Task InitializeAsync()
    {
        foreach (var (id, kind) in new[] { ("chat-handover", "chat"), ("chat-empty", "chat"), ("channel-general", "channel") })
        {
            await Store.CreateAsync(new(id, Tenant, id, kind, null, [], [], [], Epoch: 1,
                GraphChatId: kind == "chat" ? id : null, GraphTeamId: kind == "channel" ? "team" : null,
                GraphChannelId: kind == "channel" ? id : null, CoverageUntil: Clock.GetUtcNow().AddMinutes(5)), default);
        }
        await RenewAsync();
        for (var index = 0; index < 45; index++)
            await Store.ApplyAsync(Tenant, "chat-handover", new($"message-{index:D2}", Sam.Id, Sam.Name, $"Context {index}",
                Clock.GetUtcNow().AddMinutes(-60 + index), Clock.GetUtcNow().AddMinutes(-60 + index)), default);
        await Store.ApplyAsync(Tenant, "channel-general", new("root", Sam.Id, Sam.Name, "Channel parent",
            Clock.GetUtcNow().AddHours(-1), Clock.GetUtcNow().AddHours(-1)), default);
    }
    public async Task RenewAsync()
    {
        foreach (var id in new[] { "chat-handover", "chat-empty", "channel-general" })
        {
            var value = (await Store.GetAsync(Tenant, id, default))!;
            await Store.SaveAsync(value with { Value = value.Value with { CoverageUntil = Clock.GetUtcNow().AddMinutes(5) } }, default);
            foreach (var actor in new[] { Alex, Sam })
                await Store.SetEntitlementAsync(new(Tenant, id, actor.Id, actor.Name, Clock.GetUtcNow().AddDays(-7),
                    Clock.GetUtcNow(), Clock.GetUtcNow().AddMinutes(5), $"evidence-{Clock.GetUtcNow():O}", true, true, value.Value.Epoch), default);
        }
    }
}
