using System.Net;
using Microsoft.Azure.Cosmos;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed record PartitionDocument(string Id, string Pk, string Kind, string TenantId, string ConversationId,
    string ActorId, DateTimeOffset At, long Revision, int Ttl, string Json, bool Pending = false, string Parent = "",
    string SortTime = "", string SourceId = "", int SchemaVersion = 1);
public sealed record DocumentPage(IReadOnlyList<Versioned<PartitionDocument>> Items, string? Cursor);
public sealed record DocumentFilter(string TenantId, string Kind, string? Pk = null, string? ActorId = null,
    string? ConversationId = null, DateTimeOffset? Since = null, bool? Pending = null, string? Parent = null,
    DateTimeOffset? Before = null, string? BeforeId = null, long? AfterRevision = null, bool OldestFirst = false);
public sealed record DocumentWrite(PartitionDocument Value, string? ExpectedVersion);
public interface IPartitionDocuments
{
    Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct);
    Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct);
    Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct);
}

public sealed class MemoryPartitionDocuments : IPartitionDocuments
{
    private readonly Dictionary<string, Versioned<PartitionDocument>> values = [];
    private readonly object gate = new();
    private long version;
    public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(values.GetValueOrDefault($"{pk}/{id}"));
    }
    public Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Validate(writes);
        lock (gate)
        {
            if (writes.Any(w => values.GetValueOrDefault($"{w.Value.Pk}/{w.Value.Id}")?.Version != w.ExpectedVersion))
                return Task.FromResult(false);
            foreach (var write in writes)
                values[$"{write.Value.Pk}/{write.Value.Id}"] = new(write.Value, (++version).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(true);
        }
    }
    internal static void Validate(IReadOnlyList<DocumentWrite> writes)
    {
        if (writes.Count is < 1 or > 100 || writes.Select(w => w.Value.Pk).Distinct().Count() != 1 ||
            writes.Select(w => w.Value.Id).Distinct().Count() != writes.Count)
            throw new InvalidOperationException("A batch must have 1-100 unique items in one logical partition.");
    }
    public Task<DocumentPage> QueryAsync(DocumentFilter f, int limit, string? cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            var query = values.Values.Where(v => v.Value.TenantId == f.TenantId && v.Value.Kind == f.Kind &&
                (f.Pk is null || v.Value.Pk == f.Pk) && (f.ActorId is null || v.Value.ActorId == f.ActorId) &&
                (f.ConversationId is null || v.Value.ConversationId == f.ConversationId) &&
                (f.Since is null || v.Value.At >= f.Since) && (f.Pending is null || v.Value.Pending == f.Pending) &&
                (f.Parent is null || v.Value.Parent == f.Parent) &&
                (f.AfterRevision is null || v.Value.Revision > f.AfterRevision) &&
                (f.Before is null || v.Value.At < f.Before || v.Value.At == f.Before &&
                    string.CompareOrdinal(v.Value.SourceId, f.BeforeId) > 0));
            var ordered = f.OldestFirst
                ? query.OrderBy(v => v.Value.At).ThenBy(v => v.Value.Id, StringComparer.Ordinal)
                : f.Kind == "message"
                ? query.OrderByDescending(v => v.Value.At).ThenBy(v => v.Value.SourceId, StringComparer.Ordinal)
                : query.OrderBy(v => v.Value.Id, StringComparer.Ordinal);
            var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var items = ordered.Skip(offset).Take(limit + 1).ToArray();
            return Task.FromResult(new DocumentPage(items.Take(limit).ToArray(), items.Length > limit ? (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }
    }
}

public sealed class CosmosPartitionDocuments(Container container) : IPartitionDocuments
{
    public async Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct)
    {
        try
        {
            var response = await container.ReadItemAsync<PartitionDocument>(id, new PartitionKey(pk), cancellationToken: ct);
            return new(response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
    {
        MemoryPartitionDocuments.Validate(writes);
        var batch = container.CreateTransactionalBatch(new(writes[0].Value.Pk));
        foreach (var write in writes)
        {
            if (write.ExpectedVersion is null) batch.CreateItem(write.Value);
            else batch.ReplaceItem(write.Value.Id, write.Value, new() { IfMatchEtag = write.ExpectedVersion });
        }
        using var response = await batch.ExecuteAsync(ct);
        if (response.IsSuccessStatusCode) return true;
        if (Enumerable.Range(0, response.Count).Any(i => response[i].StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
            return false;
        throw new AppError(503, "projection_commit_unavailable", $"Projection transaction failed ({(int)response.StatusCode}).");
    }
    public async Task<DocumentPage> QueryAsync(DocumentFilter f, int limit, string? cursor, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || cursor?.Length > 16384) throw new ArgumentOutOfRangeException(nameof(limit));
        var clauses = new List<string> { "c.tenantId = @tenant", "c.kind = @kind" };
        var parameters = new Dictionary<string, object> { ["@tenant"] = f.TenantId, ["@kind"] = f.Kind };
        void Add(string property, object? value, string comparison = "=")
        {
            if (value is null) return;
            clauses.Add($"c.{property} {comparison} @{property}");
            parameters[$"@{property}"] = value;
        }
        Add("actorId", f.ActorId); Add("conversationId", f.ConversationId);
        Add("sortTime", f.Since?.ToUniversalTime().ToString("O"), ">="); Add("pending", f.Pending); Add("parent", f.Parent);
        Add("revision", f.AfterRevision, ">");
        if (f.Before is not null)
        {
            clauses.Add("(c.sortTime < @before OR (c.sortTime = @before AND c.sourceId > @beforeId))");
            parameters["@before"] = f.Before.Value.ToUniversalTime().ToString("O");
            parameters["@beforeId"] = f.BeforeId ?? "";
        }
        var order = f.OldestFirst ? "c.sortTime ASC, c.id ASC" :
            f.Kind == "message" ? "c.sortTime DESC, c.sourceId ASC" : "c.id";
        var query = new QueryDefinition($"SELECT * FROM c WHERE {string.Join(" AND ", clauses)} ORDER BY {order}");
        foreach (var parameter in parameters) query.WithParameter(parameter.Key, parameter.Value);
        using var iterator = container.GetItemQueryIterator<PartitionDocument>(query, cursor,
            new QueryRequestOptions { PartitionKey = f.Pk is null ? null : new(f.Pk), MaxItemCount = limit,
                ResponseContinuationTokenLimitInKb = 2 });
        for (var page = 0; page < 10; page++)
        {
            var response = await iterator.ReadNextAsync(ct);
            if (response.Count != 0 || !iterator.HasMoreResults)
                return new(response.Select(d => new Versioned<PartitionDocument>(d, "")).ToArray(), response.ContinuationToken);
        }
        throw new AppError(503, "projection_query_budget", "The bounded query could not locate a usable page.");
    }
}
