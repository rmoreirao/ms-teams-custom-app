using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class LiveStateStore(IPartitionDocuments documents, TimeProvider clock, IPartitionDocuments? authority = null,
    IRecentMessageCache? cache = null, IReadOnlySet<string>? approvedResources = null,
    IReadOnlyDictionary<string, ApprovedResource>? resourceRegistry = null) : ILiveStateStore
{
    private IPartitionDocuments Authority => authority ?? documents;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => clock.GetUtcNow();
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Partition(string tenant, string conversation) => $"{tenant}|c|{Key(conversation)}";
    private static string PrincipalPartition(string tenant, string actor) => $"{tenant}|p|{Key(actor)}";
    private static string MessageKey(string id, string? parent) => Key(JsonSerializer.Serialize(new[] { parent, id }));
    private static string MessageId(string id, string? parent) => $"message:{MessageKey(id, parent)}";
    private static string ReadViewId(string? parent) => $"read-view:{Key(JsonSerializer.Serialize(parent))}";
    private static T Decode<T>(PartitionDocument value)
    {
        if (value.SchemaVersion != 1) throw new InvalidOperationException("Unsupported live document schema; migration is required.");
        return JsonSerializer.Deserialize<T>(value.Json, Json)
            ?? throw new InvalidOperationException("Persisted live document is invalid.");
    }
    private PartitionDocument Wrap<T>(string id, string pk, string kind, string tenant, string conversation, T value,
        int ttl = -1, string actor = "", DateTimeOffset? at = null, long revision = 0, bool pending = false, string parent = "", string sourceId = "") =>
        new(id, pk, kind, tenant, conversation, actor, at ?? Now, revision, ttl, JsonSerializer.Serialize(value, Json), pending, parent,
            (at ?? Now).ToUniversalTime().ToString("O"), sourceId);
    public async Task<Versioned<Conversation>?> GetAsync(string tenant, string id, CancellationToken ct)
    {
        if (approvedResources is not null && !approvedResources.Contains(id)) return null;
        var value = await documents.ReadCurrentAsync(Partition(tenant, id), "conversation", ct);
        if (value is null) return null;
        var conversation = Decode<Conversation>(value.Value);
        if (resourceRegistry is not null &&
            (!resourceRegistry.TryGetValue(id, out var approved) || !approved.Matches(conversation)))
            throw new AppError(503, "resource_mapping_changed", "Resource mapping changed; explicit migration or a new logical ID is required.");
        return new(conversation with { CoverageReady = conversation.CoverageReady && conversation.CoverageUntil > Now }, value.Version);
    }
    public Task<bool> CreateAsync(Conversation value, CancellationToken ct)
    {
        if (value.Messages.Count != 0 || value.Members.Count != 0 || value.Changes.Count != 0 ||
            (approvedResources is not null && !approvedResources.Contains(value.Id)) ||
            (resourceRegistry is not null && (!resourceRegistry.TryGetValue(value.Id, out var approved) || !approved.Matches(value))))
            throw new InvalidOperationException("Live onboarding must use approved empty resource metadata, not fixture aggregates.");
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
        if (await IsBlockedAsync(actor.TenantId, actor.Id, ct))
            throw new AppError(401, "authentication_required", "This account no longer has access.");
        var page = await Authority.QueryAsync(new(actor.TenantId, "catalog", PrincipalPartition(actor.TenantId, actor.Id), actor.Id), 50, cursor, ct);
        List<ConversationView> result = [];
        foreach (var record in page.Items)
        {
            var grant = Decode<Entitlement>(record.Value);
            if (approvedResources is not null && !approvedResources.Contains(grant.ConversationId)) continue;
            try { await GetMemberAsync(actor, grant.ConversationId, false, ct); }
            catch (AppError ex) when (ex.Status == 404) { continue; }
            var conversation = await GetAsync(actor.TenantId, grant.ConversationId, ct);
            if (conversation is not null)
                result.Add(new(conversation.Value.Id, conversation.Value.Title, conversation.Value.Kind, conversation.Value.Team, conversation.Value.Revision));
        }
        return new(result, page.Cursor);
    }
    private async Task<Entitlement> GrantAsync(Actor actor, string conversation, CancellationToken ct)
    {
        var value = await Authority.ReadCurrentAsync(Partition(actor.TenantId, conversation), $"member:{Key(actor.Id)}", ct);
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
        if (await IsBlockedAsync(actor.TenantId, actor.Id, ct))
            throw new AppError(401, "authentication_required", "This account no longer has access.");
        var grant = await GrantAsync(actor, conversation, ct);
        if (sending && !grant.CanSend) throw new AppError(403, "send_denied", "Current policy does not permit sending.");
        var metadata = await GetAsync(actor.TenantId, conversation, ct);
        if (metadata is null || grant.Epoch != metadata.Value.Epoch)
            throw new AppError(503, "authorization_stale", "Policy and conversation epochs do not match.");
        return new(grant.ActorId, grant.Name, grant.HistoryFrom, FreshUntil: grant.FreshUntil);
    }
    public async Task SetEntitlementAsync(Entitlement grant, CancellationToken ct)
    {
        if (grant.FreshUntil <= grant.VerifiedAt || grant.FreshUntil - grant.VerifiedAt > TimeSpan.FromMinutes(5) ||
            grant.VerifiedAt > Now || string.IsNullOrWhiteSpace(grant.AuthorityVersion))
            throw new InvalidOperationException("Invalid authority lease; cache access cannot renew source evidence.");
        var pk = Partition(grant.TenantId, grant.ConversationId);
        var memberId = $"member:{Key(grant.ActorId)}";
        var current = await Authority.ReadCurrentAsync(pk, memberId, ct);
        var same = false;
        if (current is not null)
        {
            var previous = Decode<Entitlement>(current.Value);
            if (grant.VerifiedAt < previous.VerifiedAt || grant.Epoch < previous.Epoch)
                throw new InvalidOperationException("Policy authority cannot move backwards.");
            if (grant.VerifiedAt == previous.VerifiedAt && grant.AuthorityVersion != previous.AuthorityVersion)
                throw new InvalidOperationException("Conflicting policy versions require fresh authoritative evidence.");
            if (grant.VerifiedAt == previous.VerifiedAt && grant != previous)
                throw new InvalidOperationException("The same source evidence cannot renew or change a policy lease.");
            same = grant == previous;
        }
        if (!same && !await Authority.WriteAsync([new(Wrap(memberId, pk, "member", grant.TenantId, grant.ConversationId, grant,
            actor: grant.ActorId), current?.Version)], ct)) throw new AppError(409, "authority_conflict", "Policy changed concurrently.");
        var catalogPk = PrincipalPartition(grant.TenantId, grant.ActorId);
        var id = $"catalog:{Key(grant.ConversationId)}";
        var catalog = await Authority.ReadCurrentAsync(catalogPk, id, ct);
        if (catalog is not null && Decode<Entitlement>(catalog.Value) == grant) return;
        if (!await Authority.WriteAsync([new(Wrap(id, catalogPk, "catalog", grant.TenantId, grant.ConversationId, grant,
            actor: grant.ActorId), catalog?.Version)], ct))
            throw new AppError(409, "catalog_conflict", "Catalog update must be retried from authority.");
    }
    public async Task SetDirectChatAuthorityAsync(DirectChatAuthority evidence, CancellationToken ct)
    {
        if (evidence.ChatType != "oneOnOne" || evidence.MemberIds.Count != 2 || evidence.MemberIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
            evidence.MemberIds.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(evidence.GraphChatId) ||
            string.IsNullOrWhiteSpace(evidence.AuthorityVersion) || evidence.VerifiedAt > Now ||
            evidence.FreshUntil <= evidence.VerifiedAt || evidence.FreshUntil - evidence.VerifiedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("One-to-one discovery requires bounded authoritative exact-pair evidence.");
        evidence = evidence with { MemberIds = evidence.MemberIds.Order(StringComparer.Ordinal).ToArray() };
        var pk = Partition(evidence.TenantId, evidence.ResourceId);
        var previous = await Authority.ReadCurrentAsync(pk, "direct-chat-authority", ct);
        if (previous is not null)
        {
            var old = Decode<DirectChatAuthority>(previous.Value);
            if (evidence.VerifiedAt < old.VerifiedAt || evidence.GraphChatId != old.GraphChatId ||
                evidence.VerifiedAt == old.VerifiedAt && (evidence.AuthorityVersion != old.AuthorityVersion ||
                    evidence.FreshUntil != old.FreshUntil || !evidence.MemberIds.SequenceEqual(old.MemberIds)))
                throw new InvalidOperationException("Direct-chat authority cannot be retargeted, renewed or changed using stale evidence.");
            if (evidence.VerifiedAt == old.VerifiedAt) return;
        }
        if (!await Authority.WriteAsync([new(Wrap("direct-chat-authority", pk, "direct-chat-authority",
            evidence.TenantId, evidence.ResourceId, evidence), previous?.Version)], ct))
            throw new AppError(409, "authority_conflict", "The one-to-one policy evidence changed concurrently.");
    }
    public async Task<DirectChatAuthority?> DirectChatAuthorityAsync(string tenant, string resource, CancellationToken ct)
    {
        var value = await Authority.ReadCurrentAsync(Partition(tenant, resource), "direct-chat-authority", ct);
        return value is null ? null : Decode<DirectChatAuthority>(value.Value);
    }
    public async Task<IReadOnlyList<Message>> MessagesAsync(Actor actor, string conversation, string? parent,
        DateTimeOffset historyFrom, DateTimeOffset? before, string? beforeId, CancellationToken ct)
    {
        var grant = await GetMemberAsync(actor, conversation, false, ct);
        var cutoff = new[] { historyFrom, grant.HistoryFrom, Now.AddDays(-7) }.Max();
        var metadata = await GetAsync(actor.TenantId, conversation, ct);
        if (metadata is null)
            throw new AppError(404, "access_denied", "Conversation is not available.");
        var key = $"recent:v1:{Key($"{actor.TenantId}:{conversation}:{metadata.Value.Epoch}:{metadata.Value.Revision}:{parent}:{before:O}:{beforeId}")}";
        var cached = cache is null ? null : await cache.ReadAsync(key, ct);
        if (cached is not null) return cached.Where(m => m.CreatedAt >= cutoff).ToArray();
        List<Message> messages = [];
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await documents.QueryAsync(new(actor.TenantId, "message", Partition(actor.TenantId, conversation),
                ConversationId: conversation, Since: Now.AddDays(-7), Parent: parent ?? "", Before: before, BeforeId: beforeId),
                21 - messages.Count, cursor, ct);
            messages.AddRange(result.Items.Select(d => Decode<Message>(d.Value)));
            cursor = result.Cursor;
            if (cursor is null || messages.Count == 21)
            {
                if (cache is not null) await cache.WriteAsync(key, messages, ct);
                return messages.Where(m => m.CreatedAt >= cutoff).ToArray();
            }
        }
        throw new AppError(503, "projection_page_unavailable", "The bounded database read could not finish. Retry this page.");
    }
    public async Task<Message?> MessageAsync(string tenant, string conversation, string id, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(tenant, conversation), MessageId(id, parent), ct);
        return value is null ? null : Decode<Message>(value.Value);
    }
    public async Task<Message?> CurrentMessageAsync(string tenant, string conversation, string id, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Partition(tenant, conversation), MessageId(id, parent), ct);
        return value is null ? null : Decode<Message>(value.Value);
    }
    public async Task<ProjectionScan> ScanAsync(string tenant, string conversation, string? parent, string? cursor, CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(tenant, "message", Partition(tenant, conversation),
            ConversationId: conversation, Since: Now.AddDays(-7), Parent: parent), 1, cursor, ct);
        return new(page.Items.Select(v => Decode<Message>(v.Value)).ToArray(), page.Cursor);
    }
    public async Task<ChangePage> ChangesAsync(Actor actor, string conversation, long after, CancellationToken ct)
    {
        if (after < 0) throw new AppError(400, "invalid_revision", "Revision cannot be negative.");
        var member = await GetMemberAsync(actor, conversation, false, ct);
        var metadata = (await GetAsync(actor.TenantId, conversation, ct))!.Value;
        var cutoff = member.HistoryFrom > Now.AddDays(-7) ? member.HistoryFrom : Now.AddDays(-7);
        var access = ReadAccess.Create(member, metadata, Now);
        if (after > metadata.Revision)
            throw new AppError(503, "projection_behind", "The reader has not reached the requested revision. Synchronizing recent context.", access);
        var page = await documents.QueryAsync(new(actor.TenantId, "change", Partition(actor.TenantId, conversation),
            AfterRevision: after), 100, null, ct);
        var records = page.Items.Where(v => v.Value.Revision <= metadata.Revision).OrderBy(v => v.Value.Revision).ToArray();
        var reset = after < metadata.Revision && (records.Length == 0 || records[0].Value.Revision != after + 1 ||
            page.Cursor is not null || records[^1].Value.Revision < metadata.Revision ||
            records.Where((record, index) => record.Value.Revision != after + index + 1).Any());
        var changes = records.Where(r => r.Value.At >= cutoff)
            .Select(r => Decode<Change>(r.Value)).ToArray();
        List<Message> messages = [];
        var bytes = 0;
        if (!reset)
            foreach (var change in changes.OrderByDescending(c => c.Revision).DistinctBy(c => (c.MessageId, c.ParentId)))
            {
                var document = await documents.ReadAsync(Partition(actor.TenantId, conversation),
                    MessageId(change.MessageId, change.ParentId), ct);
                if (document is null || document.Value.Revision < change.Revision)
                    throw new AppError(503, "projection_behind", "The changed content is not yet visible. Synchronizing recent context.", access);
                var message = Decode<Message>(document.Value);
                if (message.CreatedAt >= cutoff)
                {
                    if (message.ParentId is not null)
                    {
                        var parent = await CurrentMessageAsync(actor.TenantId, conversation, message.ParentId, null, ct);
                        if (parent is null || parent.Deleted || parent.CreatedAt < cutoff) continue;
                    }
                    bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(message, Json));
                    if (bytes > 262144) { reset = true; break; }
                    messages.Add(message);
                }
            }
        return new(reset ? [] : changes.Where(change => messages.Any(message =>
            message.Id == change.MessageId && message.ParentId == change.ParentId)).ToArray(),
            metadata.Revision, reset, reset ? [] : messages, cutoff, Access: access);
    }
    public async Task<Versioned<SendOperation>?> GetOperationAsync(string tenant, string conversation, string id, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Partition(tenant, conversation), $"op:{Key(id)}", ct);
        return value is null ? null : new(Decode<SendOperation>(value.Value), value.Version);
    }
    private PartitionDocument Operation(SendOperation operation) => Wrap($"op:{Key(operation.Id)}",
        Partition(operation.TenantId, operation.ConversationId), "operation", operation.TenantId, operation.ConversationId,
        operation with { Body = "" }, 14 * 86400, operation.ActorId);
    public Task<bool> CreateOperationAsync(SendOperation value, CancellationToken ct) =>
        documents.WriteAsync([new(Operation(value), null)], ct);
    public async Task<bool> SaveOperationAsync(Versioned<SendOperation> value, CancellationToken ct)
    {
        List<DocumentWrite> writes = [new(Operation(value.Value), value.Version)];
        if (value.Value.State == OperationState.Dispatching)
        {
            var pk = Partition(value.Value.TenantId, value.Value.ConversationId);
            var metadata = await documents.ReadCurrentAsync(pk, "conversation", ct);
            if (metadata is null || Decode<Conversation>(metadata.Value).Epoch != value.Value.Epoch)
                throw new AppError(503, "writer_fenced", "Conversation ownership changed before dispatch.");
            writes.Add(new(metadata.Value, metadata.Version));
        }
        return await documents.WriteAsync(writes, ct);
    }
    public Task<bool> CommitAsync(Versioned<Conversation> conversation, Versioned<SendOperation>? operation, CancellationToken ct) =>
        CommitProjectionAsync(conversation, operation, null, ct);
    private async Task<bool> CommitProjectionAsync(Versioned<Conversation> conversation, Versioned<SendOperation>? operation,
        string? verifiedAgainst, CancellationToken ct)
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
        var generation = current.Value.RepairGeneration;
        if (incoming is not null && incoming.CreatedAt >= Now.AddDays(-7))
        {
            var id = MessageId(incoming.Id, incoming.ParentId);
            var previous = await documents.ReadCurrentAsync(pk, id, ct);
            var old = previous is null ? null : Decode<Message>(previous.Value);
            if (verifiedAgainst is not null && old?.SourceVersion != verifiedAgainst)
                throw new AppError(409, "source_changed", "The projection changed during authoritative verification.");
            var tombstone = await documents.ReadCurrentAsync(pk, $"tombstone:{MessageKey(incoming.Id, incoming.ParentId)}", ct);
            var same = old is not null && (old.SourceVersion == incoming.SourceVersion ||
                old with { SourceVersion = incoming.SourceVersion } == incoming);
            var older = old is not null && incoming.ModifiedAt < old.ModifiedAt;
            if (!same && !older && (incoming.Deleted || tombstone is null))
            {
                if (old is not null && old.ModifiedAt == incoming.ModifiedAt && old.Deleted != incoming.Deleted && !incoming.Deleted)
                { ready = false; generation++; }
                else if (verifiedAgainst is null && old is not null && old.ModifiedAt == incoming.ModifiedAt && !incoming.Deleted &&
                    (old.Body != incoming.Body || old.ParentId != incoming.ParentId))
                { ready = false; generation++; }
                else
                {
                    revision++;
                    var clean = incoming.Deleted ? incoming with { Body = "", ContentNotice = null, TeamsUrl = null, HtmlBody = null } : incoming;
                    var ttl = Math.Max(1, (int)(incoming.CreatedAt.AddDays(7) - Now).TotalSeconds);
                    writes.Add(new(Wrap(id, pk, "message", value.TenantId, value.Id, clean, ttl,
                        at: incoming.CreatedAt, revision: revision, parent: incoming.ParentId ?? "", sourceId: incoming.Id), previous?.Version));
                    if (incoming.Deleted && tombstone is null)
                        writes.Add(new(Wrap($"tombstone:{MessageKey(incoming.Id, incoming.ParentId)}", pk, "tombstone",
                            value.TenantId, value.Id, incoming.Id, 14 * 86400), null));
                    var change = new OutboxChange(Guid.NewGuid().ToString("N"), value.TenantId, value.Id, revision, Now);
                    writes.Add(new(Wrap($"outbox:{change.Id}", pk, "outbox", value.TenantId, value.Id, change,
                        14 * 86400, revision: revision, pending: true), null));
                    writes.Add(new(Wrap($"change:{revision:D20}", pk, "change", value.TenantId, value.Id,
                        new Change(revision, incoming.Id, Now, incoming.ParentId), 14 * 86400, at: incoming.CreatedAt, revision: revision), null));
                }
            }
        }
        var metadata = current.Value with { Revision = revision, CoverageReady = ready, RepairGeneration = generation,
            Messages = [], Members = [], Changes = [], Tombstones = [] };
        writes.Add(new(Wrap("conversation", pk, "conversation", value.TenantId, value.Id, metadata), current.Version));
        var committed = await documents.WriteAsync(writes, ct);
        if (committed && revision > current.Value.Revision) LiveTelemetry.ProjectionChanges.Add(1);
        return committed;
    }
    public async Task<bool> ApplyAsync(string tenant, string conversation, Message message, CancellationToken ct)
        => await ApplyMessageAsync(tenant, conversation, message, null, ct);
    public Task<bool> ApplyVerifiedAsync(string tenant, string conversation, Message message, string expectedSourceVersion, CancellationToken ct) =>
        ApplyMessageAsync(tenant, conversation, message, expectedSourceVersion, ct);
    private async Task<bool> ApplyMessageAsync(string tenant, string conversation, Message message, string? verifiedAgainst, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await GetAsync(tenant, conversation, ct) ?? throw new AppError(404, "resource_unknown", "Resource is not onboarded.");
            if (await CommitProjectionAsync(current with { Value = current.Value with { Messages = [message] } }, null, verifiedAgainst, ct)) return true;
        }
        throw new AppError(503, "projection_conflict", "Projection concurrency limit exceeded.");
    }
    public async Task<Coverage?> ReadCoverageAsync(string tenant, string conversation, CancellationToken ct)
    {
        if (await GetAsync(tenant, conversation, ct) is null) return null;
        var value = await documents.ReadCurrentAsync(Partition(tenant, conversation), "coverage", ct);
        var coverage = value is null ? null : Decode<Coverage>(value.Value);
        if (coverage is not null && (coverage.TenantId != tenant || coverage.ConversationId != conversation))
            throw new InvalidOperationException("Coverage checkpoint does not match its requested resource.");
        return coverage;
    }
    public async Task<Versioned<ReadViewState>?> ReadViewAsync(string tenant, string conversation, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Partition(tenant, conversation), ReadViewId(parent), ct);
        return value is null ? null : new(Decode<ReadViewState>(value.Value), value.Version);
    }
    public async Task<bool> SaveReadViewAsync(string tenant, string conversation, string? parent,
        Versioned<ReadViewState>? previous, ReadViewState next, CancellationToken ct)
    {
        var pk = Partition(tenant, conversation);
        var metadata = await documents.ReadCurrentAsync(pk, "conversation", ct);
        if (metadata is null || Decode<Conversation>(metadata.Value).Epoch != next.Epoch)
            throw new AppError(503, "writer_fenced", "Conversation ownership changed during read refresh.");
        return await documents.WriteAsync([
            new(Wrap(ReadViewId(parent), pk, "read-view", tenant, conversation, next), previous?.Version),
            new(metadata.Value, metadata.Version)], ct);
    }
    public async Task SetCoverageAsync(Coverage coverage, CancellationToken ct)
    {
        var current = await GetAsync(coverage.TenantId, coverage.ConversationId, ct) ?? throw new AppError(404, "resource_unknown", "Resource is not onboarded.");
        if (coverage.Ready && (coverage.VerifiedAt is null || coverage.VerifiedAt > Now ||
            (coverage.FreshUntil ?? coverage.VerifiedAt?.AddMinutes(5)) <= Now ||
            coverage.RepairGeneration is not null && coverage.RepairGeneration != current.Value.RepairGeneration))
            throw new AppError(409, "coverage_evidence_invalid", "Coverage evidence is expired or superseded by a newer repair requirement.");
        var pk = Partition(coverage.TenantId, coverage.ConversationId);
        var checkpoint = await documents.ReadCurrentAsync(pk, "coverage", ct);
        List<DocumentWrite> writes = [
            new(Wrap("coverage", pk, "coverage", coverage.TenantId, coverage.ConversationId, coverage), checkpoint?.Version),
            new(Wrap("conversation", pk, "conversation", coverage.TenantId, coverage.ConversationId,
                current.Value with { CoverageReady = coverage.Ready, CoverageUntil = coverage.Ready ?
                    coverage.FreshUntil ?? coverage.VerifiedAt?.AddMinutes(5) : null,
                    RepairGeneration = current.Value.RepairGeneration + (coverage.Ready ? 0 : 1) }), current.Version)];
        var historical = checkpoint is null ? null : Decode<Coverage>(checkpoint.Value);
        if (!coverage.Ready && historical is { Ready: true, VerifiedAt: { } verified })
        {
            var view = await ReadViewAsync(coverage.TenantId, coverage.ConversationId, null, ct);
            if (view?.Value.Epoch != current.Value.Epoch || !view.Value.Complete)
                writes.Add(new(Wrap(ReadViewId(null), pk, "read-view", coverage.TenantId, coverage.ConversationId,
                    new ReadViewState(current.Value.Epoch, true, null, verified)), view?.Version));
        }
        if (!await documents.WriteAsync(writes, ct))
            throw new AppError(409, "coverage_conflict", "Coverage changed concurrently.");
    }
    public async Task<EntitlementPage> RecipientsAsync(string tenant, string conversation, string? cursor, CancellationToken ct)
    {
        var page = await Authority.QueryAsync(new(tenant, "member", Partition(tenant, conversation)), 100, cursor, ct);
        return new(page.Items.Select(d => Decode<Entitlement>(d.Value)).Where(g => g.Active && g.FreshUntil > Now &&
            g.VerifiedAt <= Now && g.FreshUntil - g.VerifiedAt <= TimeSpan.FromMinutes(5)).ToArray(), page.Cursor);
    }
    public async Task<IReadOnlyList<OutboxChange>> PendingOutboxAsync(string tenant, CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(tenant, "outbox", Pending: true, OldestFirst: true), 100, null, ct);
        return page.Items.Select(d => Decode<OutboxChange>(d.Value)).ToArray();
    }
    public async Task CompleteOutboxAsync(OutboxChange change, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Partition(change.TenantId, change.ConversationId), $"outbox:{change.Id}", ct);
        if (value is not null && value.Value.Pending &&
            !await documents.WriteAsync([new(value.Value with { Pending = false }, value.Version)], ct))
            throw new AppError(409, "outbox_conflict", "Outbox publication checkpoint changed concurrently.");
    }
    public async Task<Versioned<DeliveryProgress>> DeliveryAsync(OutboxChange change, CancellationToken ct)
    {
        var pk = Partition(change.TenantId, change.ConversationId);
        var outbox = await documents.ReadCurrentAsync(pk, $"outbox:{change.Id}", ct)
            ?? throw new AppError(410, "outbox_expired", "Delivery intent is missing or expired; inspect recovery evidence.");
        if (Decode<OutboxChange>(outbox.Value) != change)
            throw new AppError(400, "delivery_identity_mismatch", "Delivery work does not match its durable intent.");
        if (!outbox.Value.Pending) return new(new(0, null, "", DateTimeOffset.MinValue, true), "");
        var progress = await documents.ReadCurrentAsync(pk, $"delivery:{change.Id}", ct);
        return progress is null ? new(new(0, null, "", DateTimeOffset.MinValue, false), "") :
            new(Decode<DeliveryProgress>(progress.Value), progress.Version);
    }
    public async Task<bool> SaveDeliveryAsync(OutboxChange change, Versioned<DeliveryProgress> previous,
        DeliveryProgress next, CancellationToken ct)
    {
        if (next.Page < previous.Value.Page || next.Page > previous.Value.Page + 1 || previous.Value.Complete)
            throw new InvalidOperationException("Delivery progress cannot rewind or skip a page.");
        var pk = Partition(change.TenantId, change.ConversationId);
        var outbox = await documents.ReadCurrentAsync(pk, $"outbox:{change.Id}", ct);
        if (outbox is null || !outbox.Value.Pending) return false;
        if (Decode<OutboxChange>(outbox.Value) != change)
            throw new AppError(400, "delivery_identity_mismatch", "Delivery intent changed.");
        var ttl = Math.Max(1, (int)(change.CreatedAt.AddDays(14) - Now).TotalSeconds);
        List<DocumentWrite> writes = [new(Wrap($"delivery:{change.Id}", pk, "delivery", change.TenantId,
            change.ConversationId, next, ttl), previous.Version.Length == 0 ? null : previous.Version)];
        if (next.Complete)
        {
            var metadata = await documents.ReadCurrentAsync(pk, "conversation", ct)
                ?? throw new AppError(503, "delivery_resource_missing", "Delivery metadata is missing.");
            var conversation = Decode<Conversation>(metadata.Value);
            if (next.TargetRevision < change.Revision || next.TargetRevision > conversation.Revision)
                throw new InvalidOperationException("Delivery cannot publish an uncommitted revision.");
            writes.Add(new(outbox.Value with { Pending = false }, outbox.Version));
            writes.Add(new(Wrap("conversation", pk, "conversation", change.TenantId, change.ConversationId,
                conversation with { PublishedRevision = Math.Max(conversation.PublishedRevision, next.TargetRevision) }), metadata.Version));
        }
        return await documents.WriteAsync(writes, ct);
    }
    public async Task<bool> IsBlockedAsync(string tenant, string actor, CancellationToken ct)
    {
        var value = await Authority.ReadCurrentAsync(PrincipalPartition(tenant, actor), "revocation", ct);
        return value is not null && Decode<bool>(value.Value);
    }
    public async Task SetBlockedAsync(string tenant, string actor, bool blocked, CancellationToken ct)
    {
        var pk = PrincipalPartition(tenant, actor);
        var current = await Authority.ReadCurrentAsync(pk, "revocation", ct);
        if (!await Authority.WriteAsync([new(Wrap("revocation", pk, "revocation", tenant, "", blocked), current?.Version)], ct))
            throw new AppError(409, "revocation_conflict", "Revocation changed concurrently.");
    }
}
