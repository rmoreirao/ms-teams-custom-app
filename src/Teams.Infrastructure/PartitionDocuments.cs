using System.Net;
using Microsoft.Azure.Cosmos;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed record PartitionDocument(string Id, string Pk, string Kind, string TenantId, string ConversationId,
    string ActorId, DateTimeOffset At, long Revision, int Ttl, string Json, bool Pending = false, string Parent = "",
    string SortTime = "", string SourceId = "", int SchemaVersion = 1)
{
    [Newtonsoft.Json.JsonProperty("_etag", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? Etag { get; init; }
}
public sealed record DocumentPage(IReadOnlyList<Versioned<PartitionDocument>> Items, string? Cursor);
public sealed record DocumentFilter(string TenantId, string Kind, string? Pk = null, string? ActorId = null,
    string? ConversationId = null, DateTimeOffset? Since = null, bool? Pending = null, string? Parent = null,
    DateTimeOffset? Before = null, string? BeforeId = null, long? AfterRevision = null, bool OldestFirst = false,
    long? ThroughRevision = null);
public sealed record DocumentWrite(PartitionDocument Value, string? ExpectedVersion);
public sealed record DocumentDelete(string Pk, string Id, string ExpectedVersion);
public interface IPartitionDocuments
{
    Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct);
    Task<Versioned<PartitionDocument>?> ReadCurrentAsync(string pk, string id, CancellationToken ct);
    Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct);
    Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct);
    Task<bool> MutateAsync(IReadOnlyList<DocumentWrite> writes, IReadOnlyList<DocumentDelete> deletes, CancellationToken ct) =>
        throw new NotSupportedException("Conditional deletion is not supported by this document adapter.");
}

public static class PartitionDocumentBatch
{
    public static void Validate(IReadOnlyList<DocumentWrite> writes, IReadOnlyList<DocumentDelete>? deletes = null)
    {
        deletes ??= [];
        var count = writes.Count + deletes.Count;
        if (writes.Count < 1 || count > 100 ||
            writes.Select(w => w.Value.Pk).Concat(deletes.Select(d => d.Pk)).Distinct().Count() != 1 ||
            writes.Select(w => w.Value.Id).Concat(deletes.Select(d => d.Id)).Distinct().Count() != count ||
            deletes.Any(d => string.IsNullOrWhiteSpace(d.ExpectedVersion)))
            throw new InvalidOperationException("A batch must have 1-100 unique items in one logical partition.");
    }
}

public sealed record PrimaryReadResult(int Version, bool Found, PartitionDocument? Document, string? Etag)
{
    public Versioned<PartitionDocument>? Validate(string pk, string id, string? sessionToken)
    {
        if (Version != 1 || !Found && (Document is not null || Etag is not null))
            throw new AppError(503, "primary_read_invalid", "The authoritative read returned invalid identity, schema or session evidence.");
        if (!Found) return null;
        if (Document is not { } document || document.Id != id || document.Pk != pk ||
            Etag is not { Length: > 0 } etag || string.IsNullOrWhiteSpace(etag) || string.IsNullOrWhiteSpace(sessionToken))
            throw new AppError(503, "primary_read_invalid", "The authoritative read returned invalid identity, schema or session evidence.");
        return new(document, etag);
    }
}

public sealed class CosmosPartitionDocuments(Container container) : IPartitionDocuments
{
    public async Task<Versioned<PartitionDocument>?> ReadCurrentAsync(string pk, string id, CancellationToken ct)
    {
        try
        {
            // Critical reads also advance this SDK client's partition session barrier.
            var response = await container.Scripts.ExecuteStoredProcedureAsync<PrimaryReadResult>(
                "readCurrentV1", new PartitionKey(pk), new object[] { id, pk }, cancellationToken: ct);
            var result = response.Resource ?? throw new AppError(503, "primary_read_invalid", "The authoritative read returned no evidence.");
            return result.Validate(pk, id, response.Headers.Session);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw new AppError(503, "primary_read_unavailable",
                "A critical current read requires readCurrentV1 and its execute permission. Provision the Session storage contract.");
        }
    }
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
        => await MutateAsync(writes, [], ct);
    public async Task<bool> MutateAsync(IReadOnlyList<DocumentWrite> writes, IReadOnlyList<DocumentDelete> deletes, CancellationToken ct)
    {
        PartitionDocumentBatch.Validate(writes, deletes);
        var batch = container.CreateTransactionalBatch(new(writes[0].Value.Pk));
        foreach (var write in writes)
        {
            if (write.ExpectedVersion is null) batch.CreateItem(write.Value);
            else batch.ReplaceItem(write.Value.Id, write.Value, new() { IfMatchEtag = write.ExpectedVersion });
        }
        foreach (var delete in deletes)
            batch.DeleteItem(delete.Id, new() { IfMatchEtag = delete.ExpectedVersion });
        using var response = await batch.ExecuteAsync(ct);
        if (response.IsSuccessStatusCode) return true;
        if (Enumerable.Range(0, response.Count).Any(i => response[i].StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound))
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
        if (f.ThroughRevision is not null)
        {
            clauses.Add("c.revision <= @throughRevision");
            parameters["@throughRevision"] = f.ThroughRevision.Value;
        }
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
                return new(response.Select(d => new Versioned<PartitionDocument>(d, d.Etag ?? "")).ToArray(), response.ContinuationToken);
        }
        throw new AppError(503, "projection_query_budget", "The bounded query could not locate a usable page.");
    }
}
