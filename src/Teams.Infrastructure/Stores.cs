using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class MemoryStateStore : IStateStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, (object Value, long Version)> items = [];
    private readonly Dictionary<string, WriteResult> receipts = [];
    private readonly HashSet<string> blocked = [];
    private static string Key(string tenant, string conversation, string id) => $"{tenant}|{conversation}|{id}";
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private Versioned<T>? Read<T>(string key) => items.TryGetValue(key, out var item)
        ? new(Clone((T)item.Value), item.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
    private bool Write<T>(string key, T value, string? expected)
    {
        var previous = items.GetValueOrDefault(key);
        if (expected is null ? previous.Value is not null : previous.Version.ToString() != expected)
            return false;
        items[key] = (Clone(value)!, previous.Version + 1);
        return true;
    }
    public Task<IReadOnlyList<Conversation>> ListAsync(string tenant, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult<IReadOnlyList<Conversation>>(items.Values.Select(x => x.Value).OfType<Conversation>()
                .Where(c => c.TenantId == tenant).Select(Clone).ToArray());
    }
    public Task<Versioned<Conversation>?> GetAsync(string tenant, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(Read<Conversation>(Key(tenant, id, "conversation")));
    }
    public Task<bool> CreateAsync(Conversation value, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Write(Key(value.TenantId, value.Id, "conversation"), value, null));
    }
    public Task<bool> SaveAsync(Versioned<Conversation> value, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Write(Key(value.Value.TenantId, value.Value.Id, "conversation"), value.Value, value.Version));
    }
    public Task<Versioned<SendOperation>?> GetOperationAsync(string tenant, string conversation, string id, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Read<SendOperation>(Key(tenant, conversation, id)));
    }
    public Task<bool> CreateOperationAsync(SendOperation value, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Write(Key(value.TenantId, value.ConversationId, value.Id), value, null));
    }
    public Task<bool> SaveOperationAsync(Versioned<SendOperation> value, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(Write(Key(value.Value.TenantId, value.Value.ConversationId, value.Value.Id), value.Value, value.Version));
    }
    public Task<bool> CommitAsync(Versioned<Conversation> conversation, Versioned<SendOperation>? operation, CancellationToken ct)
    {
        lock (gate)
        {
            var ckey = Key(conversation.Value.TenantId, conversation.Value.Id, "conversation");
            if (Read<Conversation>(ckey)?.Version != conversation.Version) return Task.FromResult(false);
            if (operation is not null)
            {
                var okey = Key(operation.Value.TenantId, operation.Value.ConversationId, operation.Value.Id);
                if (Read<SendOperation>(okey)?.Version != operation.Version) return Task.FromResult(false);
                Write(okey, operation.Value, operation.Version);
            }
            Write(ckey, conversation.Value, conversation.Version);
            return Task.FromResult(true);
        }
    }
    public Task<WriteResult?> GetSimulatorReceiptAsync(string tenant, string conversation, string operation, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(receipts.GetValueOrDefault(Key(tenant, conversation, operation)));
    }
    public Task SaveSimulatorReceiptAsync(string tenant, string conversation, string operation, WriteResult result, CancellationToken ct)
    {
        lock (gate) receipts[Key(tenant, conversation, operation)] = result;
        return Task.CompletedTask;
    }
    public Task<bool> IsBlockedAsync(string tenant, string actor, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(blocked.Contains($"{tenant}|{actor}"));
    }
    public Task SetBlockedAsync(string tenant, string actor, bool value, CancellationToken ct)
    {
        lock (gate)
        {
            if (value) blocked.Add($"{tenant}|{actor}");
            else blocked.Remove($"{tenant}|{actor}");
        }
        return Task.CompletedTask;
    }
}

