using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class CosmosSessionReadFactAttribute : FactAttribute
{
    public CosmosSessionReadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COSMOS_SESSION_TEST") != "1")
            Skip = "Opt-in real Cosmos check: requires an explicitly selected private synthetic container and existing Entra data access.";
    }
}

public sealed class CosmosSessionReadTests
{
    [CosmosSessionReadFact]
    public async Task IndependentGatewayClientsObserveProjectionRevocationAndEpochFencesWithoutAnAccountUpgrade()
    {
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_SESSION_ENDPOINT");
        var name = Environment.GetEnvironmentVariable("COSMOS_SESSION_CONTAINER");
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
        new LiveOptions { ReadConsistency = "Session" }.ValidateStorageProfile(
            account.Consistency.DefaultConsistencyLevel.ToString(), account.ReadableRegions.Count(), account.WritableRegions.Count());
        var proof = await SessionReadProbe.RunAsync(writerClient, readerClient, name, default);
        Assert.True(proof.ProjectionVisible);
        Assert.True(proof.RevocationVisible);
        Assert.True(proof.EpochFenced);
        Assert.True(proof.CleanedDocuments > 0);
    }
}
