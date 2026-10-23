namespace Teams.Core;

public sealed record Actor(string TenantId, string Id, string Name, string SessionId = "fixture");
public sealed record Member(string Id, string Name, DateTimeOffset HistoryFrom, bool Active = true,
    DateTimeOffset? FreshUntil = null);
public sealed record Message(string Id, string ActorId, string Author, string Body,
    DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt, string? ParentId = null,
    bool Deleted = false, string SourceVersion = "1", string? ContentNotice = null, string? TeamsUrl = null, string? HtmlBody = null);
public sealed record Change(long Revision, string MessageId, DateTimeOffset At, string? ParentId = null);
public sealed record Tombstone(string MessageId, DateTimeOffset At);
public sealed record Conversation(string Id, string TenantId, string Title, string Kind,
    string? Team, List<Member> Members, List<Message> Messages, List<Change> Changes,
    long Revision = 0, long PublishedRevision = 0, bool CoverageReady = true,
    string? GraphChatId = null, string? GraphTeamId = null, string? GraphChannelId = null,
    long Epoch = 0, List<Tombstone>? Tombstones = null, DateTimeOffset? CoverageUntil = null, long RepairGeneration = 0,
    long CacheGeneration = 0, long CacheResetRevision = 0, string? ActiveCacheCleanup = null, string? LastCacheCleanup = null,
    long? CacheInvalidationRevision = null);
public enum OperationState { Pending, Dispatching, Confirmed, Rejected, Unknown }
public sealed record SendOperation(string Id, string TenantId, string ConversationId, string ActorId,
    string Fingerprint, string Body, string? ParentId, OperationState State, DateTimeOffset CreatedAt,
    string? GraphMessageId = null, string? Error = null, long Epoch = 0,
    DateTimeOffset? Deadline = null, DateTimeOffset? AttemptUntil = null, DateTimeOffset? RetryAt = null,
    int Attempts = 0, int Throttles = 0, int? FirstStatus = null);
public sealed record Versioned<T>(T Value, string Version);
public sealed record SendCommand(string OperationId, string Window, string Body, string? ParentId);
public sealed record WriteResult(OperationState State, Message? Message, string? Error = null,
    TimeSpan? RetryAfter = null, int? Status = null, bool Dispatched = true);
public sealed record OperationView(string Id, OperationState State, string? MessageId, string? Error,
    int Attempts = 0, int Throttles = 0, int? FirstStatus = null, DateTimeOffset? RetryAt = null);
public sealed record ConversationView(string Id, string Title, string Kind, string? Team, long Revision, bool IsDirect = false);
public sealed record ReadAccess(DateTimeOffset HistoryFrom, DateTimeOffset ValidUntil, long Epoch)
{
    public static ReadAccess Create(Member member, Conversation conversation, DateTimeOffset now)
    {
        if (member.FreshUntil is not { } authorityUntil || authorityUntil <= now)
            throw new AppError(503, "authorization_stale", "Current policy authority is required before serving this content.");
        return new(member.HistoryFrom > now.AddDays(-7) ? member.HistoryFrom : now.AddDays(-7),
            authorityUntil, conversation.Epoch);
    }
}
public sealed record MessagePage(IReadOnlyList<Message> Messages, string? Cursor, long Revision, string Window,
    Message? ParentMessage = null, ReadAccess? Access = null,
    [property: System.Text.Json.Serialization.JsonIgnore] ReadRefreshRequest? Refresh = null,
    [property: System.Text.Json.Serialization.JsonIgnore] bool Pending = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReadEvidence? Diagnostics = null);
public sealed record PendingRead(string State, int RetryAfterMs, ReadAccess? Access,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReadEvidence? Diagnostics = null);
public sealed record ChangePage(IReadOnlyList<Change> Changes, long Revision, bool ResetRequired,
    IReadOnlyList<Message>? Messages = null, DateTimeOffset? HistoryFrom = null, string? Window = null, ReadAccess? Access = null,
    bool CacheReset = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReadEvidence? Diagnostics = null);

public sealed class AppError(int status, string code, string message, ReadAccess? access = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public ReadAccess? Access { get; } = access;
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
}

public interface IGraphTransport
{
    Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct);
    Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct);
}
public interface ICommandBudget
{
    Task AcquireAsync(Actor actor, Conversation conversation, CancellationToken ct);
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
