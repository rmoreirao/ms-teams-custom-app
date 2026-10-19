using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.WebPubSub;
using StackExchange.Redis;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class MemoryInbox : IEventInbox
{
    private readonly Channel<InboxDelivery> channel = Channel.CreateBounded<InboxDelivery>(new BoundedChannelOptions(1000)
    { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, EventEnvelope> pending = new();
    public Task EnqueueAsync(EventEnvelope envelope, CancellationToken ct) =>
        channel.Writer.WriteAsync(new(envelope, Guid.NewGuid().ToString("N")), ct).AsTask();
    public async Task<InboxDelivery?> ReceiveAsync(CancellationToken ct)
    {
        if (!channel.Reader.TryRead(out var item)) return null;
        pending[item.Receipt] = item.Envelope;
        return await Task.FromResult(item);
    }
    public Task CompleteAsync(string receipt, CancellationToken ct)
    {
        pending.TryRemove(receipt, out _);
        return Task.CompletedTask;
    }
    public async Task AbandonAsync(string receipt, CancellationToken ct)
    {
        if (pending.TryRemove(receipt, out var value))
            await channel.Writer.WriteAsync(new(value, receipt), ct);
    }
    public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Task.FromResult(0);
}

public sealed class ServiceBusInbox(ServiceBusClient client, string queue) : IEventInbox
{
    private readonly ServiceBusSender sender = client.CreateSender(queue);
    private readonly ServiceBusReceiver receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions
    { ReceiveMode = ServiceBusReceiveMode.PeekLock });
    private readonly ServiceBusReceiver deadLetters = client.CreateReceiver(queue,
        new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock, SubQueue = SubQueue.DeadLetter });
    private readonly ConcurrentDictionary<string, ServiceBusReceivedMessage> pending = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task EnqueueAsync(EventEnvelope envelope, CancellationToken ct) =>
        await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(envelope, Json))
        {
            ContentType = "application/json",
            TimeToLive = TimeSpan.FromHours(1),
            MessageId = Guid.NewGuid().ToString("N")
        }, ct);
    public async Task<InboxDelivery?> ReceiveAsync(CancellationToken ct)
    {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1), ct);
        if (message is null) return null;
        var envelope = message.Body.ToObjectFromJson<EventEnvelope>(Json)
            ?? throw new InvalidOperationException("Notification envelope was empty.");
        pending[message.LockToken] = message;
        return new(envelope, message.LockToken);
    }
    public async Task CompleteAsync(string receipt, CancellationToken ct)
    {
        if (!pending.TryGetValue(receipt, out var message))
            throw new InvalidOperationException("Notification receipt was not owned by this consumer.");
        await receiver.CompleteMessageAsync(message, ct);
        pending.TryRemove(receipt, out _);
    }
    public async Task AbandonAsync(string receipt, CancellationToken ct)
    {
        if (pending.TryRemove(receipt, out var message))
            await receiver.AbandonMessageAsync(message, cancellationToken: ct);
    }
    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        var messages = await deadLetters.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(1), ct);
        var purged = 0;
        foreach (var message in messages)
        {
            if (message.EnqueuedTime < now.AddHours(-1))
            {
                await deadLetters.CompleteMessageAsync(message, ct);
                purged++;
            }
            else await deadLetters.AbandonMessageAsync(message, cancellationToken: ct);
        }
        return purged;
    }
}

public sealed class RedisBudget(IConnectionMultiplexer redis, string clientId) : ICommandBudget, IGraphThrottle
{
    // A single hash keeps the global, tenant, conversation and actor permits in one Redis cluster slot.
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

public sealed class LocalRealtime(IPrincipalRevocations revocations) : IRealtime
{
    private readonly ConcurrentDictionary<string, Channel<string>> listeners = new();
    public (string Id, ChannelReader<string> Reader) Subscribe(Actor actor)
    {
        var id = $"{actor.TenantId}|{actor.Id}|{Guid.NewGuid():N}";
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
        listeners[id] = channel;
        return (id, channel.Reader);
    }
    public void Unsubscribe(string id) => listeners.TryRemove(id, out _);
    public async Task PublishAsync(Conversation conversation, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { conversationId = conversation.Id, revision = conversation.Revision });
        foreach (var member in conversation.Members.Where(m => m.Active))
        {
            if (await revocations.IsBlockedAsync(conversation.TenantId, member.Id, ct)) continue;
            foreach (var listener in listeners.Where(l => l.Key.StartsWith($"{conversation.TenantId}|{member.Id}|", StringComparison.Ordinal)))
                listener.Value.Writer.TryWrite(body);
        }
    }
    public Task RevokeAsync(string tenant, string actorId, CancellationToken ct)
    {
        foreach (var listener in listeners.Where(l => l.Key.StartsWith($"{tenant}|{actorId}|", StringComparison.Ordinal)))
        {
            listener.Value.Writer.TryComplete();
            listeners.TryRemove(listener.Key, out _);
        }
        return Task.CompletedTask;
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
