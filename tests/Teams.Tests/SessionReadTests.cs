using Microsoft.AspNetCore.DataProtection;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class SessionReadTests
{
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private readonly Actor actor = new("tenant", "alex", "Alex");
    private readonly MemoryPartitionDocuments committed = new();

    private async Task<(LiveStateStore Writer, LiveStateStore Reader, LaggingDocuments Documents, Messaging Messaging)> Prepare()
    {
        var writer = new LiveStateStore(committed, TimeProvider.System);
        await writer.CreateAsync(new("chat", "tenant", "Approved", "channel", null, [], [], [],
            Epoch: 1, CoverageUntil: now.AddMinutes(4)), default);
        await writer.SetEntitlementAsync(new("tenant", "chat", actor.Id, actor.Name, now.AddDays(-1),
            now, now.AddMinutes(3), "authority-1", true, true, 1), default);
        var documents = new LaggingDocuments(committed);
        var reader = new LiveStateStore(documents, TimeProvider.System);
        var messaging = new Messaging(reader, new NoGraph(), new MemoryBudget(TimeProvider.System),
            new CommandTokens(new EphemeralDataProtectionProvider(), TimeProvider.System), TimeProvider.System);
        return (writer, reader, documents, messaging);
    }

    [Fact]
    public async Task IndependentReadersUseSessionForContentAndCurrentReadsForSecurityAndWriteEvidence()
    {
        var (writer, reader, documents, _) = await Prepare();
        await writer.ApplyAsync("tenant", "chat", new("one", actor.Id, actor.Name, "Old", now, now), default);
        var old = Assert.Single((await committed.QueryAsync(new("tenant", "message"), 10, null, default)).Items);
        documents.Stale[old.Value.Id] = old;
        await writer.ApplyAsync("tenant", "chat", new("one", actor.Id, actor.Name, "New", now, now.AddSeconds(1),
            SourceVersion: "2"), default);
        Assert.Equal("Old", (await reader.MessageAsync("tenant", "chat", "one", null, default))!.Body);
        Assert.Equal("New", (await reader.CurrentMessageAsync("tenant", "chat", "one", null, default))!.Body);
        await reader.GetMemberAsync(actor, "chat", false, default);
        Assert.Contains(old.Value.Id, documents.SessionReads);
        Assert.Contains(old.Value.Id, documents.CurrentReads);
        Assert.Contains("conversation", documents.CurrentReads);
        Assert.Contains("revocation", documents.CurrentReads);
        Assert.Contains(documents.CurrentReads, id => id.StartsWith("member:", StringComparison.Ordinal));
        documents.Stale.Clear();
        Assert.Equal("New", (await reader.MessageAsync("tenant", "chat", "one", null, default))!.Body);
    }

    [Fact]
    public async Task FutureCursorIsLagWithBoundedAccessRatherThanFalseJournalReset()
    {
        var (_, reader, _, _) = await Prepare();
        var error = await Assert.ThrowsAsync<AppError>(() => reader.ChangesAsync(actor, "chat", 42, default));
        Assert.Equal("projection_behind", error.Code);
        Assert.Equal(503, error.Status);
        Assert.NotNull(error.Access);
        Assert.Equal(now.AddDays(-1), error.Access.HistoryFrom);
        Assert.Equal(now.AddMinutes(3), error.Access.ValidUntil);
        Assert.Equal(1, error.Access.Epoch);
    }

    [Fact]
    public async Task TemporarilyMissingContentDoesNotFabricateADeletionOrReset()
    {
        var (writer, reader, documents, _) = await Prepare();
        await writer.ApplyAsync("tenant", "chat", new("one", actor.Id, actor.Name, "New", now, now), default);
        var record = Assert.Single((await committed.QueryAsync(new("tenant", "message"), 10, null, default)).Items);
        documents.Stale[record.Value.Id] = null;
        var error = await Assert.ThrowsAsync<AppError>(() => reader.ChangesAsync(actor, "chat", 0, default));
        Assert.Equal("projection_behind", error.Code);
        documents.Stale.Clear();
        var page = await reader.ChangesAsync(actor, "chat", 0, default);
        Assert.False(page.ResetRequired);
        Assert.Equal(1, page.Revision);
        Assert.Equal("New", Assert.Single(page.Messages!).Body);
        Assert.DoesNotContain(record.Value.Id, documents.CurrentReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task OldMessageVersionCannotBeLabeledWithTheNewJournalRevision(long after)
    {
        var (writer, reader, documents, _) = await Prepare();
        await writer.ApplyAsync("tenant", "chat", new("one", actor.Id, actor.Name, "Old", now, now), default);
        var old = Assert.Single((await committed.QueryAsync(new("tenant", "message"), 10, null, default)).Items);
        documents.Stale[old.Value.Id] = old;
        await writer.ApplyAsync("tenant", "chat", new("one", actor.Id, actor.Name, "New", now, now.AddSeconds(1),
            SourceVersion: "2"), default);
        Assert.Equal("projection_behind", (await Assert.ThrowsAsync<AppError>(() =>
            reader.ChangesAsync(actor, "chat", after, default))).Code);
        documents.Stale.Clear();
        var page = await reader.ChangesAsync(actor, "chat", after, default);
        Assert.Equal(2, page.Revision);
        Assert.Equal("New", Assert.Single(page.Messages!).Body);
    }

    [Fact]
    public async Task RevocationPrecedesLagButExpiredSourceCoverageDoesNotInvalidateAuthority()
    {
        var (writer, reader, _, messaging) = await Prepare();
        await writer.SetBlockedAsync("tenant", actor.Id, true, default);
        var denied = await Assert.ThrowsAsync<AppError>(() => messaging.ReadAsync(actor, "chat", null, null, 42, default));
        Assert.Equal(401, denied.Status);
        Assert.Null(denied.Access);
        await writer.SetBlockedAsync("tenant", actor.Id, false, default);
        await writer.SetCoverageAsync(new("tenant", "chat", false, null, null, "gap"), default);
        var uncovered = await Assert.ThrowsAsync<AppError>(() => reader.ChangesAsync(actor, "chat", 42, default));
        Assert.Equal("projection_behind", uncovered.Code);
        Assert.NotNull(uncovered.Access);
        Assert.Equal(now.AddMinutes(3), uncovered.Access.ValidUntil);
    }

    [Fact]
    public async Task LagResponseCannotKeepAnInaccessibleParentOpen()
    {
        var (writer, _, _, messaging) = await Prepare();
        await writer.ApplyAsync("tenant", "chat", new("parent", actor.Id, actor.Name, "", now, now, Deleted: true), default);
        var error = await Assert.ThrowsAsync<AppError>(() =>
            messaging.ChangesAsync(actor, "chat", 0, default, "parent", 42));
        Assert.Equal(404, error.Status);
        Assert.Null(error.Access);
    }

    [Fact]
    public async Task MissingCriticalReadCapabilityDoesNotFallBackToSession()
    {
        var (_, reader, documents, _) = await Prepare();
        documents.CurrentUnavailable = true;
        var error = await Assert.ThrowsAsync<AppError>(() => reader.GetMemberAsync(actor, "chat", false, default));
        Assert.Equal("primary_read_unavailable", error.Code);
        Assert.Empty(documents.SessionReads);
    }

    [Fact]
    public async Task LagCannotHideAnExistingOperationOrWeakenEpochFencing()
    {
        var (writer, reader, documents, _) = await Prepare();
        var operation = new SendOperation(Guid.NewGuid().ToString(), "tenant", "chat", actor.Id, "fingerprint",
            "", null, OperationState.Unknown, now, Epoch: 1);
        Assert.True(await writer.CreateOperationAsync(operation, default));
        var record = Assert.Single((await committed.QueryAsync(new("tenant", "operation"), 10, null, default)).Items);
        documents.Stale[record.Value.Id] = null;
        var observed = (await reader.GetOperationAsync("tenant", "chat", operation.Id, default))!;
        Assert.Equal(OperationState.Unknown, observed.Value.State);
        var metadata = (await writer.GetAsync("tenant", "chat", default))!;
        Assert.True(await writer.SaveAsync(metadata with { Value = metadata.Value with { Epoch = 2 } }, default));
        Assert.Equal("writer_fenced", (await Assert.ThrowsAsync<AppError>(() =>
            reader.SaveOperationAsync(observed with { Value = observed.Value with { State = OperationState.Dispatching } }, default))).Code);
        Assert.Empty(documents.SessionReads);
    }

    private sealed class LaggingDocuments(IPartitionDocuments committed) : IPartitionDocuments
    {
        public Dictionary<string, Versioned<PartitionDocument>?> Stale { get; } = [];
        public List<string> SessionReads { get; } = [];
        public List<string> CurrentReads { get; } = [];
        public bool CurrentUnavailable { get; set; }
        public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SessionReads.Add(id);
            return Stale.TryGetValue(id, out var stale) ? Task.FromResult(stale) : committed.ReadAsync(pk, id, ct);
        }
        public Task<Versioned<PartitionDocument>?> ReadCurrentAsync(string pk, string id, CancellationToken ct)
        {
            CurrentReads.Add(id);
            if (CurrentUnavailable) throw new AppError(503, "primary_read_unavailable", "Critical read unavailable.");
            return committed.ReadCurrentAsync(pk, id, ct);
        }
        public Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct) =>
            committed.QueryAsync(filter, limit, cursor, ct);
        public Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct) => committed.WriteAsync(writes, ct);
    }

    private sealed class NoGraph : IGraphTransport
    {
        public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Read validation must not send a Teams message.");
        public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Read validation must not resolve through Graph.");
    }
}
