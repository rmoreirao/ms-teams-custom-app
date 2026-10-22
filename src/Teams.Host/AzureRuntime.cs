using System.Text.Json;
using Azure.Identity;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.WebPubSub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Azure.Cosmos;
using StackExchange.Redis;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public static class LiveHostConfiguration
{
    public static (string Tenant, string Role, LiveOptions Options) Read(IConfiguration config)
    {
        if (config["App:GraphMode"] != "Live" || config["App:Store"] != "Cosmos" || config["App:Auth"] != "Entra")
            throw new InvalidOperationException("Only Live mode with Cosmos persistence and Entra authentication is supported.");
        var tenant = config["App:TenantId"] ?? throw new InvalidOperationException("App:TenantId must be configured.");
        var role = config["App:Role"] ?? throw new InvalidOperationException("App:Role must be configured.");
        var options = config["Live:Configuration"] is { Length: > 0 } json
            ? JsonSerializer.Deserialize<LiveOptions>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Live configuration is empty.")
            : config.GetSection("Live").Get<LiveOptions>() ?? new();
        options.Validate(tenant, false, false, role);
        options.ValidateIdentityBindings(config["AzureAd:ClientId"], config["AzureClientId"], role);
        return (tenant, role, options);
    }
}

public static class AzureRuntime
{
    public static void AddAzureRuntime(this IServiceCollection services, IConfiguration config, LiveOptions options, string tenant, string role)
    {
        services.AddSingleton(sp => new GraphRetry(options.GraphRetry, sp.GetRequiredService<TimeProvider>()));
        TokenCredential credential = options.EntitlementProvider == "Graph"
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config["AzureClientId"]!))
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = config["AzureClientId"] });
        var protection = services.AddDataProtection().SetApplicationName("TeamsReference.Live.v1");
        if (role == "Api" || options.Hosts("Control", role))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redis = ConfigurationOptions.Parse(config["Azure:Redis"] ?? throw new InvalidOperationException("Azure:Redis is required."));
                redis.Ssl = true;
                redis.AbortOnConnectFail = false;
                redis.ConfigureForAzureWithTokenCredentialAsync(credential).GetAwaiter().GetResult();
                return ConnectionMultiplexer.Connect(redis);
            });
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = config["Azure:Redis"];
                o.InstanceName = RuntimeCacheScope.Prefix(tenant, config["AzureAd:ClientId"]!);
            });
            services.AddOptions<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions>()
                .Configure<IConnectionMultiplexer>((o, redis) => o.ConnectionMultiplexerFactory = () => Task.FromResult(redis));
            if (role == "Api")
            {
                services.AddOptions<KeyManagementOptions>().Configure<IConnectionMultiplexer>((o, redis) =>
                    o.XmlRepository = new RedisXmlRepository(() => redis.GetDatabase(),
                        $"data-protection:live:{tenant}:{config["AzureAd:ClientId"]}:v1"));
                protection.ProtectKeysWithAzureKeyVault(new Uri(config["Azure:ProtectionKey"] ?? throw new InvalidOperationException("Azure:ProtectionKey is required.")), credential);
            }
            services.AddSingleton<ICommandBudget>(new LiveCommandBudget(options));
        }
        else services.AddDistributedMemoryCache();
        services.AddSingleton(_ => new CosmosClient(config["Azure:CosmosEndpoint"], credential, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
            ConsistencyLevel = ConsistencyLevel.Session,
            RequestTimeout = TimeSpan.FromSeconds(10),
            MaxRetryAttemptsOnRateLimitedRequests = 2,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(5)
        }));
        services.AddHostedService<LiveStorageValidation>();
        services.AddSingleton<IPartitionDocuments>(sp => new CosmosPartitionDocuments(
            sp.GetRequiredService<CosmosClient>().GetContainer("teams", "live-state")));
        services.AddKeyedSingleton<IPartitionDocuments>("control", (sp, _) => new CosmosPartitionDocuments(
            sp.GetRequiredService<CosmosClient>().GetContainer("teams", "live-control")));
        if (role == "Api") services.AddSingleton<IRecentMessageCache, RedisRecentMessages>();
        services.AddSingleton<ILiveStateStore>(sp => new LiveStateStore(sp.GetRequiredService<IPartitionDocuments>(),
            sp.GetRequiredService<TimeProvider>(), new CosmosPartitionDocuments(
                sp.GetRequiredService<CosmosClient>().GetContainer("teams", "live-authority")),
            sp.GetService<IRecentMessageCache>(), options.Resources.Select(r => r.Id).ToHashSet(StringComparer.Ordinal),
            options.Resources.ToDictionary(r => r.Id, StringComparer.Ordinal)));
        services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<ILiveStateStore>());
        services.AddSingleton(_ => new ServiceBusClient(config["Azure:ServiceBus"], credential));
        services.AddSingleton(_ => new WebPubSubServiceClient(new Uri(config["Azure:PubSub"]
            ?? throw new InvalidOperationException("Azure:PubSub is required.")), "messaging", credential));
        services.AddSingleton<IRealtime>(sp => new AzureRealtime(sp.GetRequiredService<WebPubSubServiceClient>(),
            sp.GetRequiredService<IPrincipalRevocations>()));
        services.AddLiveRuntime(options, tenant, role, credential);
    }
}

public sealed class LiveStorageValidation(Microsoft.Azure.Cosmos.CosmosClient cosmos, LiveOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var account = await cosmos.ReadAccountAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
        options.ValidateStorageProfile(account.Consistency.DefaultConsistencyLevel.ToString(),
            account.ReadableRegions.Count(), account.WritableRegions.Count());
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
