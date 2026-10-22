using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public sealed record SubscriptionState(string ScopeId, string Resource, string ClientState, string? GraphId,
    DateTimeOffset Expires, string CertificateId, bool CreateAttempted, string Owner, DateTimeOffset LeaseUntil);
public sealed record RepairFailure(string Code, int Attempts, DateTimeOffset At, DateTimeOffset? NextAttemptAt, bool Blocked);
public sealed record RepairRecovery(int Restarts, DateTimeOffset At, string Reason);
public sealed record RepairState(string ResourceId, string Path, string? ParentId, string? NextLink, bool Pending,
    DateTimeOffset UpdatedAt, bool Verifying = false, string? ScanCursor = null, long? Generation = null,
    RepairFailure? Failure = null, DateTimeOffset? LastProgressAt = null, RepairRecovery? Recovery = null)
{
    [JsonIgnore]
    public string Status => !Pending ? "completed" : Failure is { Blocked: true } ? "blocked" :
        Failure?.NextAttemptAt is not null ? "deferred" : "runnable";
    public bool IsRunnable(DateTimeOffset now) => Pending && Failure is not { Blocked: true } &&
        (Failure?.NextAttemptAt is null || Failure.NextAttemptAt <= now);
    public RepairState Progress(DateTimeOffset now) => this with
    {
        UpdatedAt = now, LastProgressAt = now,
        Failure = Failure is null ? null : Failure with { Attempts = 0, NextAttemptAt = null, Blocked = false }
    };
}
public sealed record LifecycleSignal(string Action, DateTimeOffset At, bool Handled = false);
public sealed record NotificationReceipt(string ResourceId, string MessageId, string? ParentId, long Revision,
    string SourceVersion, DateTimeOffset ReceivedAt, DateTimeOffset ProjectedAt);
public sealed record ReadRefreshLease(string Owner, DateTimeOffset LeaseUntil,
    DateTimeOffset? CompletedThrough = null, DateTimeOffset? RetryAt = null, int Attempts = 0, string? Failure = null,
    DateTimeOffset? RequestedAt = null);
