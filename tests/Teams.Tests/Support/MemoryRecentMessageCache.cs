using Teams.Core;

namespace Teams.Tests;

public sealed class MemoryRecentMessageCache : IRecentMessageCache
{
    private readonly Dictionary<(string Scope, string Generation, string Page), IReadOnlyList<Message>> pages = [];
    private readonly Dictionary<string, string> generations = [];
    private readonly Dictionary<(string Scope, string Operation), int> evictions = [];
    public bool FailEvictions { get; set; }
    public bool Unavailable { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public Task<RecentCacheRead> ReadAsync(string scope, string page, CancellationToken ct)
    {
        Reads++;
        var generation = generations.GetValueOrDefault(scope, "0");
        var rows = Unavailable ? null : pages.GetValueOrDefault((scope, generation, page));
        return Task.FromResult(new RecentCacheRead(rows, Unavailable ? "unavailable" : rows is null ? "miss" : "hit", generation));
    }
    public Task<string> WriteAsync(string scope, string page, string generation, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        Writes++;
        if (Unavailable) return Task.FromResult("unavailable");
        if (generations.GetValueOrDefault(scope, "0") != generation) return Task.FromResult("superseded");
        pages[(scope, generation, page)] = messages.ToArray();
        return Task.FromResult("stored");
    }
    public Task<RedisCacheSnapshot> InspectAsync(string scope, CancellationToken ct) =>
        Task.FromResult(Unavailable ? new RedisCacheSnapshot("unavailable", null, null, false) :
            new RedisCacheSnapshot("available", pages.Keys.Count(k => k.Scope == scope), 15, true));
    public Task<int> EvictAsync(string scope, string operationId, CancellationToken ct)
    {
        if (FailEvictions || Unavailable) throw new AppError(503, "cache_eviction_unavailable", "Test Redis eviction unavailable.");
        if (evictions.TryGetValue((scope, operationId), out var previous)) return Task.FromResult(previous);
        generations[scope] = operationId;
        var keys = pages.Keys.Where(k => k.Scope == scope).ToArray();
        foreach (var key in keys) pages.Remove(key);
        evictions[(scope, operationId)] = keys.Length;
        return Task.FromResult(keys.Length);
    }
}
