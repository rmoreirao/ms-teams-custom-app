namespace Teams.Core;

public sealed record Entitlement(string TenantId, string ConversationId, string ActorId, string Name,
    DateTimeOffset HistoryFrom, DateTimeOffset VerifiedAt, DateTimeOffset FreshUntil, string AuthorityVersion,
    bool Active, bool CanSend, long Epoch);
public sealed record CatalogPage(IReadOnlyList<ConversationView> Items, string? Cursor);
public sealed record EntitlementPage(IReadOnlyList<Entitlement> Items, string? Cursor);
public sealed record DirectChatAuthority(string TenantId, string ResourceId, string GraphChatId, string ChatType,
    IReadOnlyList<string> MemberIds, DateTimeOffset VerifiedAt, DateTimeOffset FreshUntil, string AuthorityVersion);
public sealed record ProjectionScan(IReadOnlyList<Message> Items, string? Cursor);
public sealed record Coverage(string TenantId, string ConversationId, bool Ready, DateTimeOffset? VerifiedAt,
    string? NextLink, string? Reason, DateTimeOffset? FreshUntil = null, long? RepairGeneration = null);
public sealed record ReadViewState(long Epoch, bool Complete, string? NextLink, DateTimeOffset VerifiedAt,
    DateTimeOffset? OldestAt = null, bool ParentMissing = false, long CacheGeneration = 0);
public sealed record ReadRefreshRequest(string TenantId, string ConversationId, long Epoch, string? Parent,
    DateTimeOffset? Before, string? BeforeId, DateTimeOffset RequestedAt, int SchemaVersion = 1, long CacheGeneration = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { TenantId, ConversationId, Epoch, Parent, Before, BeforeId, CacheGeneration })));
}
public interface IReadRefresh
{
    Task RequestAsync(ReadRefreshRequest request, bool required, CancellationToken ct);
}
public sealed record OutboxChange(string Id, string TenantId, string ConversationId, long Revision, DateTimeOffset CreatedAt);
public sealed record DeliveryProgress(long Page, string? Cursor, string Owner, DateTimeOffset LeaseUntil, bool Complete,
    long TargetRevision = 0);
public sealed record DeliveryWork(OutboxChange Change, long Page = 0, string RecoveryId = "", int SchemaVersion = 1);
public interface IDeliveryWorkQueue
{
    Task EnqueueAsync(DeliveryWork work, DateTimeOffset? scheduledAt, CancellationToken ct);
}
public sealed record ApprovedResource(string Id, string Kind, string Title, string? Team,
    string? GraphChatId, string? GraphTeamId, string? GraphChannelId)
{
    public bool Matches(Conversation conversation) => conversation.Id == Id && conversation.Kind == Kind &&
        conversation.GraphChatId == GraphChatId && conversation.GraphTeamId == GraphTeamId &&
        conversation.GraphChannelId == GraphChannelId;
    public string Path => Kind switch
    {
        "chat" when !string.IsNullOrWhiteSpace(GraphChatId) => $"chats/{Uri.EscapeDataString(GraphChatId)}/messages",
        "channel" when !string.IsNullOrWhiteSpace(GraphTeamId) && !string.IsNullOrWhiteSpace(GraphChannelId) =>
            $"teams/{Uri.EscapeDataString(GraphTeamId)}/channels/{Uri.EscapeDataString(GraphChannelId)}/messages",
        _ => throw new InvalidOperationException("Approved resource mapping is incomplete.")
    };
}
public interface ILiveStateStore : IStateStore
{
    Task<Member> GetMemberAsync(Actor actor, string conversation, bool sending, CancellationToken ct);
    Task<CatalogPage> CatalogAsync(Actor actor, string? cursor, CancellationToken ct);
    Task<IReadOnlyList<Message>> MessagesAsync(Actor actor, string conversation, string? parent,
        DateTimeOffset historyFrom, DateTimeOffset? before, string? beforeId, CancellationToken ct, ReadEvidence? evidence = null);
    Task<CacheSnapshot> InspectCacheAsync(Actor actor, string conversation, CancellationToken ct);
    Task<CacheCleanup> CleanCacheAsync(Actor actor, string conversation, CacheCleanupCommand command, CancellationToken ct);
    Task<CacheCleanup> CacheCleanupAsync(Actor actor, string conversation, string operationId, CancellationToken ct);
    Task<Message?> MessageAsync(string tenant, string conversation, string id, string? parent, CancellationToken ct);
    Task<Message?> CurrentMessageAsync(string tenant, string conversation, string id, string? parent, CancellationToken ct);
    Task<bool> ApplyAsync(string tenant, string conversation, Message message, CancellationToken ct);
    Task<bool> ApplyVerifiedAsync(string tenant, string conversation, Message message, string expectedSourceVersion, CancellationToken ct);
    Task SetCoverageAsync(Coverage coverage, CancellationToken ct);
    Task<Coverage?> ReadCoverageAsync(string tenant, string conversation, CancellationToken ct);
    Task<Versioned<ReadViewState>?> ReadViewAsync(string tenant, string conversation, string? parent, CancellationToken ct);
    Task<bool> SaveReadViewAsync(string tenant, string conversation, string? parent,
        Versioned<ReadViewState>? previous, ReadViewState next, CancellationToken ct);
    Task<Versioned<Entitlement>?> SetEntitlementAsync(Entitlement entitlement, CancellationToken ct);
    Task CompleteEntitlementRevocationAsync(Versioned<Entitlement> revocation, CancellationToken ct);
    Task SetDirectChatAuthorityAsync(DirectChatAuthority evidence, CancellationToken ct);
    Task<DirectChatAuthority?> DirectChatAuthorityAsync(string tenant, string resource, CancellationToken ct);
    Task<EntitlementPage> RecipientsAsync(string tenant, string conversation, string? cursor, CancellationToken ct);
    Task<IReadOnlyList<OutboxChange>> PendingOutboxAsync(string tenant, CancellationToken ct);
    Task CompleteOutboxAsync(OutboxChange change, CancellationToken ct);
    Task<Versioned<DeliveryProgress>> DeliveryAsync(OutboxChange change, CancellationToken ct);
    Task<bool> SaveDeliveryAsync(OutboxChange change, Versioned<DeliveryProgress> previous,
        DeliveryProgress next, CancellationToken ct);
    Task<ProjectionScan> ScanAsync(string tenant, string conversation, string? parent, string? cursor, CancellationToken ct);
    Task<ChangePage> ChangesAsync(Actor actor, string conversation, long after, CancellationToken ct, ReadEvidence? evidence = null);
}
public interface IGraphBackground
{
    IGraphBackground ForResource(string resourcePath) => this;
    Task<System.Text.Json.JsonElement> GetAsync(string path, CancellationToken ct);
    Task<System.Text.Json.JsonElement> PostAsync(string path, object body, CancellationToken ct);
    Task<System.Text.Json.JsonElement> PatchAsync(string path, object body, CancellationToken ct);
}
public interface IRecentMessageCache
{
    Task<RecentCacheRead> ReadAsync(string scope, string page, CancellationToken ct);
    Task<string> WriteAsync(string scope, string page, string generation, IReadOnlyList<Message> messages, CancellationToken ct);
    Task<RedisCacheSnapshot> InspectAsync(string scope, CancellationToken ct);
    Task<int> EvictAsync(string scope, string operationId, CancellationToken ct);
}
