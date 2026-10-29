namespace Teams.Core;

public sealed record SubscriptionScope(string Id, string Path)
{
    public bool Covers(ApprovedResource resource) => Path == resource.Path ||
        Path == "chats/getAllMessages" && resource.Kind == "chat" ||
        Path == "teams/getAllMessages" && resource.Kind == "channel";
}

public sealed record ResourceRegistration(ApprovedResource Resource, DateTimeOffset HistoryFrom, bool CanSend);
public interface IResourceRegistry
{
    Task<IReadOnlyList<ResourceRegistration>> ListAsync(CancellationToken ct);
    Task<ResourceRegistration?> FindAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<SubscriptionScope>> ScopesAsync(CancellationToken ct);
    Task<ResourceRegistration> RegisterAsync(ResourceRegistration resource, CancellationToken ct);
}

public sealed record ChannelSelection(string TeamId, string ChannelId);
public sealed record OpenChannel(string OperationId, string Selection);
public sealed record ChannelWork(string TenantId, string ActorId, string ActorName, string OperationId,
    string Kind, string? TeamId = null, string? ChannelId = null, string? Continuation = null, int SchemaVersion = 1);
public sealed record DiscoveredTeam(string Id, string Name);
public sealed record DiscoveredChannel(string Id, string TeamId, string Name, string TeamName);
public sealed record ChannelOperation(string Id, string ActorId, string Fingerprint, string Kind, string State,
    long Revision, DateTimeOffset UpdatedAt, DateTimeOffset? FreshUntil = null,
    IReadOnlyList<DiscoveredTeam>? Teams = null, IReadOnlyList<DiscoveredChannel>? Channels = null,
    string? Continuation = null, string? ConversationId = null, string? Error = null,
    string? Owner = null, DateTimeOffset? LeaseUntil = null, int Attempts = 0, string? TeamId = null,
    ChannelWork? Request = null);
public sealed record RealtimeEvent(string Type, long Revision, long Sequence = 0, string? OperationId = null,
    string? ConversationId = null, string? ParentId = null, long? Generation = null, int SchemaVersion = 1);
public sealed record ActorEventWork(string Id, string TenantId, string ActorId, RealtimeEvent Event, DateTimeOffset CreatedAt);
public sealed record ActorEventPage(IReadOnlyList<RealtimeEvent> Events, long Sequence, bool ResetRequired, bool More);
public interface IChannelWorkQueue
{
    Task EnqueueAsync(ChannelWork work, DateTimeOffset? scheduledAt, CancellationToken ct);
}
