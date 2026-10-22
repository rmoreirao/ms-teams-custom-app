using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class RedisRecentMessagesTests
{
    private const string Scope = "recent:v2:{test-chat-hash}";
    private static RedisResult Number(long number) => RedisResult.Create((RedisValue)number);
    private static RedisResult Text(string? value) => RedisResult.Create(value is null ? RedisValue.Null : (RedisValue)value);
    private static RedisRecentMessages Cache(Func<string, RedisKey[], RedisValue[], Task<RedisResult>> script, TimeProvider? clock = null)
    {
        var database = DispatchProxy.Create<IDatabase, RedisProxy>();
        ((RedisProxy)(object)database).Call = (method, args) => method.Name == nameof(IDatabase.ScriptEvaluateAsync)
            ? script((string)args![0]!, (RedisKey[])args[1]!, args[2] as RedisValue[] ?? [])
            : throw new InvalidOperationException($"Unexpected Redis command: {method.Name}");
        var connection = DispatchProxy.Create<IConnectionMultiplexer, RedisProxy>();
        ((RedisProxy)(object)connection).Call = (method, _) => method.Name == nameof(IConnectionMultiplexer.GetDatabase)
            ? database : throw new InvalidOperationException($"Unexpected Redis connection operation: {method.Name}");
        return new(connection, clock ?? TimeProvider.System, NullLogger<RedisRecentMessages>.Instance);
    }
    [Fact]
    public async Task ReadsDistinguishAMissFromAValidEmptyHitAndRejectInvalidContent()
    {
        string? content = null;
        var cache = Cache((script, keys, values) =>
        {
            Assert.Single(keys); Assert.Equal($"{Scope}:generation", keys[0].ToString());
            Assert.Equal(Scope, values[0].ToString()); Assert.Equal("page", values[1].ToString());
            Assert.DoesNotContain("SET", script);
            return Task.FromResult(RedisResult.Create(new[] { Text("generation"), Text(content) }));
        });
        Assert.Equal("miss", (await cache.ReadAsync(Scope, "page", default)).Outcome);
        content = "[]";
        var empty = await cache.ReadAsync(Scope, "page", default);
        Assert.Equal("hit", empty.Outcome); Assert.Empty(empty.Messages!);
        Assert.Equal("generation", empty.Generation);
        content = "invalid JSON";
        Assert.Equal("cache_invalid", (await Assert.ThrowsAsync<AppError>(() => cache.ReadAsync(Scope, "page", default))).Code);
    }
    [Fact]
    public async Task WritesUseOneClusterSlotBoundedTtlAndTheReadGeneration()
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        var calls = 0;
        var cache = Cache((script, keys, values) =>
        {
            calls++;
            Assert.Equal(new[] { $"{Scope}:generation", $"{Scope}:index", $"{Scope}:page:generation:page" }, keys.Select(key => key.ToString()));
            Assert.Equal("generation", values[0].ToString());
            Assert.InRange((long)values[1], 1, 15000);
            Assert.Contains("superseded", script);
            Assert.Contains(">= 256", script);
            return Task.FromResult(Text("stored"));
        }, clock);
        Assert.Equal("stored", await cache.WriteAsync(Scope, "page", "generation", [], default));
        Assert.Equal("stored", await cache.WriteAsync(Scope, "page", "generation",
            [new("one", "sam", "Sam", "body", now.AddDays(-7).AddSeconds(2), now)], default));
        Assert.Equal("expired", await cache.WriteAsync(Scope, "page", "generation",
            [new("old", "sam", "Sam", "old", now.AddDays(-8), now)], default));
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task InspectionDoesNotMutateOrFetchMessageContentAndEvictionIsScopedAndIdempotent()
    {
        var operation = Guid.NewGuid().ToString();
        var cache = Cache((script, keys, values) =>
        {
            Assert.DoesNotContain("FLUSH", script); Assert.DoesNotContain("SCAN", script);
            if (keys.Length == 1)
            {
                Assert.Equal($"{Scope}:index", keys[0].ToString());
                Assert.DoesNotContain("SET", script); Assert.DoesNotContain("DEL", script);
                return Task.FromResult(RedisResult.Create(new[] { Number(2), Number(12000), Number(1) }));
            }
            Assert.Equal(new[] { $"{Scope}:generation", $"{Scope}:index", $"{Scope}:eviction:{operation}" }, keys.Select(key => key.ToString()));
            Assert.Equal(operation, values[0].ToString());
            Assert.Equal(CacheCleanup.RetentionSeconds, (long)values[1]);
            Assert.Equal(14 * 86400, CacheCleanup.RetentionSeconds);
            Assert.Contains("'EX', ARGV[2]", script);
            Assert.Contains("if previous then return", script);
            return Task.FromResult(Number(2));
        });
        var snapshot = await cache.InspectAsync(Scope, default);
        Assert.Equal("available", snapshot.Status); Assert.Equal(2, snapshot.Pages);
        Assert.Equal(12, snapshot.MaximumTtlSeconds);
        Assert.Equal(2, await cache.EvictAsync(Scope, operation, default));
        Assert.Equal(2, await cache.EvictAsync(Scope, operation, default));
    }
    [Fact]
    public async Task RedisFailureIsNotReportedAsAnEmptyCacheOrASuccessfulEviction()
    {
        var cache = Cache((_, _, _) => Task.FromException<RedisResult>(
            new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Test Redis unavailable.")));
        Assert.Equal("unavailable", (await cache.ReadAsync(Scope, "page", default)).Outcome);
        Assert.Equal("unavailable", (await cache.WriteAsync(Scope, "page", "0", [], default)));
        var snapshot = await cache.InspectAsync(Scope, default);
        Assert.Equal("unavailable", snapshot.Status); Assert.Null(snapshot.Pages);
        Assert.Equal("cache_eviction_unavailable", (await Assert.ThrowsAsync<AppError>(() =>
            cache.EvictAsync(Scope, Guid.NewGuid().ToString(), default))).Code);
    }
    public class RedisProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = (_, _) => throw new InvalidOperationException("Configure the Redis proxy.");
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
