using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public sealed class ChannelWorkflowStore(IPartitionDocuments documents, string tenant, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record EventLease(string Owner, DateTimeOffset Until, bool Complete);
    private string Partition(string actor) => $"{tenant}|channel-actor|{Hash(actor)}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Fingerprint(ChannelWork work) =>
        Hash(JsonSerializer.Serialize(new { work.TenantId, work.ActorId, work.Kind, work.TeamId, work.ChannelId, work.Continuation }));
    private PartitionDocument Wrap<T>(string actor, string id, string kind, T value, long revision = 0, bool pending = false) =>
        new(id, Partition(actor), kind, tenant, "", actor, clock.GetUtcNow(), revision, 14 * 86400,
            JsonSerializer.Serialize(value, Json), Pending: pending);
    private static T Decode<T>(PartitionDocument value) => value.SchemaVersion == 1
        ? JsonSerializer.Deserialize<T>(value.Json, Json) ?? throw new AppError(503, "channel_state_invalid", "Channel state is invalid.")
        : throw new AppError(503, "channel_schema_invalid", "Channel state needs a compatible runtime.");
    public async Task<Versioned<ChannelOperation>?> GetAsync(string actor, string operation, CancellationToken ct)
    {
        var value = await documents.ReadCurrentAsync(Partition(actor), $"channel-operation:{operation}", ct);
        return value is null ? null : new(Decode<ChannelOperation>(value.Value), value.Version);
    }
    public async Task<ChannelOperation> CreateAsync(ChannelWork work, CancellationToken ct)
    {
        if (work.TenantId != tenant || !Guid.TryParseExact(work.OperationId, "D", out _) ||
            work.Kind is not ("teams" or "channels" or "open"))
            throw new AppError(400, "channel_request_invalid", "Use a valid channel operation.");
        var previous = await GetAsync(work.ActorId, work.OperationId, ct);
        if (previous is not null)
        {
            if (previous.Value.Fingerprint != Fingerprint(work))
                throw new AppError(409, "channel_operation_conflict", "This operation ID belongs to another request.");
            return previous.Value;
        }
        return await SaveAsync(work.ActorId, null, new(work.OperationId, work.ActorId, Fingerprint(work), work.Kind,
            "pending", 0, clock.GetUtcNow(), TeamId: work.TeamId, Request: work), ct);
    }
    public async Task<ChannelOperation> SaveAsync(string actor, Versioned<ChannelOperation>? previous,
        ChannelOperation value, CancellationToken ct)
    {
        if (value.ActorId != actor || previous is not null &&
            (previous.Value.Id != value.Id || previous.Value.Fingerprint != value.Fingerprint || previous.Value.Request != value.Request))
            throw new AppError(409, "channel_operation_conflict", "Channel work cannot change its accepted identity.");
        var next = value with { Revision = (previous?.Value.Revision ?? 0) + 1, UpdatedAt = clock.GetUtcNow() };
        await CommitAsync(actor, previous, next,
            new("channel.operation.changed", next.Revision, OperationId: next.Id, ConversationId: next.ConversationId), ct);
        return next;
    }
    public Task EmitAsync(string actor, RealtimeEvent value, CancellationToken ct) =>
        CommitAsync(actor, null, null, value, ct);
    private async Task CommitAsync(string actor, Versioned<ChannelOperation>? previous, ChannelOperation? operation,
        RealtimeEvent value, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var head = await documents.ReadCurrentAsync(Partition(actor), "channel-event-head", ct);
            var sequence = (head is null ? 0 : Decode<long>(head.Value)) + 1;
            var item = value with { Sequence = sequence };
            var id = $"actor-outbox:{sequence:D20}";
            var work = new ActorEventWork(id, tenant, actor, item, clock.GetUtcNow());
            List<DocumentWrite> writes = [
                new(Wrap(actor, "channel-event-head", "channel-event-head", sequence), head?.Version),
                new(Wrap(actor, $"channel-event:{sequence:D20}", "channel-event", item, sequence), null),
                new(Wrap(actor, id, "actor-outbox", work, sequence, true), null)
            ];
            if (operation is not null)
                writes.Add(new(Wrap(actor, $"channel-operation:{operation.Id}", "channel-operation", operation, operation.Revision,
                    operation.State is "pending" or "deferred" or "preparing"), previous?.Version));
            if (await documents.WriteAsync(writes, ct)) return;
            if (operation is not null && (await GetAsync(actor, operation.Id, ct))?.Version != previous?.Version)
                throw new AppError(409, "channel_operation_conflict", "Channel work changed concurrently. Recover the original operation.");
        }
        throw new AppError(409, "channel_event_conflict", "Channel progress is changing. Retry the original operation.");
    }
    public async Task RecoverAsync(string actor, IChannelWorkQueue queue, CancellationToken ct)
    {
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var pending = await documents.QueryAsync(new(tenant, "channel-operation", Partition(actor), actor, Pending: true),
                50, cursor, ct);
            foreach (var item in pending.Items)
            {
                var operation = Decode<ChannelOperation>(item.Value);
                if (operation.LeaseUntil > clock.GetUtcNow()) continue;
                var request = operation.Request ?? throw new AppError(503, "channel_state_invalid", "Accepted channel work is missing.");
                if (request.ActorId != actor || request.TenantId != tenant || request.OperationId != operation.Id ||
                    Fingerprint(request) != operation.Fingerprint)
                    throw new AppError(503, "channel_state_invalid", "Accepted channel work cannot be recovered safely.");
                await queue.EnqueueAsync(request, clock.GetUtcNow(), ct);
            }
            cursor = pending.Cursor;
            if (cursor is null) return;
        }
        throw new AppError(503, "channel_recovery_budget", "Channel recovery exceeded its metadata page budget.");
    }
    public async Task<ActorEventPage> ChangesAsync(string actor, long after, CancellationToken ct)
    {
        if (after < 0) throw new AppError(400, "invalid_revision", "Event sequence cannot be negative.");
        var head = await documents.ReadCurrentAsync(Partition(actor), "channel-event-head", ct);
        var sequence = head is null ? 0 : Decode<long>(head.Value);
        if (after > sequence) return new([], sequence, true, false);
        List<RealtimeEvent> events = [];
        for (var next = after + 1; next <= sequence && events.Count < 50; next++)
        {
            var item = await documents.ReadCurrentAsync(Partition(actor), $"channel-event:{next:D20}", ct);
            if (item is null) return new([], sequence, true, false);
            var value = Decode<RealtimeEvent>(item.Value);
            if (value.Sequence != next)
                throw new AppError(503, "channel_state_invalid", "Channel journal sequence does not match its committed identity.");
            events.Add(value);
        }
        return new(events, sequence, false, after + events.Count < sequence);
    }
    public async Task DeliverAsync(ActorEventWork work, IRealtime realtime, CancellationToken ct)
    {
        if (work.TenantId != tenant || work.Event.SchemaVersion != 1 || work.Event.Sequence < 1 ||
            work.CreatedAt > clock.GetUtcNow().AddMinutes(1) || work.CreatedAt < clock.GetUtcNow().AddDays(-14))
            throw new AppError(400, "channel_event_invalid", "The channel event identity or age is invalid.");
        var pk = Partition(work.ActorId);
        var document = await documents.ReadCurrentAsync(pk, work.Id, ct);
        if (document is null || !document.Value.Pending) return;
        if (Decode<ActorEventWork>(document.Value) != work)
            throw new AppError(400, "channel_event_invalid", "The delivery does not match its committed event.");
        var leaseId = $"actor-delivery:{work.Event.Sequence:D20}";
        var previous = await documents.ReadCurrentAsync(pk, leaseId, ct);
        var lease = previous is null ? null : Decode<EventLease>(previous.Value);
        if (lease?.Complete == true) return;
        if (lease?.Until > clock.GetUtcNow()) throw new DeliveryDeferredException(lease.Until);
        var owner = Guid.NewGuid().ToString("N");
        var claim = Wrap(work.ActorId, leaseId, "actor-delivery", new EventLease(owner, clock.GetUtcNow().AddMinutes(2), false));
        if (!await documents.WriteAsync([new(claim, previous?.Version)], ct))
            throw new DeliveryDeferredException(clock.GetUtcNow().AddSeconds(1));
        var claimed = await documents.ReadCurrentAsync(pk, leaseId, ct)
            ?? throw new AppError(503, "channel_event_unavailable", "Event delivery ownership could not be verified.");
        try
        {
            await realtime.PublishEventAsync(tenant, work.ActorId, work.Event, ct);
            if (!await documents.WriteAsync([
                new(claim with { Json = JsonSerializer.Serialize(new EventLease("", DateTimeOffset.MinValue, true), Json) }, claimed.Version),
                new(document.Value with { Pending = false }, document.Version)
            ], ct)) throw new AppError(409, "channel_event_conflict", "Event completion changed concurrently.");
        }
        catch
        {
            await documents.WriteAsync([new(claim with
                { Json = JsonSerializer.Serialize(new EventLease("", DateTimeOffset.MinValue, false), Json) }, claimed.Version)], CancellationToken.None);
            throw;
        }
    }
}
