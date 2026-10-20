using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Cosmos;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class CosmosSessionPrimaryFactAttribute : FactAttribute
{
    public CosmosSessionPrimaryFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COSMOS_SESSION_PRIMARY_TEST") != "1")
            Skip = "Opt-in real Cosmos check: requires an explicitly selected private synthetic container and existing Entra data access.";
    }
}

public sealed class CosmosSessionPrimaryTests
{
    [CosmosSessionPrimaryFact]
    public async Task IndependentGatewayClientsObserveProjectionRevocationAndEpochFencesWithoutAnAccountUpgrade()
    {
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_SESSION_PRIMARY_ENDPOINT");
        var name = Environment.GetEnvironmentVariable("COSMOS_SESSION_PRIMARY_CONTAINER");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            !uri.Host.EndsWith(".documents.azure.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            name is null || !System.Text.RegularExpressions.Regex.IsMatch(name, "^session-primary-validation-[a-z0-9-]{1,64}$"))
            throw new InvalidOperationException("Select an explicit Cosmos endpoint and a reserved synthetic validation container.");

        CosmosClient Client() => new(uri.AbsoluteUri, new DefaultAzureCredential(), new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway, ConsistencyLevel = ConsistencyLevel.Session,
            SerializerOptions = new() { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
            RequestTimeout = TimeSpan.FromSeconds(10), MaxRetryAttemptsOnRateLimitedRequests = 1,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(2)
        });
        using var writerClient = Client();
        using var readerClient = Client();
        var account = await readerClient.ReadAccountAsync();
        new LiveOptions { ReadConsistency = "SessionPrimary" }.ValidateStorageProfile(
            account.Consistency.DefaultConsistencyLevel.ToString(), account.ReadableRegions.Count(), account.WritableRegions.Count());
        var container = writerClient.GetContainer("teams", name);
        var tracking = new TrackingDocuments(new CosmosPartitionDocuments(container, primaryReads: true));
        var writer = new LiveStateStore(tracking, TimeProvider.System);
        var reader = new LiveStateStore(new CosmosPartitionDocuments(readerClient.GetContainer("teams", name), primaryReads: true), TimeProvider.System);
        var tenant = $"synthetic-session-validation-{Guid.NewGuid():N}";
        var actor = new Actor(tenant, "synthetic-actor", "Synthetic actor");
        const string conversation = "synthetic-conversation";
        var now = DateTimeOffset.UtcNow;
        try
        {
            Assert.True(await writer.CreateAsync(new(conversation, tenant, "Synthetic validation", "chat", null, [], [], [],
                Epoch: 1, CoverageUntil: now.AddMinutes(5)), default));
            var grant = new Entitlement(tenant, conversation, actor.Id, actor.Name, now.AddDays(-1), now,
                now.AddMinutes(5), "synthetic-authority-1", true, true, 1);
            await writer.SetEntitlementAsync(grant, default);
            await reader.GetMemberAsync(actor, conversation, false, default);
            Assert.Equal(0, (await reader.GetAsync(tenant, conversation, default))!.Value.Revision);
            Assert.False(await reader.IsBlockedAsync(tenant, actor.Id, default));

            await writer.ApplyAsync(tenant, conversation,
                new("synthetic-message", actor.Id, actor.Name, "Synthetic Cosmos validation, not Teams content.", now, now), default);
            var messaging = new Messaging(reader, new NoGraph(), new MemoryBudget(TimeProvider.System),
                new CommandTokens(new EphemeralDataProtectionProvider(), TimeProvider.System), TimeProvider.System);
            var page = await messaging.ReadAsync(actor, conversation, null, null, 1, default);
            Assert.Equal(1, page.Revision);
            Assert.Equal("synthetic-message", Assert.Single(page.Messages).Id);

            var operation = new SendOperation(Guid.NewGuid().ToString(), tenant, conversation, actor.Id, "synthetic-fingerprint",
                "", null, OperationState.Pending, DateTimeOffset.UtcNow, Epoch: 1);
            Assert.True(await writer.CreateOperationAsync(operation, default));
            var observed = (await reader.GetOperationAsync(tenant, conversation, operation.Id, default))!;
            var metadata = (await writer.GetAsync(tenant, conversation, default))!;
            Assert.True(await writer.SaveAsync(metadata with { Value = metadata.Value with { Epoch = 2 } }, default));
            Assert.Equal("writer_fenced", (await Assert.ThrowsAsync<AppError>(() =>
                reader.SaveOperationAsync(observed with { Value = observed.Value with { State = OperationState.Dispatching } }, default))).Code);

            var verified = DateTimeOffset.UtcNow;
            await writer.SetEntitlementAsync(grant with { Active = false, Epoch = 2, VerifiedAt = verified,
                FreshUntil = verified.AddMinutes(5), AuthorityVersion = "synthetic-authority-2" }, default);
            Assert.Equal(404, (await Assert.ThrowsAsync<AppError>(() =>
                reader.GetMemberAsync(actor, conversation, false, default))).Status);
            await writer.SetBlockedAsync(tenant, actor.Id, true, default);
            Assert.True(await reader.IsBlockedAsync(tenant, actor.Id, default));
        }
        finally
        {
            foreach (var (pk, id) in tracking.Created)
            {
                try { await container.DeleteItemAsync<PartitionDocument>(id, new PartitionKey(pk)); }
                catch (CosmosException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            }
        }
    }

    private sealed class TrackingDocuments(IPartitionDocuments inner) : IPartitionDocuments
    {
        public HashSet<(string Pk, string Id)> Created { get; } = [];
        public Task<Versioned<PartitionDocument>?> ReadAsync(string pk, string id, CancellationToken ct) => inner.ReadAsync(pk, id, ct);
        public Task<DocumentPage> QueryAsync(DocumentFilter filter, int limit, string? cursor, CancellationToken ct) =>
            inner.QueryAsync(filter, limit, cursor, ct);
        public async Task<bool> WriteAsync(IReadOnlyList<DocumentWrite> writes, CancellationToken ct)
        {
            var saved = await inner.WriteAsync(writes, ct);
            if (saved) foreach (var write in writes) Created.Add((write.Value.Pk, write.Value.Id));
            return saved;
        }
    }
    private sealed class NoGraph : IGraphTransport
    {
        public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Storage validation must not call Teams.");
        public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Storage validation must not call Teams.");
    }
}
