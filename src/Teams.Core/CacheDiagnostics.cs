using System.Diagnostics;

namespace Teams.Core;

public sealed record CacheReadStep(string Layer, string Outcome, int Count, double Milliseconds);
public sealed class ReadEvidence(string kind, DateTimeOffset observedAt)
{
    public string Kind { get; } = kind;
    public DateTimeOffset ObservedAt { get; } = observedAt;
    public List<CacheReadStep> Steps { get; } = [];
    public bool HydrationRequested { get; set; }
    public void Record(string layer, string outcome, int count, long started) =>
        Steps.Add(new(layer, outcome, count, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2)));
}
public sealed record RecentCacheRead(IReadOnlyList<Message>? Messages, string Outcome, string Generation);
public sealed record RedisCacheSnapshot(string Status, int? Pages, double? MaximumTtlSeconds, bool Complete);
public sealed record CacheSnapshot(DateTimeOffset ObservedAt, long Revision, long Generation,
    int? CosmosMessages, bool CosmosCountComplete, bool CoverageComplete, bool ReadViewComplete,
    RedisCacheSnapshot Redis, CacheCleanup? Cleanup, string? CosmosError = null);
public sealed record CacheCleanupCommand(string OperationId, bool Cosmos, bool Redis);
public sealed record CacheCleanup(string Id, bool Cosmos, bool Redis, long TargetRevision, DateTimeOffset StartedAt,
    string CosmosState, string RedisState, int RemovedMessages = 0, int RemovedPages = 0,
    string? CosmosError = null, string? RedisError = null)
{
    public const int RetentionSeconds = 14 * 86400;
    public bool Complete => (CosmosState is "completed" or "not-selected") && (RedisState is "completed" or "not-selected");
}