public sealed class CosmosStateStore(Container container) : IStateStore
{
    private sealed record Document<T>(string Id, string Pk, string Kind, string TenantId, int Ttl, T Value);
    private static string Partition(string tenant, string conversation) => $"{tenant}|{conversation}";
    private static Document<Conversation> Wrap(Conversation c) => new("conversation", Partition(c.TenantId, c.Id), "conversation", c.TenantId, -1, c);
    private static Document<SendOperation> Wrap(SendOperation op) => new($"op:{op.Id}", Partition(op.TenantId, op.ConversationId), "operation", op.TenantId, 14 * 86400, op);
    public async Task<IReadOnlyList<Conversation>> ListAsync(string tenant, CancellationToken ct)
    {
        using var iterator = container.GetItemQueryIterator<Document<Conversation>>(
            new QueryDefinition("SELECT TOP 101 * FROM c WHERE c.kind = 'conversation' AND c.tenantId = @tenant")
                .WithParameter("@tenant", tenant), requestOptions: new QueryRequestOptions { MaxItemCount = 101 });
        List<Conversation> values = [];
        while (iterator.HasMoreResults) values.AddRange((await iterator.ReadNextAsync(ct)).Select(d => d.Value));
        if (values.Count > 100)
            throw new AppError(503, "dev_catalog_limit", "This dev deployment supports at most 100 fixture conversations.");
        return values;
    }
    private async Task<Versioned<T>?> Read<T>(string id, string pk, CancellationToken ct)
    {
        try
        {
            var item = await container.ReadItemAsync<Document<T>>(id, new PartitionKey(pk), cancellationToken: ct);
            return new(item.Resource.Value, item.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public Task<Versioned<Conversation>?> GetAsync(string tenant, string id, CancellationToken ct) =>
        Read<Conversation>("conversation", Partition(tenant, id), ct);
    public Task<Versioned<SendOperation>?> GetOperationAsync(string tenant, string conversation, string id, CancellationToken ct) =>
        Read<SendOperation>($"op:{id}", Partition(tenant, conversation), ct);
    private async Task<bool> Create<T>(Document<T> document, CancellationToken ct)
    {
        try
        {
            await container.CreateItemAsync(document, new PartitionKey(document.Pk), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict) { return false; }
    }
    private async Task<bool> Replace<T>(Document<T> document, string version, CancellationToken ct)
    {
        try
        {
            await container.ReplaceItemAsync(document, document.Id, new PartitionKey(document.Pk),
                new ItemRequestOptions { IfMatchEtag = version }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed) { return false; }
    }
    public Task<bool> CreateAsync(Conversation value, CancellationToken ct) => Create(Wrap(value), ct);
    public Task<bool> SaveAsync(Versioned<Conversation> value, CancellationToken ct) => Replace(Wrap(value.Value), value.Version, ct);
    public Task<bool> CreateOperationAsync(SendOperation value, CancellationToken ct) => Create(Wrap(value), ct);
    public Task<bool> SaveOperationAsync(Versioned<SendOperation> value, CancellationToken ct) => Replace(Wrap(value.Value), value.Version, ct);
    public async Task<bool> CommitAsync(Versioned<Conversation> conversation, Versioned<SendOperation>? operation, CancellationToken ct)
    {
        var document = Wrap(conversation.Value);
        var batch = container.CreateTransactionalBatch(new PartitionKey(document.Pk)).ReplaceItem(document.Id, document,
            new TransactionalBatchItemRequestOptions { IfMatchEtag = conversation.Version });
        if (operation is not null)
        {
            if (Partition(operation.Value.TenantId, operation.Value.ConversationId) != document.Pk)
                throw new InvalidOperationException("Confirmation transaction crossed a partition.");
            var op = Wrap(operation.Value);
            batch.ReplaceItem(op.Id, op, new TransactionalBatchItemRequestOptions { IfMatchEtag = operation.Version });
        }
        using var result = await batch.ExecuteAsync(ct);
        if (result.IsSuccessStatusCode) return true;
        if (result.StatusCode == HttpStatusCode.PreconditionFailed ||
            Enumerable.Range(0, result.Count).Any(i => result[i].StatusCode == HttpStatusCode.PreconditionFailed)) return false;
        throw new AppError(503, "state_commit_unavailable", $"State transaction failed ({(int)result.StatusCode}).");
    }
    public async Task<WriteResult?> GetSimulatorReceiptAsync(string tenant, string conversation, string operation, CancellationToken ct) =>
        (await Read<WriteResult>($"sim-receipt:{operation}", Partition(tenant, conversation), ct))?.Value;
    public async Task SaveSimulatorReceiptAsync(string tenant, string conversation, string operation, WriteResult result, CancellationToken ct)
    {
        var document = new Document<WriteResult>($"sim-receipt:{operation}", Partition(tenant, conversation), "simulator-receipt", tenant, 7 * 86400, result);
        if (!await Create(document, ct))
            throw new InvalidOperationException("A simulator receipt already exists for this operation.");
    }
    public async Task<bool> IsBlockedAsync(string tenant, string actor, CancellationToken ct) =>
        (await Read<bool>("revocation", Partition(tenant, $"principal:{actor}"), ct))?.Value ?? false;
    public async Task SetBlockedAsync(string tenant, string actor, bool blocked, CancellationToken ct)
    {
        var pk = Partition(tenant, $"principal:{actor}");
        await container.UpsertItemAsync(new Document<bool>("revocation", pk, "revocation", tenant, -1, blocked), new PartitionKey(pk), cancellationToken: ct);
    }
}
