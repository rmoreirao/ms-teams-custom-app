using Microsoft.AspNetCore.DataProtection;
using System.Text;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class MessagingTests
{
    private readonly Clock clock = new();
    private readonly MemoryStateStore store = new();
    private readonly Actor alex = new("tenant", "alex", "Alex");
    private readonly Actor sam = new("tenant", "sam", "Sam");
    private readonly ICommandTokens tokens;
    private readonly Simulator simulator;
    private readonly Messaging service;
    public MessagingTests()
    {
        tokens = new CommandTokens(new EphemeralDataProtectionProvider(), clock);
        simulator = new(clock, store);
        service = new(store, simulator, new MemoryBudget(clock), tokens, clock);
        Fixtures.SeedAsync(store, "tenant", ["alex", "sam", "jordan", "taylor"], clock, default).GetAwaiter().GetResult();
    }
    private async Task<SendCommand> Command(string body = "A synthetic message", string? parent = null)
    {
        var page = await service.ReadAsync(alex, parent is null ? "chat-handover" : "channel-general", null, null, 0, default);
        return new(Guid.NewGuid().ToString(), page.Window, body, parent);
    }
    [Fact]
    public async Task ReplayingConfirmedOperationDoesNotSendTwice()
    {
        var command = await Command();
        var first = await service.SendAsync(alex, "chat-handover", command, default);
        clock.Advance(TimeSpan.FromHours(1));
        var replay = await service.SendAsync(alex, "chat-handover", command, default);
        Assert.Equal(OperationState.Confirmed, first.State);
        Assert.Equal(first, replay);
        var conversation = await store.GetAsync("tenant", "chat-handover", default);
        Assert.Single(conversation!.Value.Messages, m => m.Body == command.Body);
        Assert.Equal("", (await store.GetOperationAsync("tenant", "chat-handover", command.OperationId, default))!.Value.Body);
    }
    [Fact]
    public async Task SameIdWithDifferentPayloadConflicts()
    {
        var command = await Command();
        await service.SendAsync(alex, "chat-handover", command, default);
        var error = await Assert.ThrowsAsync<AppError>(() => service.SendAsync(alex, "chat-handover", command with { Body = "changed" }, default));
        Assert.Equal(409, error.Status);
    }
    [Fact]
    public async Task UnknownResolvesOriginalReceiptWithoutResending()
    {
        simulator.Next(alex.Id, "unknown");
        var command = await Command();
        var unknown = await service.SendAsync(alex, "chat-handover", command, default);
        Assert.Equal(OperationState.Unknown, unknown.State);
        Assert.Equal(unknown, await service.SendAsync(alex, "chat-handover", command, default));
        var confirmed = await service.StatusAsync(alex, "chat-handover", command.OperationId, default);
        Assert.Equal(OperationState.Confirmed, confirmed.State);
        Assert.Single((await store.GetAsync("tenant", "chat-handover", default))!.Value.Messages, m => m.Body == command.Body);
    }
    [Fact]
    public async Task RejectedSendCreatesNoMessage()
    {
        simulator.Next(alex.Id, "reject");
        var command = await Command();
        Assert.Equal(OperationState.Rejected, (await service.SendAsync(alex, "chat-handover", command, default)).State);
        Assert.DoesNotContain((await store.GetAsync("tenant", "chat-handover", default))!.Value.Messages, m => m.Body == command.Body);
    }
    [Fact]
    public async Task NonmembersAndCrossTenantActorsCannotRead()
    {
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(new("tenant", "jordan", "Jordan"), "chat-handover", null, null, 0, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(alex with { TenantId = "other" }, "chat-handover", null, null, 0, default))).Status);
    }
    [Fact]
    public async Task OperationStatusBelongsToItsActor()
    {
        var command = await Command();
        await service.SendAsync(alex, "chat-handover", command, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.StatusAsync(sam, "chat-handover", command.OperationId, default))).Status);
    }
    [Fact]
    public async Task RevokedMembershipDeniesAlreadyOpenedView()
    {
        await service.ReadAsync(sam, "chat-handover", null, null, 0, default);
        var current = (await store.GetAsync("tenant", "chat-handover", default))!;
        await store.SaveAsync(current with
        {
            Value = current.Value with
            {
                Members = current.Value.Members.Select(m => m.Id == sam.Id ? m with { Active = false } : m).ToList()
            }
        }, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(sam, "chat-handover", null, null, 0, default))).Status);
    }
    [Fact]
    public async Task EqualTimestampPaginationHasNoGapsOrDuplicates()
    {
        var current = (await store.GetAsync("tenant", "chat-handover", default))!;
        await store.SaveAsync(current with
        {
            Value = current.Value with
            {
                Messages = current.Value.Messages.Select(m => m with { CreatedAt = clock.GetUtcNow() }).ToList()
            }
        }, default);
        List<string> ids = [];
        string? cursor = null;
        do
        {
            var page = await service.ReadAsync(alex, "chat-handover", cursor, null, 0, default);
            ids.AddRange(page.Messages.Select(m => m.Id));
            cursor = page.Cursor;
        } while (cursor is not null);
        Assert.Equal(45, ids.Count);
        Assert.Equal(45, ids.Distinct().Count());
    }
    [Fact]
    public async Task HistoryRestrictionFiltersContentAndThreadParents()
    {
        var current = (await store.GetAsync("tenant", "channel-general", default))!;
        var cutoff = clock.GetUtcNow().AddMinutes(-68);
        await store.SaveAsync(current with
        {
            Value = current.Value with
            {
                Members = current.Value.Members.Select(m => m.Id == alex.Id ? m with { HistoryFrom = cutoff } : m).ToList()
            }
        }, default);
        var page = await service.ReadAsync(alex, "channel-general", null, null, 0, default);
        Assert.All(page.Messages, m => Assert.True(m.CreatedAt >= cutoff));
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(alex, "channel-general", null, "fixture-51", 0, default))).Status);
    }
    [Fact]
    public async Task MissingCoverageIsNotFabricatedEmptyContent()
    {
        var current = (await store.GetAsync("tenant", "chat-empty", default))!;
        await store.SaveAsync(current with { Value = current.Value with { CoverageReady = false } }, default);
        Assert.Equal("projection_preparing", (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(alex, "chat-empty", null, null, 0, default))).Code);
    }
    [Fact]
    public async Task CommandTokensAreActorConversationPurposeAndExpiryBound()
    {
        var command = await Command();
        Assert.Throws<AppError>(() => tokens.Validate(sam, "chat-handover", "command:0", command.Window));
        Assert.Throws<AppError>(() => tokens.Validate(alex, "chat-empty", "command:0", command.Window));
        Assert.Throws<AppError>(() => tokens.Validate(alex with { SessionId = "different" }, "chat-handover", "command:0", command.Window));
        Assert.Throws<AppError>(() => tokens.Validate(alex, "chat-handover", "page:", command.Window));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(410, (await Assert.ThrowsAsync<AppError>(() =>
            service.SendAsync(alex, "chat-handover", command, default))).Status);
    }
    [Fact]
    public async Task ConcurrentIdenticalSubmissionsHaveOneSideEffect()
    {
        var command = await Command();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.SendAsync(alex, "chat-handover", command, default)));
        Assert.All(results, result => Assert.Equal(OperationState.Confirmed, result.State));
        Assert.Single((await store.GetAsync("tenant", "chat-handover", default))!.Value.Messages, m => m.Body == command.Body);
    }
    [Fact]
    public async Task DuplicateWhileBudgetWaitsReturnsPendingRatherThanAnUnsafeCapacityRejection()
    {
        var budget = new WaitingBudget();
        var delayed = new Messaging(store, simulator, budget, tokens, clock);
        var command = await Command();
        var owner = delayed.SendAsync(alex, "chat-handover", command, default);
        await budget.Entered.Task;
        var duplicate = await delayed.SendAsync(alex, "chat-handover", command, default);
        Assert.Equal(OperationState.Pending, duplicate.State);
        budget.Release.SetResult();
        Assert.Equal(OperationState.Confirmed, (await owner).State);
        Assert.Single((await store.GetAsync("tenant", "chat-handover", default))!.Value.Messages, m => m.Body == command.Body);
    }
    [Fact]
    public async Task ExpiringAPendingClaimPreventsItsOriginalOwnerFromDispatching()
    {
        var budget = new WaitingBudget();
        var delayed = new Messaging(store, simulator, budget, tokens, clock);
        var command = await Command();
        var owner = delayed.SendAsync(alex, "chat-handover", command, default);
        await budget.Entered.Task;
        clock.Advance(TimeSpan.FromSeconds(36));
        Assert.Equal(OperationState.Rejected, (await delayed.StatusAsync(alex, "chat-handover", command.OperationId, default)).State);
        budget.Release.SetResult();
        Assert.Equal(OperationState.Rejected, (await owner).State);
        Assert.DoesNotContain((await store.GetAsync("tenant", "chat-handover", default))!.Value.Messages, m => m.Body == command.Body);
    }
    [Fact]
    public void DuplicateAndOutOfOrderNotificationsDoNotResurrectDeletedMessages()
    {
        var conversation = Fixtures.Create("tenant", ["alex", "sam", "jordan", "taylor"], clock.GetUtcNow())[0];
        var original = conversation.Messages[0];
        Assert.Same(conversation, Projection.Apply(conversation, original, clock.GetUtcNow()));
        var deleted = original with { Deleted = true, Body = "", SourceVersion = "2", ModifiedAt = clock.GetUtcNow() };
        var changed = Projection.Apply(conversation, deleted, clock.GetUtcNow());
        var replay = Projection.Apply(changed, original, clock.GetUtcNow());
        Assert.Same(changed, replay);
        Assert.True(replay.Messages.Single(m => m.Id == original.Id).Deleted);
        Assert.Equal("", replay.Messages.Single(m => m.Id == original.Id).Body);
    }
    [Fact]
    public async Task SeedIsRepeatableWithoutResettingMessages()
    {
        Assert.Equal(0, await Fixtures.SeedAsync(store, "tenant", ["alex", "sam", "jordan", "taylor"], clock, default));
        Assert.Equal(6, (await store.ListAsync("tenant", default)).Count);
    }
    [Fact]
    public async Task SimulatorRestartRetainsExactUnknownReceipt()
    {
        simulator.Next(alex.Id, "unknown");
        var command = await Command();
        await service.SendAsync(alex, "chat-handover", command, default);
        var restarted = new Messaging(store, new Simulator(clock, store), new MemoryBudget(clock), tokens, clock);
        Assert.Equal(OperationState.Confirmed, (await restarted.StatusAsync(alex, "chat-handover", command.OperationId, default)).State);
    }
    [Fact]
    public async Task OffboardedPrincipalCannotReadOrListDespiteExistingMembership()
    {
        await store.SetBlockedAsync("tenant", alex.Id, true, default);
        Assert.Equal(401, (await Assert.ThrowsAsync<AppError>(() => service.ListAsync(alex, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<AppError>(() => service.ReadAsync(alex, "chat-handover", null, null, 0, default))).Status);
        await store.SetBlockedAsync("tenant", alex.Id, false, default);
        Assert.NotEmpty(await service.ListAsync(alex, default));
    }
    [Fact]
    public async Task ResetInvalidatesOldCommandWindowsWithoutLosingOperationEvidence()
    {
        var command = await Command();
        await Fixtures.ResetAsync(store, "tenant", ["alex", "sam", "jordan", "taylor"], clock, default);
        Assert.Equal(410, (await Assert.ThrowsAsync<AppError>(() => service.SendAsync(alex, "chat-handover", command, default))).Status);
        await Fixtures.VerifyAsync(store, "tenant", ["alex", "sam", "jordan", "taylor"], default);
    }
    [Fact]
    public void ProjectionRemainsBelowCosmosItemLimitForWorstCaseUnicode()
    {
        var value = Fixtures.Create("tenant", ["alex", "sam", "jordan", "taylor"], clock.GetUtcNow())[0];
        for (var i = 0; i < 200; i++)
            value = Projection.Apply(value, new(i.ToString(), "alex", "Alex", new string('\u65e5', 4096),
                clock.GetUtcNow().AddSeconds(i), clock.GetUtcNow()), clock.GetUtcNow());
        Assert.True(Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(value)) < 1_100_000);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
    private sealed class WaitingBudget : ICommandBudget
    {
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource Release { get; } = new();
        public async Task AcquireAsync(Actor actor, Conversation conversation, CancellationToken ct)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(ct);
        }
    }
}
