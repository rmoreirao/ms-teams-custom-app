using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public sealed record SubscriptionState(string ScopeId, string Resource, string ClientState, string? GraphId,
    DateTimeOffset Expires, string CertificateId, bool CreateAttempted, string Owner, DateTimeOffset LeaseUntil);
public sealed record RepairState(string ResourceId, string Path, string? ParentId, string? NextLink, bool Pending,
    DateTimeOffset UpdatedAt, bool Verifying = false, string? ScanCursor = null, long? Generation = null);
public sealed record LifecycleSignal(string Action, DateTimeOffset At, bool Handled = false);
public sealed record NotificationReceipt(string ResourceId, string MessageId, string? ParentId, long Revision,
    string SourceVersion, DateTimeOffset ReceivedAt, DateTimeOffset ProjectedAt);
public sealed class LiveControlStore(IPartitionDocuments documents, TimeProvider clock, string tenant)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string Pk(string id) => $"{tenant}|control|{Hash(id)}";
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
        var value = await documents.ReadAsync(Pk(scope), "subscription", ct);
        return value is null ? null : new(Decode<SubscriptionState>(value.Value), value.Version);
    }
    public Task<bool> SaveSubscriptionAsync(SubscriptionState value, string? version, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("subscription", Pk(value.ScopeId), "subscription", value), version)], ct);
    public async Task<SubscriptionState?> FindSubscriptionAsync(string graphId, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Pk($"subscription:{graphId}"), "lookup", ct);
        if (value is null) return null;
        var scope = Decode<string>(value.Value);
        return (await SubscriptionAsync(scope, ct))?.Value;
    }
    public async Task RegisterLookupAsync(SubscriptionState value, CancellationToken ct)
    {
        if (value.GraphId is null) throw new InvalidOperationException("Subscription ID required.");
        var pk = Pk($"subscription:{value.GraphId}");
        var current = await documents.ReadAsync(pk, "lookup", ct);
        if (current is not null && Decode<string>(current.Value) != value.ScopeId)
            throw new InvalidOperationException("Subscription ID belongs to another scope.");
        if (current is null && !await documents.WriteAsync([new(Wrap("lookup", pk, "lookup", value.ScopeId), null)], ct))
            throw new AppError(409, "subscription_conflict", "Subscription lookup changed concurrently.");
    }
    public async Task EnqueueRepairAsync(ApprovedResource resource, string? path, string? parent, CancellationToken ct)
    {
        var jobPath = path ?? $"{resource.Path}?$top=50" + (resource.Kind == "chat" ? "&$orderby=createdDateTime desc" : "");
        GraphUrls.Validate(jobPath, parent is null ? resource.Path : $"{resource.Path}/{Uri.EscapeDataString(parent)}/replies");
        var pk = Pk($"repair:{resource.Id}:{parent}");
        var current = await documents.ReadAsync(pk, "repair", ct);
        if (current is not null && Decode<RepairState>(current.Value).Pending) return;
        var value = new RepairState(resource.Id, jobPath, parent, null, true, clock.GetUtcNow());
        if (!await documents.WriteAsync([new(Wrap("repair", pk, "repair", value, true), current?.Version)], ct))
            throw new AppError(409, "repair_conflict", "Repair scheduling changed concurrently.");
    }
    public async Task<Versioned<RepairState>?> RepairAsync(string resource, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Pk($"repair:{resource}:{parent}"), "repair", ct);
        return value is null ? null : new(Decode<RepairState>(value.Value), value.Version);
    }
    public Task<bool> SaveRepairAsync(Versioned<RepairState> value, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("repair", Pk($"repair:{value.Value.ResourceId}:{value.Value.ParentId}"),
            "repair", value.Value, value.Value.Pending), value.Version)], ct);
    public async Task<IReadOnlyList<RepairState>> PendingRepairsAsync(CancellationToken ct)
    {
        var page = await documents.QueryAsync(new(tenant, "repair", Pending: true, OldestFirst: true), 50, null, ct);
        return page.Items.Select(d => Decode<RepairState>(d.Value)).ToArray();
    }
    public async Task<bool> HasPendingRepairAsync(string resource, CancellationToken ct) =>
        (await documents.QueryAsync(new(tenant, "repair", ConversationId: resource, Pending: true), 1, null, ct)).Items.Count != 0;
    public async Task<Versioned<LifecycleSignal>?> LifecycleAsync(string scope, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Pk(scope), "lifecycle", ct);
        return value is null ? null : new(Decode<LifecycleSignal>(value.Value), value.Version);
    }
    public Task<bool> SaveLifecycleAsync(string scope, LifecycleSignal signal, string? version, CancellationToken ct) =>
        documents.WriteAsync([new(Wrap("lifecycle", Pk(scope), "lifecycle", signal), version)], ct);
    public async Task<NotificationReceipt?> ReceiptAsync(string resource, string message, string? parent, CancellationToken ct)
    {
        var value = await documents.ReadAsync(Pk($"receipt:{resource}:{parent}:{message}"), "receipt", ct);
        return value is null ? null : Decode<NotificationReceipt>(value.Value);
    }
    public async Task SaveReceiptAsync(NotificationReceipt receipt, CancellationToken ct)
    {
        var pk = Pk($"receipt:{receipt.ResourceId}:{receipt.ParentId}:{receipt.MessageId}");
        var existing = await documents.ReadAsync(pk, "receipt", ct);
        if (existing is not null && Decode<NotificationReceipt>(existing.Value).ReceivedAt > receipt.ReceivedAt) return;
        if (!await documents.WriteAsync([new(Wrap("receipt", pk, "notification-receipt", receipt, ttl: 14 * 86400), existing?.Version)], ct))
            throw new AppError(409, "receipt_conflict", "Notification provenance changed concurrently; retry the original delivery.");
    }
}
