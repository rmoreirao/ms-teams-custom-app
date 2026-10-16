using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class LiveStateStore(IPartitionDocuments documents, TimeProvider clock) : ILiveStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => clock.GetUtcNow();
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Partition(string tenant, string conversation) => $"{tenant}|c|{Key(conversation)}";
    private static string PrincipalPartition(string tenant, string actor) => $"{tenant}|p|{Key(actor)}";
    private static string MessageId(string id, string? parent) => $"message:{Key($"{parent}:{id}")}";
    private static T Decode<T>(PartitionDocument value) => JsonSerializer.Deserialize<T>(value.Json, Json)
        ?? throw new InvalidOperationException("Persisted live document is invalid.");
    private PartitionDocument Wrap<T>(string id, string pk, string kind, string tenant, string conversation, T value,
        int ttl = -1, string actor = "", DateTimeOffset? at = null, long revision = 0, bool pending = false, string parent = "", string sourceId = "") =>
        new(id, pk, kind, tenant, conversation, actor, at ?? Now, revision, ttl, JsonSerializer.Serialize(value, Json), pending, parent,
            (at ?? Now).ToUniversalTime().ToString("O"), sourceId);
    public async Task<Versioned<Conversation>?> GetAsync(string tenant, string id, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(tenant, id), "conversation", ct);
        return value is null ? null : new(Decode<Conversation>(value.Value), value.Version);
    }
    public Task<bool> CreateAsync(Conversation value, CancellationToken ct)
    {
        if (value.Messages.Count != 0 || value.Members.Count != 0) throw new InvalidOperationException("Live onboarding must not import fixture aggregates.");
        return documents.WriteAsync([new(Wrap("conversation", Partition(value.TenantId, value.Id), "conversation",
            value.TenantId, value.Id, value), null)], ct);
    }
    public async Task<bool> SaveAsync(Versioned<Conversation> value, CancellationToken ct)
    {
        var metadata = value.Value with { Members = [], Messages = [], Changes = [], Tombstones = [] };
        return await documents.WriteAsync([new(Wrap("conversation", Partition(metadata.TenantId, metadata.Id), "conversation",
            metadata.TenantId, metadata.Id, metadata), value.Version)], ct);
    }
    public Task<IReadOnlyList<Conversation>> ListAsync(string tenant, CancellationToken ct) =>
        throw new InvalidOperationException("Live paths must query the paged principal catalog, not scan the tenant.");
    public async Task<CatalogPage> CatalogAsync(Actor actor, string? cursor, CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(actor.TenantId, "catalog", PrincipalPartition(actor.TenantId, actor.Id), actor.Id), 50, cursor, ct);
        List<ConversationView> result = [];
        foreach (var record in page.Items)
        {
            var grant = Decode<Entitlement>(record.Value);
            if (!grant.Active || grant.FreshUntil <= Now || grant.VerifiedAt > Now || grant.FreshUntil - grant.VerifiedAt > TimeSpan.FromMinutes(5)) continue;
            await GetMemberAsync(actor, grant.ConversationId, false, ct);
            var conversation = await GetAsync(actor.TenantId, grant.ConversationId, ct);
            if (conversation is not null)
                result.Add(new(conversation.Value.Id, conversation.Value.Title, conversation.Value.Kind, conversation.Value.Team, conversation.Value.Revision));
        }
        return new(result, page.Cursor);
    }
    private async Task<Entitlement> GrantAsync(Actor actor, string conversation, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(actor.TenantId, conversation), $"member:{Key(actor.Id)}", ct);
        var grant = value is null ? null : Decode<Entitlement>(value.Value);
        if (grant is null || !grant.Active) throw new AppError(404, "access_denied", "Conversation is not available.");
        if (grant.TenantId != actor.TenantId || grant.ActorId != actor.Id || grant.ConversationId != conversation ||
            grant.VerifiedAt > Now || grant.FreshUntil <= Now || grant.FreshUntil - grant.VerifiedAt > TimeSpan.FromMinutes(5) ||
            string.IsNullOrWhiteSpace(grant.AuthorityVersion))
            throw new AppError(503, "authorization_stale", "Current policy authority is required before serving this content.");
        return grant;
    }
    public async Task<Member> GetMemberAsync(Actor actor, string conversation, bool sending, CancellationToken ct)
    {
        var grant = await GrantAsync(actor, conversation, ct);
        if (sending && !grant.CanSend) throw new AppError(403, "send_denied", "Current policy does not permit sending.");
        var metadata = await GetAsync(actor.TenantId, conversation, ct);
        if (metadata is null || grant.Epoch != metadata.Value.Epoch)
            throw new AppError(503, "authorization_stale", "Policy and conversation epochs do not match.");
        return new(grant.ActorId, grant.Name, grant.HistoryFrom);
    }
    public async Task SetEntitlementAsync(Entitlement grant, CancellationToken ct)
    {
        if (grant.FreshUntil <= grant.VerifiedAt || grant.FreshUntil - grant.VerifiedAt > TimeSpan.FromMinutes(5) ||
            grant.VerifiedAt > Now || string.IsNullOrWhiteSpace(grant.AuthorityVersion))
            throw new InvalidOperationException("Invalid authority lease; cache access cannot renew source evidence.");
        var pk = Partition(grant.TenantId, grant.ConversationId);
        var memberId = $"member:{Key(grant.ActorId)}";
        var current = await documents.ReadAsync(pk, memberId, ct);
        if (current is not null)
        {
            var previous = Decode<Entitlement>(current.Value);
            if (grant.VerifiedAt < previous.VerifiedAt || grant.Epoch < previous.Epoch)
                throw new InvalidOperationException("Policy authority cannot move backwards.");
        }
        if (!await documents.WriteAsync([new(Wrap(memberId, pk, "member", grant.TenantId, grant.ConversationId, grant,
            actor: grant.ActorId), current?.Version)], ct)) throw new AppError(409, "authority_conflict", "Policy changed concurrently.");
        var catalogPk = PrincipalPartition(grant.TenantId, grant.ActorId);
        var id = $"catalog:{Key(grant.ConversationId)}";
        var catalog = await documents.ReadAsync(catalogPk, id, ct);
        if (!await documents.WriteAsync([new(Wrap(id, catalogPk, "catalog", grant.TenantId, grant.ConversationId, grant,
            actor: grant.ActorId), catalog?.Version)], ct))
            throw new AppError(409, "catalog_conflict", "Catalog update must be retried from authority.");
    }
    public async Task<IReadOnlyList<Message>> MessagesAsync(Actor actor, string conversation, string? parent,
        DateTimeOffset historyFrom, DateTimeOffset? before, string? beforeId, CancellationToken ct)
    {
        var grant = await GetMemberAsync(actor, conversation, false, ct);
        var cutoff = new[] { historyFrom, grant.HistoryFrom, Now.AddDays(-7) }.Max();
        List<Message> messages = [];
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await documents.QueryAsync(new(actor.TenantId, "message", Partition(actor.TenantId, conversation),
                ConversationId: conversation, Since: cutoff, Parent: parent ?? "", Before: before, BeforeId: beforeId),
                21 - messages.Count, cursor, ct);
            messages.AddRange(result.Items.Select(d => Decode<Message>(d.Value)));
            cursor = result.Cursor;
            if (cursor is null || messages.Count == 21) return messages;
        }
        throw new AppError(503, "projection_page_unavailable", "The bounded database read could not finish. Retry this page.");
    }
    public async Task<Message?> MessageAsync(string tenant, string conversation, string id, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(tenant, conversation), MessageId(id, parent), ct);
        return value is null ? null : Decode<Message>(value.Value);
    }
    public async Task<Versioned<SendOperation>?> GetOperationAsync(string tenant, string conversation, string id, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(tenant, conversation), $"op:{Key(id)}", ct);
        return value is null ? null : new(Decode<SendOperation>(value.Value), value.Version);
    }
    private PartitionDocument Operation(SendOperation operation) => Wrap($"op:{Key(operation.Id)}",
        Partition(operation.TenantId, operation.ConversationId), "operation", operation.TenantId, operation.ConversationId,
        operation with { Body = "" }, 14 * 86400, operation.ActorId);
    public Task<bool> CreateOperationAsync(SendOperation value, CancellationToken ct) =>
        documents.WriteAsync([new(Operation(value), null)], ct);
    public Task<bool> SaveOperationAsync(Versioned<SendOperation> value, CancellationToken ct) =>
        documents.WriteAsync([new(Operation(value.Value), value.Version)], ct);
    public async Task<bool> CommitAsync(Versioned<Conversation> conversation, Versioned<SendOperation>? operation, CancellationToken ct)
    {
        var value = conversation.Value;
        var pk = Partition(value.TenantId, value.Id);
        var current = await GetAsync(value.TenantId, value.Id, ct);
        if (current is null || current.Version != conversation.Version) return false;
        List<DocumentWrite> writes = [];
        if (operation is not null)
        {
            if (operation.Value.TenantId != value.TenantId || operation.Value.ConversationId != value.Id)
                throw new InvalidOperationException("Confirmation cannot cross a partition.");
            writes.Add(new(Operation(operation.Value), operation.Version));
        }
        var incoming = value.Messages.SingleOrDefault();
        var revision = current.Value.Revision;
        var ready = current.Value.CoverageReady;
        if (incoming is not null && incoming.CreatedAt >= Now.AddDays(-7))
        {
            var id = MessageId(incoming.Id, incoming.ParentId);
            var previous = await documents.ReadAsync(pk, id, ct);
            var old = previous is null ? null : Decode<Message>(previous.Value);
            var tombstone = await documents.ReadAsync(pk, $"tombstone:{Key($"{incoming.ParentId}:{incoming.Id}")}", ct);
            var same = old is not null && (old.SourceVersion == incoming.SourceVersion || old == incoming);
            var older = old is not null && incoming.ModifiedAt < old.ModifiedAt;
            if (!same && !older && (incoming.Deleted || tombstone is null))
            {
                if (old is not null && old.ModifiedAt == incoming.ModifiedAt && old.Deleted != incoming.Deleted && !incoming.Deleted)
                    ready = false;
                else if (old is not null && old.ModifiedAt == incoming.ModifiedAt && !incoming.Deleted &&
                    (old.Body != incoming.Body || old.ParentId != incoming.ParentId))
                    ready = false;
                else
                {
                    revision++;
                    var clean = incoming.Deleted ? incoming with { Body = "" } : incoming;
                    var ttl = Math.Max(1, (int)(incoming.CreatedAt.AddDays(7) - Now).TotalSeconds);
                    writes.Add(new(Wrap(id, pk, "message", value.TenantId, value.Id, clean, ttl,
                        at: incoming.CreatedAt, revision: revision, parent: incoming.ParentId ?? "", sourceId: incoming.Id), previous?.Version));
                    if (incoming.Deleted && tombstone is null)
                        writes.Add(new(Wrap($"tombstone:{Key($"{incoming.ParentId}:{incoming.Id}")}", pk, "tombstone",
                            value.TenantId, value.Id, incoming.Id, 14 * 86400), null));
                    var change = new OutboxChange(Guid.NewGuid().ToString("N"), value.TenantId, value.Id, revision, Now);
                    writes.Add(new(Wrap($"outbox:{change.Id}", pk, "outbox", value.TenantId, value.Id, change,
                        14 * 86400, revision: revision, pending: true), null));
                }
            }
        }
        var metadata = current.Value with { Revision = revision, CoverageReady = ready, Messages = [], Members = [], Changes = [], Tombstones = [] };
        writes.Add(new(Wrap("conversation", pk, "conversation", value.TenantId, value.Id, metadata), current.Version));
        return await documents.WriteAsync(writes, ct);
    }
    public async Task<bool> ApplyAsync(string tenant, string conversation, Message message, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await GetAsync(tenant, conversation, ct) ?? throw new AppError(404, "resource_unknown", "Resource is not onboarded.");
            if (await CommitAsync(current with { Value = current.Value with { Messages = [message] } }, null, ct)) return true;
        }
        throw new AppError(503, "projection_conflict", "Projection concurrency limit exceeded.");
    }
    public async Task SetCoverageAsync(Coverage coverage, CancellationToken ct)
    {
        var current = await GetAsync(coverage.TenantId, coverage.ConversationId, ct) ?? throw new AppError(404, "resource_unknown", "Resource is not onboarded.");
        var pk = Partition(coverage.TenantId, coverage.ConversationId);
        var checkpoint = await documents.ReadAsync(pk, "coverage", ct);
        if (!await documents.WriteAsync([
            new(Wrap("coverage", pk, "coverage", coverage.TenantId, coverage.ConversationId, coverage), checkpoint?.Version),
            new(Wrap("conversation", pk, "conversation", coverage.TenantId, coverage.ConversationId,
                current.Value with { CoverageReady = coverage.Ready }), current.Version)], ct))
            throw new AppError(409, "coverage_conflict", "Coverage changed concurrently.");
    }
    public async Task<EntitlementPage> RecipientsAsync(string tenant, string conversation, string? cursor, CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(tenant, "member", Partition(tenant, conversation)), 100, cursor, ct);
        return new(page.Items.Select(d => Decode<Entitlement>(d.Value)).Where(g => g.Active && g.FreshUntil > Now &&
            g.VerifiedAt <= Now && g.FreshUntil - g.VerifiedAt <= TimeSpan.FromMinutes(5)).ToArray(), page.Cursor);
    }
    public async Task<IReadOnlyList<OutboxChange>> PendingOutboxAsync(string tenant, CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(tenant, "outbox", Pending: true), 100, null, ct);
        return page.Items.Select(d => Decode<OutboxChange>(d.Value)).ToArray();
    }
    public async Task CompleteOutboxAsync(OutboxChange change, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(change.TenantId, change.ConversationId), $"outbox:{change.Id}", ct);
        if (value is not null && value.Value.Pending &&
            !await documents.WriteAsync([new(value.Value with { Pending = false }, value.Version)], ct))
            throw new AppError(409, "outbox_conflict", "Outbox publication checkpoint changed concurrently.");
    }
    public async Task<bool> IsBlockedAsync(string tenant, string actor, CancellationToken ct)
    {
        var value = await documents.ReadAsync(PrincipalPartition(tenant, actor), "revocation", ct);
        return value is not null && Decode<bool>(value.Value);
    }
    public async Task SetBlockedAsync(string tenant, string actor, bool blocked, CancellationToken ct)
    {
        var pk = PrincipalPartition(tenant, actor);
        var current = await documents.ReadAsync(pk, "revocation", ct);
        if (!await documents.WriteAsync([new(Wrap("revocation", pk, "revocation", tenant, "", blocked), current?.Version)], ct))
            throw new AppError(409, "revocation_conflict", "Revocation changed concurrently.");
    }
    public Task<WriteResult?> GetSimulatorReceiptAsync(string tenant, string conversation, string operation, CancellationToken ct) =>
        throw new InvalidOperationException("Simulator receipts are forbidden in live persistence.");
    public Task SaveSimulatorReceiptAsync(string tenant, string conversation, string operation, WriteResult result, CancellationToken ct) =>
        throw new InvalidOperationException("Simulator receipts are forbidden in live persistence.");
}
