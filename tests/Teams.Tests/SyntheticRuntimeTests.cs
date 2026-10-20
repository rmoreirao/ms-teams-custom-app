using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class SyntheticRuntimeTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private static readonly string[] Actors = [
        "33333333-3333-3333-3333-333333333333",
        "44444444-4444-4444-4444-444444444444",
        "55555555-5555-5555-5555-555555555555"
    ];
    private static LiveOptions Options() => new() {
        AuthorityMode = "DelegatedSynthetic", ReadConsistency = "SessionPrimary",
        SyntheticActorIds = Actors.ToArray(), SyntheticNotBefore = DateTimeOffset.UtcNow.AddDays(-1),
        HostedRoles = ["Api","Receiver","Control"]
    };
    [Fact]
    public void CompactHostingIsAnExplicitSyntheticOnlyChoice()
    {
        var options = Options();
        options.ValidateHosting("Api", Tenant);
        Assert.True(options.Hosts("Receiver", "Api"));
        Assert.True(options.Hosts("Control", "Api"));
        Assert.False(options.Hosts("Projector", "Api"));
        options.HostedRoles = ["Projector","Delivery"];
        options.ValidateHosting("Projector", Tenant);
        options.AuthorityMode = "Policy";
        Assert.Throws<InvalidOperationException>(() => options.ValidateHosting("Projector", Tenant));
    }
    [Fact]
    public void EncryptedLiveCacheNeverReusesThePilotsDifferentlyProtectedTokenOrSessionBlobs()
    {
        var live = RuntimeCacheScope.Prefix(Tenant, Client);
        Assert.NotEqual("", live);
        Assert.Equal($"live-cache-v1:{Tenant}:{Client}:", live);
        Assert.NotEqual(live, RuntimeCacheScope.Prefix("other-tenant", Client));
        Assert.NotEqual(live, RuntimeCacheScope.Prefix(Tenant, "other-client"));
    }
    [Fact]
    public void SyntheticHostingCannotQuietlyBroadenActorsOrEnableAnUnsupportedRoleMix()
    {
        foreach (var options in new[] {
            new LiveOptions { AuthorityMode = "DelegatedSynthetic" },
            new LiveOptions { AuthorityMode = "Unknown" },
            new LiveOptions { AuthorityMode = "Policy", HostedRoles = ["Api", "Control"] }
        }) Assert.Throws<InvalidOperationException>(() => options.ValidateHosting("Api", Tenant));
        var valid = Options();
        valid.SyntheticActorIds = [..Actors, "66666666-6666-6666-6666-666666666666"];
        Assert.Throws<InvalidOperationException>(() => valid.ValidateHosting("Api", Tenant));
        valid = Options();
        valid.HostedRoles = ["Api","Receiver","Control","Projector"];
        Assert.Throws<InvalidOperationException>(() => valid.ValidateHosting("Api", Tenant));
    }
    [Fact]
    public async Task DelegatedContextsAreBoundToTheActualUserTenantAndOriginalSessionExpiry()
    {
        var clock = new Clock();
        using var provider = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var contexts = new SyntheticUserContexts(provider.GetRequiredService<IDistributedCache>(),
            new EphemeralDataProtectionProvider(), Options(), new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["App:TenantId"] = Tenant, ["AzureAd:ClientId"] = Client }).Build(),
            clock, NullLogger<SyntheticUserContexts>.Instance);
        ClaimsPrincipal Principal(string actor = "33333333-3333-3333-3333-333333333333", string tenant = Tenant) =>
            new(new ClaimsIdentity([new("oid", actor), new("tid", tenant), new("name", "Synthetic member"),
                new("app_sid", Guid.NewGuid().ToString())], "validated-test-authentication"));
        var expiry = clock.GetUtcNow().AddHours(1);
        await contexts.SaveAsync(Principal(), expiry, default);
        Assert.Equal(Actors[0], SyntheticUserContexts.ActorId(await contexts.ReadAsync(Actors[0], default)));
        Assert.Equal("synthetic_context_denied", (await Assert.ThrowsAsync<AppError>(() =>
            contexts.SaveAsync(Principal(tenant: "other"), expiry, default))).Code);
        Assert.Equal("synthetic_context_denied", (await Assert.ThrowsAsync<AppError>(() =>
            contexts.SaveAsync(Principal(), clock.GetUtcNow().AddHours(9), default))).Code);
        Assert.Equal("synthetic_actor_denied", (await Assert.ThrowsAsync<AppError>(() =>
            contexts.ReadAsync("66666666-6666-6666-6666-666666666666", default))).Code);
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal("synthetic_signin_required", (await Assert.ThrowsAsync<AppError>(() =>
            contexts.ReadAsync(Actors[0], default))).Code);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
