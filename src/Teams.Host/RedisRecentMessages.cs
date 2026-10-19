using System.Text.Json;
using StackExchange.Redis;
using Teams.Core;

namespace Teams.Host;

public sealed class RedisRecentMessages(IConnectionMultiplexer redis, TimeProvider clock, ILogger<RedisRecentMessages> logger) : IRecentMessageCache
{
    public async Task<IReadOnlyList<Message>?> ReadAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            if (value.IsNull) return null;
            var messages = JsonSerializer.Deserialize<Message[]>((string)value!)
                ?? throw new JsonException("Cached page is invalid.");
            if (messages.Length > 21) throw new JsonException("Cached page exceeds the bound.");
            return messages;
        }
        catch (RedisException ex)
        {
            logger.LogWarning("Recent-view cache unavailable: {ErrorType}; using bounded Cosmos reads.", ex.GetType().Name);
            return null;
        }
        catch (JsonException)
        {
            logger.LogError("Recent-view cache contains invalid data; refusing the page.");
            throw new AppError(503, "cache_invalid", "This view could not be verified. Retry after it expires.");
        }
    }
    public async Task WriteAsync(string key, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(messages);
        if (bytes.Length > 1_000_000)
        {
            logger.LogWarning("Recent-view cache payload budget exceeded; projection remains authoritative.");
            return;
        }
        var lifetime = messages.Count == 0 ? TimeSpan.FromSeconds(15) :
            new[] { TimeSpan.FromSeconds(15), messages.Min(m => m.CreatedAt.AddDays(7)) - clock.GetUtcNow() }.Min();
        if (lifetime <= TimeSpan.Zero) return;
        try { await redis.GetDatabase().StringSetAsync(key, bytes, lifetime); }
        catch (RedisException ex)
        {
            logger.LogWarning("Recent-view cache update failed: {ErrorType}; bounded Cosmos reads remain available.", ex.GetType().Name);
        }
    }
}
