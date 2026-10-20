using Microsoft.Azure.Cosmos;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed record SessionPrimaryProof(bool ProjectionVisible, bool RevocationVisible, bool EpochFenced, int CleanedDocuments);

public static class SessionPrimaryProbe
{
    public static async Task<SessionPrimaryProof> RunAsync(CosmosClient first, CosmosClient second, string containerName, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(containerName, "^session-primary-validation-[a-z0-9-]{1,64}$"))
            throw new InvalidOperationException("Use an explicitly selected isolated synthetic validation container.");
        var container = first.GetContainer("teams", containerName);
        var tracked = new Tracked(new CosmosPartitionDocuments(container, true));
        var writer = new LiveStateStore(tracked, TimeProvider.System);
        var reader = new LiveStateStore(new CosmosPartitionDocuments(second.GetContainer("teams", containerName), true), TimeProvider.System);
        var tenant = $"synthetic-session-validation-{Guid.NewGuid():N}";
        var actor = new Actor(tenant, "synthetic-actor", "Synthetic actor");
        const string resource = "synthetic-conversation";
        var now = DateTimeOffset.UtcNow;
        var visible = false;
        var revoked = false;
        var fenced = false;
        try
        {
            if (!await writer.CreateAsync(new(resource, tenant, "Synthetic validation", "chat", null, [], [], [],
                Epoch: 1, CoverageUntil: now.AddMinutes(5)), ct)) throw new InvalidOperationException("Synthetic probe identity collided.");
            var grant = new Entitlement(tenant, resource, actor.Id, actor.Name, now.AddDays(-1), now,
                now.AddMinutes(5), "synthetic-authority-1", true, true, 1);
            await writer.SetEntitlementAsync(grant, ct);
            await reader.GetMemberAsync(actor, resource, false, ct);
            await reader.GetAsync(tenant, resource, ct);
            await reader.IsBlockedAsync(tenant, actor.Id, ct);
            await writer.ApplyAsync(tenant, resource, new("synthetic-message", actor.Id, actor.Name,
                "Synthetic Cosmos validation, not Teams content.", now, now), ct);
            var metadata = (await reader.GetAsync(tenant, resource, ct))!;
            var messages = await reader.MessagesAsync(actor, resource, null, now.AddDays(-1), null, null, ct);
            visible = metadata.Value.Revision == 1 && messages.Any(message => message.Id == "synthetic-message");
            var operation = new SendOperation(Guid.NewGuid().ToString(), tenant, resource, actor.Id, "synthetic-fingerprint",
                "", null, OperationState.Pending, DateTimeOffset.UtcNow, Epoch: 1);
            if (!await writer.CreateOperationAsync(operation, ct)) throw new InvalidOperationException("Synthetic operation collided.");
            var prior = (await reader.GetOperationAsync(tenant, resource, operation.Id, ct))!;
            var original = (await writer.GetAsync(tenant, resource, ct))!;
            if (!await writer.SaveAsync(original with { Value = original.Value with { Epoch = 2 } }, ct))
                throw new InvalidOperationException("Synthetic epoch changed unexpectedly.");
            try { await reader.SaveOperationAsync(prior with { Value = prior.Value with { State = OperationState.Dispatching } }, ct); }
            catch (AppError error) when (error.Code == "writer_fenced") { fenced = true; }
            var verified = DateTimeOffset.UtcNow;
            await writer.SetEntitlementAsync(grant with { Active = false, Epoch = 2, VerifiedAt = verified,
                FreshUntil = verified.AddMinutes(5), AuthorityVersion = "synthetic-authority-2" }, ct);
            var denied = false;
            try { await reader.GetMemberAsync(actor, resource, false, ct); }
            catch (AppError error) when (error.Status == 404) { denied = true; }
            await writer.SetBlockedAsync(tenant, actor.Id, true, ct);
            revoked = denied && await reader.IsBlockedAsync(tenant, actor.Id, ct);
            if (!visible || !revoked || !fenced) throw new AppError(503, "session_probe_failed", "The independent-client SessionPrimary check failed.");
        }
        finally
        {
            foreach (var (pk, id) in tracked.Created)
            {
                try { await container.DeleteItemAsync<PartitionDocument>(id, new PartitionKey(pk), cancellationToken: CancellationToken.None); }
                catch (CosmosException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            }
        }
        return new(visible, revoked, fenced, tracked.Created.Count);
    }
    private sealed class Tracked(IPartitionDocuments inner) : IPartitionDocuments
    {
        public HashSet<(string Pk, string Id)> Created { get; } = [];
        public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct) => inner.ReadAsync(pk, id, ct);
        public Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct) =>
            inner.QueryAsync(filter, limit, cursor, ct);
        public async Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
        {
            var result = await inner.WriteAsync(writes, ct);
            if (result) foreach (var write in writes) Created.Add((write.Value.Pk, write.Value.Id));
            return result;
        }
    }
}
