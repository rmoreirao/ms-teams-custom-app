using System.Collections.Concurrent;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class Simulator(TimeProvider clock, IStateStore store) : IGraphTransport
{
    private readonly ConcurrentDictionary<string, string> scenarios = new();
    public bool Simulated => true;
    public void Next(string actor, string scenario)
    {
        if (scenario is not ("confirm" or "reject" or "unknown" or "throttle" or "delay"))
            throw new AppError(400, "invalid_scenario", "Choose a supported simulator outcome.");
        scenarios[actor] = scenario;
    }
    public async Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct)
    {
        scenarios.TryRemove(actor.Id, out var scenario);
        if (scenario == "reject") return new(OperationState.Rejected, null, "The simulator rejected this message.");
        if (scenario == "throttle") return new(OperationState.Rejected, null, "The simulator returned 429; no message was created.");
        if (scenario == "delay") await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var now = clock.GetUtcNow();
        var message = new Message($"sim-{operation.Id}", actor.Id, actor.Name, operation.Body, now, now, operation.ParentId);
        var result = new WriteResult(OperationState.Confirmed, message);
        await store.SaveSimulatorReceiptAsync(actor.TenantId, conversation.Id, operation.Id, result, ct);
        return scenario == "unknown"
            ? new(OperationState.Unknown, null, "The simulator lost the response after committing. Check this operation; do not resend.")
            : result;
    }
    public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
        store.GetSimulatorReceiptAsync(actor.TenantId, conversation.Id, operation.Id, ct);
}

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
                throw new AppError(429, "graph_capacity_exceeded", "Sending is temporarily limited. Wait before a deliberate new attempt.");
            foreach (var scope in scopes) permits[scope] = now.AddMilliseconds(1250);
        }
        return Task.CompletedTask;
    }
}
