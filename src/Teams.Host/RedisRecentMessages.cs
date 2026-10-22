using System.Text.Json;
using StackExchange.Redis;
using Teams.Core;

namespace Teams.Host;

public sealed class RedisRecentMessages(IConnectionMultiplexer redis, TimeProvider clock, ILogger<RedisRecentMessages> logger) : IRecentMessageCache
{
    internal const string ReadScript = """
        local generation = redis.call('GET', KEYS[1]) or '0'
        return {generation, redis.call('GET', ARGV[1] .. ':page:' .. generation .. ':' .. ARGV[2])}
        """;
    internal const string WriteScript = """
        if (redis.call('GET', KEYS[1]) or '0') ~= ARGV[1] then return 'superseded' end
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        if redis.call('ZCARD', KEYS[2]) >= 256 and not redis.call('ZSCORE', KEYS[2], KEYS[3]) then return 'capacity' end
        redis.call('PSETEX', KEYS[3], ARGV[2], ARGV[3])
        redis.call('ZADD', KEYS[2], now + ARGV[2], KEYS[3])
        redis.call('PEXPIRE', KEYS[2], 30000)
        return 'stored'
        """;
    internal const string InspectScript = """
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local keys = redis.call('ZRANGEBYSCORE', KEYS[1], now, '+inf', 'LIMIT', 0, 257)
        local count = 0
        local maximum = 0
        for i = 1, math.min(#keys, 256) do
          local ttl = redis.call('PTTL', keys[i])
          if ttl > 0 then count = count + 1; maximum = math.max(maximum, ttl) end
        end
        return {count, maximum, #keys <= 256 and 1 or 0}
        """;
    internal const string EvictScript = """
        local previous = redis.call('GET', KEYS[3])
        if previous then return tonumber(previous) end
        local keys = redis.call('ZRANGE', KEYS[2], 0, 256)
        if #keys > 256 then return redis.error_reply('Recent-page index exceeds its bound') end
        redis.call('SET', KEYS[1], ARGV[1])
        local count = 0
        for _, key in ipairs(keys) do count = count + redis.call('DEL', key) end
        redis.call('DEL', KEYS[2])
        redis.call('SET', KEYS[3], count, 'EX', ARGV[2])
        return count
        """;
    public async Task<RecentCacheRead> ReadAsync(string scope, string page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var values = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(ReadScript,
                [(RedisKey)$"{scope}:generation"], [(RedisValue)scope, page]).WaitAsync(ct)
                ?? throw new JsonException("Cached page response is invalid.");
            var generation = (string?)values[0] ?? "0";
            var value = (string?)values[1];
            if (value is null) return new(null, "miss", generation);
            var messages = JsonSerializer.Deserialize<Message[]>(value)
                ?? throw new JsonException("Cached page is invalid.");
            if (messages.Length > 21) throw new JsonException("Cached page exceeds the bound.");
            return new(messages, "hit", generation);
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Recent-view cache unavailable: {ErrorType}; using bounded Cosmos reads.", ex.GetType().Name);
            return new(null, "unavailable", "0");
        }
        catch (JsonException)
        {
            logger.LogError("Recent-view cache contains invalid data; refusing the page.");
            throw new AppError(503, "cache_invalid", "This view could not be verified. Retry after it expires.");
        }
    }
    public async Task<string> WriteAsync(string scope, string page, string generation, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(messages);
        if (bytes.Length > 1_000_000)
        {
            logger.LogWarning("Recent-view cache payload budget exceeded; projection remains authoritative.");
            return "too-large";
        }
        var lifetime = messages.Count == 0 ? TimeSpan.FromSeconds(15) :
            new[] { TimeSpan.FromSeconds(15), messages.Min(m => m.CreatedAt.AddDays(7)) - clock.GetUtcNow() }.Min();
        if (lifetime <= TimeSpan.Zero) return "expired";
        try
        {
            var result = await redis.GetDatabase().ScriptEvaluateAsync(WriteScript,
                [(RedisKey)$"{scope}:generation", $"{scope}:index", $"{scope}:page:{generation}:{page}"],
                [(RedisValue)generation, Math.Max(1, (long)lifetime.TotalMilliseconds), bytes]).WaitAsync(ct);
            return (string?)result ?? throw new AppError(503, "cache_invalid", "The cache write returned no outcome.");
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Recent-view cache update failed: {ErrorType}; bounded Cosmos reads remain available.", ex.GetType().Name);
            return "unavailable";
        }
    }
    public async Task<RedisCacheSnapshot> InspectAsync(string scope, CancellationToken ct)
    {
        try
        {
            var result = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(InspectScript,
                [(RedisKey)$"{scope}:index"]).WaitAsync(ct)
                ?? throw new AppError(503, "cache_invalid", "The cache inspection returned no outcome.");
            return new("available", (int)result[0], (double)(long)result[1] / 1000, (int)result[2] == 1);
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Recent-view cache inspection failed: {ErrorType}.", ex.GetType().Name);
            return new("unavailable", null, null, false);
        }
    }
    public async Task<int> EvictAsync(string scope, string operationId, CancellationToken ct)
    {
        try
        {
            return (int)await redis.GetDatabase().ScriptEvaluateAsync(EvictScript,
                [(RedisKey)$"{scope}:generation", $"{scope}:index", $"{scope}:eviction:{operationId}"],
                [(RedisValue)operationId, CacheCleanup.RetentionSeconds]).WaitAsync(ct);
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Recent-view cache eviction failed: {ErrorType}; no eviction success is claimed.", ex.GetType().Name);
            throw new AppError(503, "cache_eviction_unavailable", "Redis cleanup could not be confirmed. Check or resume the original operation.");
        }
    }
}
