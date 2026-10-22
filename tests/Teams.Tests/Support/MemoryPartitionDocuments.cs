using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class MemoryPartitionDocuments : IPartitionDocuments
{
    private readonly Dictionary<string, Versioned<PartitionDocument>> values = [];
    private readonly object gate = new();
    private long version;
    public Task<Versioned<PartitionDocument>?> ReadCurrentAsync(string pk, string id, CancellationToken ct) =>
        ReadAsync(pk, id, ct);
    public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(values.GetValueOrDefault($"{pk}/{id}"));
    }
    public Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PartitionDocumentBatch.Validate(writes);
        lock (gate)
        {
            if (writes.Any(w => values.GetValueOrDefault($"{w.Value.Pk}/{w.Value.Id}")?.Version != w.ExpectedVersion))
                return Task.FromResult(false);
            foreach (var write in writes)
                values[$"{write.Value.Pk}/{write.Value.Id}"] = new(write.Value, (++version).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(true);
        }
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
            return Task.FromResult(new DocumentPage(items.Take(limit).ToArray(),
                items.Length > limit ? (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }
    }
}
