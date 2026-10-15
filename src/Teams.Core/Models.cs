namespace Teams.Core;

public sealed record Actor(string TenantId, string Id, string Name, string SessionId = "fixture");
public sealed record Member(string Id, string Name, DateTimeOffset HistoryFrom, bool Active = true);
public sealed record Message(string Id, string ActorId, string Author, string Body,
    DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt, string? ParentId = null,
    bool Deleted = false, string SourceVersion = "1");
public sealed record Change(long Revision, string MessageId, DateTimeOffset At);
public sealed record Tombstone(string MessageId, DateTimeOffset At);
public sealed record Conversation(string Id, string TenantId, string Title, string Kind,
    string? Team, List<Member> Members, List<Message> Messages, List<Change> Changes,
    long Revision = 0, long PublishedRevision = 0, bool CoverageReady = true,
    string? GraphChatId = null, string? GraphTeamId = null, string? GraphChannelId = null,
    long Epoch = 0, List<Tombstone>? Tombstones = null);
public enum OperationState { Pending, Dispatching, Confirmed, Rejected, Unknown }
public sealed record SendOperation(string Id, string TenantId, string ConversationId, string ActorId,
    string Fingerprint, string Body, string? ParentId, OperationState State, DateTimeOffset CreatedAt,
    string? GraphMessageId = null, string? Error = null, long Epoch = 0);
public sealed record Versioned<T>(T Value, string Version);
public sealed record SendCommand(string OperationId, string Window, string Body, string? ParentId);
public sealed record WriteResult(OperationState State, Message? Message, string? Error = null);
public sealed record OperationView(string Id, OperationState State, string? MessageId, string? Error, bool Simulated);
public sealed record ConversationView(string Id, string Title, string Kind, string? Team, long Revision);
public sealed record MessagePage(IReadOnlyList<Message> Messages, string? Cursor, long Revision, string Window, bool Simulated, Message? ParentMessage = null);
public sealed record ChangePage(IReadOnlyList<Change> Changes, long Revision, bool ResetRequired);
public sealed record EventEnvelope(string TenantId, string ConversationId, Message Message, int SchemaVersion = 1);
public sealed record InboxDelivery(EventEnvelope Envelope, string Receipt);

public sealed class AppError(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public interface IPrincipalRevocations
{
    Task<bool> IsBlockedAsync(string tenant, string actor, CancellationToken ct);
    Task SetBlockedAsync(string tenant, string actor, bool blocked, CancellationToken ct);
}
public interface IStateStore : IPrincipalRevocations
{
    Task<IReadOnlyList<Conversation>> ListAsync(string tenant, CancellationToken ct);
    Task<Versioned<Conversation>?> GetAsync(string tenant, string id, CancellationToken ct);
    Task<bool> CreateAsync(Conversation value, CancellationToken ct);
    Task<bool> SaveAsync(Versioned<Conversation> value, CancellationToken ct);
    Task<Versioned<SendOperation>?> GetOperationAsync(string tenant, string conversation, string id, CancellationToken ct);
    Task<bool> CreateOperationAsync(SendOperation value, CancellationToken ct);
    Task<bool> SaveOperationAsync(Versioned<SendOperation> value, CancellationToken ct);
    Task<bool> CommitAsync(Versioned<Conversation> conversation, Versioned<SendOperation>? operation, CancellationToken ct);
    Task<WriteResult?> GetSimulatorReceiptAsync(string tenant, string conversation, string operation, CancellationToken ct);
    Task SaveSimulatorReceiptAsync(string tenant, string conversation, string operation, WriteResult result, CancellationToken ct);
}

public interface IGraphTransport
{
    bool Simulated { get; }
    Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct);
    Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct);
}
public interface ICommandBudget
{
    Task AcquireAsync(Actor actor, Conversation conversation, CancellationToken ct);
}
public interface IEventInbox
{
    Task EnqueueAsync(EventEnvelope envelope, CancellationToken ct);
    Task<InboxDelivery?> ReceiveAsync(CancellationToken ct);
    Task CompleteAsync(string receipt, CancellationToken ct);
    Task AbandonAsync(string receipt, CancellationToken ct);
    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct);
}
public interface IRealtime
{
    Task PublishAsync(Conversation conversation, CancellationToken ct);
    Task RevokeAsync(string tenant, string actorId, CancellationToken ct);
}
public interface ICommandTokens
{
    string Issue(Actor actor, string conversation, string purpose, string value, TimeSpan lifetime);
    string Validate(Actor actor, string conversation, string purpose, string token);
}
