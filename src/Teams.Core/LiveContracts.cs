namespace Teams.Core;

public sealed record Entitlement(string TenantId, string ConversationId, string ActorId, string Name,
    DateTimeOffset HistoryFrom, DateTimeOffset VerifiedAt, DateTimeOffset FreshUntil, string AuthorityVersion,
    bool Active, bool CanSend, long Epoch);
public sealed record CatalogPage(IReadOnlyList<ConversationView> Items, string? Cursor);
public sealed record EntitlementPage(IReadOnlyList<Entitlement> Items, string? Cursor);
public sealed record ProjectionScan(IReadOnlyList<Message> Items, string? Cursor);
public sealed record Coverage(string TenantId, string ConversationId, bool Ready, DateTimeOffset? VerifiedAt,
    string? NextLink, string? Reason, DateTimeOffset? FreshUntil = null, long? RepairGeneration = null);
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
        DateTimeOffset historyFrom, DateTimeOffset? before, string? beforeId, CancellationToken ct);
    Task<Message?> MessageAsync(string tenant, string conversation, string id, string? parent, CancellationToken ct);
    Task<bool> ApplyAsync(string tenant, string conversation, Message message, CancellationToken ct);
    Task<bool> ApplyVerifiedAsync(string tenant, string conversation, Message message, string expectedSourceVersion, CancellationToken ct);
    Task SetCoverageAsync(Coverage coverage, CancellationToken ct);
    Task SetEntitlementAsync(Entitlement entitlement, CancellationToken ct);
    Task<EntitlementPage> RecipientsAsync(string tenant, string conversation, string? cursor, CancellationToken ct);
    Task<IReadOnlyList<OutboxChange>> PendingOutboxAsync(string tenant, CancellationToken ct);
    Task CompleteOutboxAsync(OutboxChange change, CancellationToken ct);
    Task<Versioned<DeliveryProgress>> DeliveryAsync(OutboxChange change, CancellationToken ct);
    Task<bool> SaveDeliveryAsync(OutboxChange change, Versioned<DeliveryProgress> previous,
        DeliveryProgress next, CancellationToken ct);
    Task<ProjectionScan> ScanAsync(string tenant, string conversation, string? parent, string? cursor, CancellationToken ct);
    Task<ChangePage> ChangesAsync(Actor actor, string conversation, long after, CancellationToken ct);
}
public interface IGraphBackground
{
    Task<System.Text.Json.JsonElement> GetAsync(string path, CancellationToken ct);
    Task<System.Text.Json.JsonElement> PostAsync(string path, object body, CancellationToken ct);
    Task<System.Text.Json.JsonElement> PatchAsync(string path, object body, CancellationToken ct);
}
public interface IRecentMessageCache
{
    Task<IReadOnlyList<Message>?> ReadAsync(string key, CancellationToken ct);
    Task WriteAsync(string key, IReadOnlyList<Message> messages, CancellationToken ct);
}