public sealed class LiveControlStore(IPartitionDocuments documents, TimeProvider clock, string tenant)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string RepairId(string? parent) => parent is null ? "root" : Hash(parent)[..16];
    private string Pk(string id) => $"{tenant}|control|{Hash(id)}";
    private string? repairCursor;
    private PartitionDocument Wrap<T>(string id, string pk, string kind, T value, bool pending = false, int ttl = -1) =>
        new(id, pk, kind, tenant, value is RepairState repair ? repair.ResourceId : "", "",
            clock.GetUtcNow(), 0, ttl, JsonSerializer.Serialize(value, Json), pending,
            SortTime: clock.GetUtcNow().ToUniversalTime().ToString("O"));
    private static T Decode<T>(PartitionDocument value)
    {
        if (value.SchemaVersion != 1) throw new InvalidOperationException("Unsupported control schema; migration is required.");
        return JsonSerializer.Deserialize<T>(value.Json, Json)
            ?? throw new InvalidOperationException("Control state is invalid.");
    }
    public async Task<Versioned<SubscriptionState>?> SubscriptionAsync(string scope, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Pk(scope), "subscription", ct);
        return value is null ? null : new(Decode<SubscriptionState>(value.Value), value.Version);
    }
    public Task<bool> SaveSubscriptionAsync(SubscriptionState value, string? version, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("subscription", Pk(value.ScopeId), "subscription", value), version)], ct);
    public async Task<Versioned<ReadRefreshLease>?> ReadRefreshAsync(string key, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Pk($"read-refresh:{key}"), "refresh", ct);
        return value is null ? null : new(Decode<ReadRefreshLease>(value.Value), value.Version);
    }
    public Task<bool> SaveReadRefreshAsync(string key, Versioned<ReadRefreshLease>? previous,
        ReadRefreshLease next, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("refresh", Pk($"read-refresh:{key}"), "read-refresh", next,
            ttl: 86400), previous?.Version)], ct);
    public async Task<SubscriptionState?> FindSubscriptionAsync(string graphId, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Pk($"subscription:{graphId}"), "lookup", ct);
        if (value is null) return null;
        var scope = Decode<string>(value.Value);
        return (await SubscriptionAsync(scope, ct))?.Value;
    }
    public async Task RegisterLookupAsync(SubscriptionState value, CancellationToken ct)
    {
        if (value.GraphId is null) throw new InvalidOperationException("Subscription ID required.");
        var pk = Pk($"subscription:{value.GraphId}");
        var current = await documents.ReadCurrentAsync(pk, "lookup", ct);
        if (current is not null && Decode<string>(current.Value) != value.ScopeId)
            throw new InvalidOperationException("Subscription ID belongs to another scope.");
        if (current is null && !await documents.WriteAsync([new(Wrap("lookup", pk, "lookup", value.ScopeId), null)], ct))
            throw new AppError(409, "subscription_conflict", "Subscription lookup changed concurrently.");
    }
    public async Task EnqueueRepairAsync(ApprovedResource resource, string? path, string? parent, CancellationToken ct)
    {
        var jobPath = path ?? RepairPath(resource, parent);
        GraphUrls.Validate(jobPath, parent is null ? resource.Path : $"{resource.Path}/{Uri.EscapeDataString(parent)}/replies");
        var pk = Pk($"repair:{resource.Id}:{parent}");
        var current = await documents.ReadCurrentAsync(pk, "repair", ct);
        if (current is not null && Decode<RepairState>(current.Value).Pending) return;
        var value = new RepairState(resource.Id, jobPath, parent, null, true, clock.GetUtcNow());
        if (!await documents.WriteAsync([new(Wrap("repair", pk, "repair", value, true), current?.Version)], ct))
            throw new AppError(409, "repair_conflict", "Repair scheduling changed concurrently.");
    }
    public async Task<Versioned<RepairState>?> RepairAsync(string resource, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Pk($"repair:{resource}:{parent}"), "repair", ct);
        return value is null ? null : new(Decode<RepairState>(value.Value), value.Version);
    }
    public Task<bool> SaveRepairAsync(Versioned<RepairState> value, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("repair", Pk($"repair:{value.Value.ResourceId}:{value.Value.ParentId}"),
            "repair", value.Value, value.Value.Pending), value.Version)], ct);
    private static string RepairPath(ApprovedResource resource, string? parent) => parent is null
        ? $"{resource.Path}?$top=50" + (resource.Kind == "chat" ? "&$orderby=createdDateTime desc" : "")
        : $"{resource.Path}/{Uri.EscapeDataString(parent)}/replies?$top=50";
    public Task<bool> RestartRepairAsync(ApprovedResource resource, Versioned<RepairState> previous,
        long generation, string reason, CancellationToken ct)
    {
        if (previous.Value.ResourceId != resource.Id)
            throw new AppError(400, "repair_identity_mismatch", "Repair recovery cannot change its approved resource.");
        var now = clock.GetUtcNow();
        return SaveRepairAsync(previous with { Value = previous.Value with
        {
            Path = RepairPath(resource, previous.Value.ParentId), NextLink = null, ScanCursor = null,
            Verifying = false, Pending = true, Generation = generation,
            Failure = previous.Value.Failure is null ? null :
                previous.Value.Failure with { Attempts = 0, NextAttemptAt = null, Blocked = false },
            Recovery = new(previous.Value.Generation == generation ? (previous.Value.Recovery?.Restarts ?? 0) + 1 : 0, now, reason)
        } }, ct);
    }
    public async Task<RepairState?> FailRepairAsync(ApprovedResource resource, Versioned<RepairState> previous,
        long generation, string code, bool blocked, bool restart, CancellationToken ct)
    {
        if (previous.Value.ResourceId != resource.Id)
            throw new AppError(400, "repair_identity_mismatch", "Repair recovery cannot change its approved resource.");
        var now = clock.GetUtcNow();
        var attempts = Math.Min(30, (previous.Value.Failure?.Attempts ?? 0) + 1);
        var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempts, 6))) + Random.Shared.NextDouble());
        var next = previous.Value with
        {
            Failure = new(code, attempts, now, blocked ? null : now + delay, blocked)
        };
        if (restart)
            next = next with
            {
                Path = RepairPath(resource, next.ParentId), NextLink = null, ScanCursor = null,
                Verifying = false, Generation = generation,
                Recovery = new((previous.Value.Recovery?.Restarts ?? 0) + 1, now, code)
            };
        return await SaveRepairAsync(previous with { Value = next }, ct) ? next : null;
    }
    public async Task<IReadOnlyList<RepairState>> PendingRepairsAsync(CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(tenant, "repair", Pending: true, OldestFirst: true), 50, repairCursor, ct);
        repairCursor = page.Cursor;
        return page.Items.Select(d => Decode<RepairState>(d.Value)).Where(job => job.IsRunnable(clock.GetUtcNow())).ToArray();
    }
    public async Task<IReadOnlyList<RepairState>> UnresolvedRepairsAsync(string resource, CancellationToken ct)
    {
        List<RepairState> result = [];
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var items = await documents.QueryAsync(new(tenant, "repair", ConversationId: resource, Pending: true), 50, cursor, ct);
            result.AddRange(items.Items.Select(item => Decode<RepairState>(item.Value)));
            cursor = items.Cursor;
            if (cursor is null) return result;
        }
        throw new AppError(503, "repair_inspection_budget", "Repair inspection exceeded its bounded metadata budget.");
    }
    public async Task<bool> HasPendingRepairAsync(string resource, CancellationToken ct) =>
        (await documents.QueryAsync(new(tenant, "repair", ConversationId: resource, Pending: true), 1, null, ct)).Items.Count != 0;
    public async Task<Versioned<LifecycleSignal>?> LifecycleAsync(string scope, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Pk(scope), "lifecycle", ct);
        return value is null ? null : new(Decode<LifecycleSignal>(value.Value), value.Version);
    }
    public Task<bool> SaveLifecycleAsync(string scope, LifecycleSignal signal, string? version, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("lifecycle", Pk(scope), "lifecycle", signal), version)], ct);
    public async Task<NotificationReceipt?> ReceiptAsync(string resource, string message, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Pk($"receipt:{resource}:{parent}:{message}"), "receipt", ct);
        return value is null ? null : Decode<NotificationReceipt>(value.Value);
    }
    public async Task SaveReceiptAsync(NotificationReceipt receipt, CancellationToken ct)
    {
        var pk = Pk($"receipt:{receipt.ResourceId}:{receipt.ParentId}:{receipt.MessageId}");
        var existing = await documents.ReadCurrentAsync(pk, "receipt", ct);
        if (existing is not null && Decode<NotificationReceipt>(existing.Value).ReceivedAt > receipt.ReceivedAt) return;
        if (!await documents.WriteAsync([new(Wrap("receipt", pk, "notification-receipt", receipt, ttl: 14 * 86400), existing?.Version)], ct))
            throw new AppError(409, "receipt_conflict", "Notification provenance changed concurrently; retry the original delivery.");
    }
}
