using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Teams.Core;

namespace Teams.Tests;

public sealed class MemoryBudget(TimeProvider clock) : ICommandBudget
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> permits = [];
    public Task AcquireAsync(Actor actor, Conversation conversation, CancellationToken ct)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            string[] scopes = [$"{actor.TenantId}|actor:{actor.Id}", $"{actor.TenantId}|conversation:{conversation.Id}"];
            if (scopes.Any(s => permits.TryGetValue(s, out var at) && at > now))
                throw new AppError(429, "graph_capacity_exceeded", "Wait before another send.");
            foreach (var scope in scopes) permits[scope] = now.AddMilliseconds(1250);
        }
        return Task.CompletedTask;
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

public sealed class GraphResultStub(TimeProvider clock) : IGraphTransport
{
    public int Sends { get; private set; }
    public OperationState NextResult { get; set; } = OperationState.Confirmed;
    public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct)
    {
        Sends++;
        return Task.FromResult(NextResult == OperationState.Confirmed
            ? new WriteResult(OperationState.Confirmed, new(operation.Id, actor.Id, actor.Name, operation.Body,
                clock.GetUtcNow(), clock.GetUtcNow(), operation.ParentId))
            : new WriteResult(NextResult, null, "Controlled Graph dependency outcome."));
    }
    public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
        Task.FromResult<WriteResult?>(null);
}
