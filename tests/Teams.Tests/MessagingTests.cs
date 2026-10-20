using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class MessagingTests
{
    private readonly LiveFixture fixture = new();
    private readonly ICommandTokens tokens;
    private readonly GraphResultStub graph;
    private readonly Messaging service;
    private Actor Alex => fixture.Alex;
    public MessagingTests()
    {
        tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        graph = new(fixture.Clock);
        service = new(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock);
    }
    private async Task<SendCommand> Command(string body = "Test message", string? parent = null)
    {
        var page = await service.ReadAsync(Alex, parent is null ? "chat-handover" : "channel-general", null, null, 0, default);
        return new(Guid.NewGuid().ToString(), page.Window, body, parent);
    }
    [Fact]
    public async Task ReplayingConfirmedOperationDoesNotDispatchOrPersistBodyTwice()
    {
        var command = await Command();
        var first = await service.SendAsync(Alex, "chat-handover", command, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(first, await service.SendAsync(Alex, "chat-handover", command, default));
        Assert.Equal(OperationState.Confirmed, first.State);
        Assert.Equal(1, graph.Sends);
        Assert.Equal("", (await fixture.Store.GetOperationAsync(fixture.Tenant, "chat-handover", command.OperationId, default))!.Value.Body);
        Assert.Single(await fixture.Store.MessagesAsync(Alex, "chat-handover", null, fixture.Clock.GetUtcNow().AddDays(-7), null, null, default),
            m => m.Body == command.Body);
    }
    [Fact]
    public async Task SameOperationWithDifferentPayloadConflicts()
    {
        var command = await Command();
        await service.SendAsync(Alex, "chat-handover", command, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<AppError>(() =>
            service.SendAsync(Alex, "chat-handover", command with { Body = "changed" }, default))).Status);
    }
    [Fact]
    public async Task UnknownOutcomeRemainsUnknownWithoutUnsafeResendOrFabricatedReceipt()
    {
        graph.NextResult = OperationState.Unknown;
        var command = await Command();
        var result = await service.SendAsync(Alex, "chat-handover", command, default);
        Assert.Equal(OperationState.Unknown, result.State);
        Assert.Equal(result, await service.SendAsync(Alex, "chat-handover", command, default));
        Assert.Equal(result, await service.StatusAsync(Alex, "chat-handover", command.OperationId, default));
        Assert.Equal(1, graph.Sends);
        Assert.Null(await fixture.Store.MessageAsync(fixture.Tenant, "chat-handover", command.OperationId, null, default));
    }
    [Fact]
    public async Task RejectedSendCreatesNoMessage()
    {
        graph.NextResult = OperationState.Rejected;
        var command = await Command();
        Assert.Equal(OperationState.Rejected, (await service.SendAsync(Alex, "chat-handover", command, default)).State);
        Assert.Null(await fixture.Store.MessageAsync(fixture.Tenant, "chat-handover", command.OperationId, null, default));
    }
    [Fact]
    public async Task NonmembersAndCrossTenantActorsCannotRead()
    {
        foreach (var actor in new[] { fixture.Jordan, Alex with { TenantId = "other" } })
            Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
                service.ReadAsync(actor, "chat-handover", null, null, 0, default))).Status);
    }
    [Fact]
    public async Task LateMemberSeesOnlyAuthorizedHistory()
    {
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.SetEntitlementAsync(new(fixture.Tenant, "chat-handover", fixture.Jordan.Id, fixture.Jordan.Name,
            now.AddMinutes(-20), now, now.AddMinutes(5), "late-member", true, false, 1), default);
        var page = await service.ReadAsync(fixture.Jordan, "chat-handover", null, null, 0, default);
        Assert.Equal(5, page.Messages.Count);
        Assert.All(page.Messages, message => Assert.True(message.CreatedAt >= now.AddMinutes(-20)));
        Assert.Equal(403, (await Assert.ThrowsAsync<AppError>(() => service.SendAsync(fixture.Jordan, "chat-handover",
            new(Guid.NewGuid().ToString(), page.Window, "not allowed", null), default))).Status);
    }
    [Fact]
    public async Task OperationStatusBelongsToItsActor()
    {
        var command = await Command();
        await service.SendAsync(Alex, "chat-handover", command, default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.StatusAsync(fixture.Sam, "chat-handover", command.OperationId, default))).Status);
    }
    [Fact]
    public async Task RevocationDeniesAlreadyOpenedView()
    {
        await service.ReadAsync(Alex, "chat-handover", null, null, 0, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.SetEntitlementAsync(new(fixture.Tenant, "chat-handover", Alex.Id, Alex.Name, now.AddDays(-7),
            now, now.AddMinutes(5), "revoked", false, false, 1), default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(Alex, "chat-handover", null, null, 0, default))).Status);
    }
    [Fact]
    public async Task EqualTimestampPaginationHasNoGapsOrDuplicates()
    {
        var at = fixture.Clock.GetUtcNow();
        for (var index = 0; index < 45; index++)
            await fixture.Store.ApplyAsync(fixture.Tenant, "chat-empty",
                new($"equal-{index:D2}", Alex.Id, Alex.Name, "Equal timestamp", at, at), default);
        List<string> ids = [];
        string? cursor = null;
        do
        {
            var page = await service.ReadAsync(Alex, "chat-empty", cursor, null, 0, default);
            ids.AddRange(page.Messages.Select(m => m.Id));
            cursor = page.Cursor;
        } while (cursor is not null);
        Assert.Equal(45, ids.Count);
        Assert.Equal(45, ids.Distinct().Count());
    }
    [Fact]
    public async Task HiddenThreadParentCannotBeReadOrUsedForAReply()
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.SetEntitlementAsync(new(fixture.Tenant, "channel-general", Alex.Id, Alex.Name, now.AddMinutes(-10),
            now, now.AddMinutes(5), "restricted-history", true, true, 1), default);
        Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(Alex, "channel-general", null, "root", 0, default))).Status);
        var command = await Command(parent: "root");
        Assert.Equal(400, (await Assert.ThrowsAsync<AppError>(() =>
            service.SendAsync(Alex, "channel-general", command, default))).Status);
    }
    [Fact]
    public async Task MissingCoverageIsNotFabricatedEmptyContent()
    {
        await fixture.Store.SetCoverageAsync(new(fixture.Tenant, "chat-empty", false, fixture.Clock.GetUtcNow(), null, "gap"), default);
        Assert.Equal("projection_preparing", (await Assert.ThrowsAsync<AppError>(() =>
            service.ReadAsync(Alex, "chat-empty", null, null, 0, default))).Code);
    }
    [Fact]
    public async Task CommandWindowsAreActorConversationPurposeSessionAndExpiryBound()
    {
        var command = await Command();
        Assert.Throws<AppError>(() => tokens.Validate(fixture.Sam, "chat-handover", "command:1", command.Window));
        Assert.Throws<AppError>(() => tokens.Validate(Alex, "chat-empty", "command:1", command.Window));
        Assert.Throws<AppError>(() => tokens.Validate(Alex with { SessionId = "different" }, "chat-handover", "command:1", command.Window));
        Assert.Throws<AppError>(() => tokens.Validate(Alex, "chat-handover", "page:1", command.Window));
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await fixture.RenewAsync();
        Assert.Equal(410, (await Assert.ThrowsAsync<AppError>(() => service.SendAsync(Alex, "chat-handover", command, default))).Status);
    }
    [Fact]
    public async Task ConcurrentIdenticalSubmissionsHaveOneGraphDispatch()
    {
        var command = await Command();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.SendAsync(Alex, "chat-handover", command, default)));
        Assert.All(results, result => Assert.Equal(OperationState.Confirmed, result.State));
        Assert.Equal(1, graph.Sends);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingClaimCannotBeRedispatchedByADuplicateOrExpiredOwner(bool expire)
    {
        var budget = new WaitingBudget();
        var messaging = new Messaging(fixture.Store, graph, budget, tokens, fixture.Clock);
        var command = await Command();
        var owner = messaging.SendAsync(Alex, "chat-handover", command, default);
        await budget.Entered.Task;
        Assert.Equal(OperationState.Pending, (await messaging.SendAsync(Alex, "chat-handover", command, default)).State);
        if (expire)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(36));
            Assert.Equal(OperationState.Rejected, (await messaging.StatusAsync(Alex, "chat-handover", command.OperationId, default)).State);
        }
        budget.Release.SetResult();
        Assert.Equal(expire ? OperationState.Rejected : OperationState.Confirmed, (await owner).State);
        Assert.Equal(expire ? 0 : 1, graph.Sends);
    }
    [Fact]
    public async Task OffboardedPrincipalCannotReadOrListDespiteExistingMembership()
    {
        await fixture.Store.SetBlockedAsync(fixture.Tenant, Alex.Id, true, default);
        Assert.Equal(401, (await Assert.ThrowsAsync<AppError>(() => service.ListAsync(Alex, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<AppError>(() => service.ReadAsync(Alex, "chat-handover", null, null, 0, default))).Status);
    }
    [Fact]
    public void AggregateProjectionNeverResurrectsDeletedContentAndRemainsBounded()
    {
        var now = fixture.Clock.GetUtcNow();
        var original = new Message("one", Alex.Id, Alex.Name, "Original", now, now);
        var value = new Conversation("projection", fixture.Tenant, "Projection", "chat", null, [], [original], []);
        var deleted = Projection.Apply(value, original with { Deleted = true, SourceVersion = "2" }, now);
        Assert.Same(deleted, Projection.Apply(deleted, original, now));
        Assert.Equal("", deleted.Messages.Single().Body);
        for (var i = 0; i < 200; i++)
            value = Projection.Apply(value, new(i.ToString(), Alex.Id, Alex.Name, new string('\u65e5', 4096), now.AddSeconds(i), now), now);
        Assert.True(Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(value)) < 1_100_000);
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
