using Azure.Messaging.WebPubSub;
using StackExchange.Redis;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class RedisBudget(IConnectionMultiplexer redis, string clientId) : ICommandBudget, IGraphThrottle
{
    // One hash keeps global, tenant, conversation and actor permits in the same Redis cluster slot.
    private const string Script = """
        local t=redis.call('TIME'); local now=t[1]*1000+math.floor(t[2]/1000)
        for i=1,#ARGV,2 do
          local at=tonumber(redis.call('HGET',KEYS[1],ARGV[i]) or '0')
          if at>now then return 0 end
        end
        for i=1,#ARGV,2 do redis.call('HSET',KEYS[1],ARGV[i],now+tonumber(ARGV[i+1])) end
        redis.call('EXPIRE',KEYS[1],86400); return 1
        """;
    public async Task AcquireAsync(Actor actor, Conversation conversation, CancellationToken ct)
    {
        var channel = conversation.Kind == "channel";
        RedisValue[] values =
        [
            $"cooldown:{actor.TenantId}", 0,
            $"global:{channel}", channel ? 3 : 7,
            $"tenant:{actor.TenantId}:{channel}", channel ? 25 : 63,
            $"conversation:{actor.TenantId}:{conversation.Id}", 1250,
            $"actor:{actor.TenantId}:{actor.Id}", 1250
        ];
        var result = await redis.GetDatabase().ScriptEvaluateAsync(Script, [$"budget:{{{clientId}}}"], values);
        if ((long)result != 1)
            throw new AppError(429, "graph_capacity_exceeded", "The shared send budget is exhausted. Wait before a new attempt.");
    }
    public async Task CooldownAsync(Actor actor, TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await redis.GetDatabase().ScriptEvaluateAsync("""
            local t=redis.call('TIME'); local now=t[1]*1000+math.floor(t[2]/1000)
            local previous=tonumber(redis.call('HGET',KEYS[1],ARGV[1]) or '0')
            redis.call('HSET',KEYS[1],ARGV[1],math.max(previous,now+tonumber(ARGV[2])))
            redis.call('EXPIRE',KEYS[1],math.max(86400,math.ceil(tonumber(ARGV[2])/1000)))
            """, [$"budget:{{{clientId}}}"], [$"cooldown:{actor.TenantId}", (long)delay.TotalMilliseconds]);
    }
}

public sealed class AzureRealtime(WebPubSubServiceClient client, IPrincipalRevocations revocations) : IRealtime
{
    public static string User(string tenant, string actor) => $"{tenant}:{actor}";
    public async Task PublishAsync(Conversation conversation, CancellationToken ct)
    {
        var body = BinaryData.FromObjectAsJson(new { conversationId = conversation.Id, revision = conversation.Revision });
        foreach (var member in conversation.Members.Where(m => m.Active))
        {
            if (await revocations.IsBlockedAsync(conversation.TenantId, member.Id, ct)) continue;
            await client.SendToUserAsync(User(conversation.TenantId, member.Id), Azure.Core.RequestContent.Create(body),
                Azure.Core.ContentType.ApplicationJson, context: new Azure.RequestContext { CancellationToken = ct });
        }
    }
    public async Task RevokeAsync(string tenant, string actorId, CancellationToken ct) =>
        await client.CloseUserConnectionsAsync(User(tenant, actorId), reason: "Access revoked",
            context: new Azure.RequestContext { CancellationToken = ct });
}
