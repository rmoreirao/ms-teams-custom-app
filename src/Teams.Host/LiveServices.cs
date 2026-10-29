using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Teams.Core;

namespace Teams.Host;

public static class LiveServices
{
    public static void AddLiveRuntime(this IServiceCollection services, LiveOptions options, string tenant,
        string role, TokenCredential credential)
    {
        services.AddSingleton(options);
        services.AddSingleton(sp => new LiveControlStore(sp.GetRequiredKeyedService<Teams.Infrastructure.IPartitionDocuments>("control"),
            sp.GetRequiredService<TimeProvider>(), tenant));
        if (options.ChannelDiscovery.RegistryEnabled)
        {
            services.AddHostedService<ChannelRuntimeValidation>();
            services.AddSingleton(sp => new ChannelWorkflowStore(sp.GetRequiredService<Teams.Infrastructure.IPartitionDocuments>(),
                tenant, sp.GetRequiredService<TimeProvider>()));
            if (role is "Api" or "Control") services.AddSingleton<IChannelWorkQueue, ServiceBusChannelWork>();
        }
        services.AddHostedService<ReadRefreshQueueValidation>();
        if (options.Hosts("Api", role)) services.AddSingleton<IReadRefresh, ServiceBusReadRefresh>();
        if (options.Hosts("Receiver", role))
        {
            services.AddSingleton<INotificationTokenValidator>(new NotificationTokenValidator(tenant, options.SyncClientId,
                new ConfigurationManager<OpenIdConnectConfiguration>(
                    "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever())));
            services.AddSingleton<IEncryptedNotificationInbox, ServiceBusNotificationInbox>();
            services.AddSingleton(sp => new NotificationIntake(options, tenant, sp.GetRequiredService<LiveControlStore>(),
                sp.GetRequiredService<INotificationTokenValidator>(), sp.GetRequiredService<IEncryptedNotificationInbox>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IResourceRegistry>()));
        }
        if (options.Hosts("Projector", role))
        {
            services.AddSingleton<IEncryptedNotificationInbox, ServiceBusNotificationInbox>();
            services.AddSingleton<INotificationKeyDecryptor>(new VaultNotificationKeys(options, credential));
            services.AddSingleton<NotificationDecryptor>();
            services.AddSingleton(sp => new NotificationProjector(options, tenant, sp.GetRequiredService<ILiveStateStore>(),
                sp.GetRequiredService<LiveControlStore>(), sp.GetRequiredService<NotificationDecryptor>(),
                sp.GetRequiredService<IEncryptedNotificationInbox>(), sp.GetRequiredService<IResourceRegistry>()));
            services.AddHostedService<NotificationWorker>();
        }
        if (options.Hosts("Delivery", role))
        {
            services.AddSingleton<IDeliveryWorkQueue, ServiceBusDeliveryQueue>();
            services.AddSingleton(sp => new OutboxDispatcher(tenant, options, sp.GetRequiredService<IDeliveryWorkQueue>(),
                sp.GetRequiredService<ILogger<OutboxDispatcher>>(), sp.GetRequiredService<IResourceRegistry>()));
            services.AddSingleton(sp => new DeliveryHandler(tenant, options, sp.GetRequiredService<ILiveStateStore>(),
                sp.GetRequiredService<IRealtime>(), sp.GetRequiredService<IDeliveryWorkQueue>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<DeliveryHandler>>(), sp.GetRequiredService<IResourceRegistry>(),
                sp.GetService<ChannelWorkflowStore>()));
            services.AddHostedService<OutboxFeedWorker>();
            services.AddHostedService<DeliveryQueueWorker>();
        }
        if (options.Hosts("Control", role))
        {
            services.AddHttpClient("live-graph", c => c.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddHttpClient("live-authority", c => c.Timeout = TimeSpan.FromSeconds(10))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            if (options.DelegatedSynthetic)
            {
                services.AddSingleton<DelegatedSyntheticGraph>();
                services.AddSingleton<IGraphBackground>(sp => sp.GetRequiredService<DelegatedSyntheticGraph>());
                services.AddSingleton<IEntitlementFeed, DelegatedSyntheticAuthority>();
            }
            else
            {
                services.AddSingleton<GraphBackground>(sp =>
                {
                    var managed = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ControlIdentityClientId));
                    var sync = new ClientAssertionCredential(tenant, options.SyncClientId, async ct =>
                        (await managed.GetTokenAsync(new TokenRequestContext(["api://AzureADTokenExchange/.default"]), ct)).Token);
                    return new GraphBackground(sp.GetRequiredService<IHttpClientFactory>().CreateClient("live-graph"), sync,
                        sp.GetRequiredService<GraphRetry>());
                });
                if (options.EntitlementProvider == "Graph")
                {
                    services.AddSingleton<ApplicationGraph>();
                    services.AddSingleton<IGraphBackground>(sp => sp.GetRequiredService<ApplicationGraph>());
                    services.AddSingleton<IGraphPolicyReader>(sp => sp.GetRequiredService<ApplicationGraph>());
                    services.AddSingleton(sp => new GraphPolicyFeed(
                        sp.GetRequiredService<IGraphBackground>(), sp.GetRequiredService<IGraphPolicyReader>(),
                        sp.GetRequiredService<ILiveStateStore>(), options, tenant, configWebClient(sp),
                        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<GraphPolicyFeed>>(),
                        sp.GetRequiredService<IResourceRegistry>()));
                    services.AddSingleton<IEntitlementFeed>(sp => sp.GetRequiredService<GraphPolicyFeed>());
                }
                else
                {
                    services.AddSingleton<IGraphBackground>(sp => sp.GetRequiredService<GraphBackground>());
                    services.AddSingleton<IEntitlementFeed>(sp => new EntitlementFeed(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("live-authority"), credential, options, tenant));
                }
            }
            services.AddSingleton<SubscriptionController>();
            services.AddSingleton<ResourceHydrator>();
            services.AddSingleton(sp => new ReadRefreshHandler(tenant, options, sp.GetRequiredService<ILiveStateStore>(),
                sp.GetRequiredService<LiveControlStore>(), sp.GetRequiredService<IGraphBackground>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ReadRefreshHandler>>(),
                sp.GetRequiredService<IResourceRegistry>()));
            services.AddHostedService<ReadRefreshWorker>();
            services.AddHostedService(sp => new LiveControlWorker(tenant, options, sp.GetRequiredService<ILiveStateStore>(),
                sp.GetRequiredService<LiveControlStore>(), sp.GetRequiredService<SubscriptionController>(),
                sp.GetRequiredService<ResourceHydrator>(), sp.GetRequiredService<IEntitlementFeed>(),
                sp.GetRequiredService<IRealtime>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LiveControlWorker>>(),
                sp.GetRequiredService<IResourceRegistry>()));
            if (options.ChannelDiscovery.RegistryEnabled)
            {
                services.AddSingleton<ChannelGraphSource>();
                services.AddSingleton(sp => new ChannelJobHandler(tenant, options, sp.GetRequiredService<ChannelWorkflowStore>(),
                    sp.GetRequiredService<ChannelGraphSource>(), sp.GetRequiredService<IResourceRegistry>(),
                    sp.GetRequiredService<ILiveStateStore>(), sp.GetRequiredService<IEntitlementFeed>(),
                    sp.GetRequiredService<SubscriptionController>(), sp.GetRequiredService<ReadRefreshHandler>(),
                    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IRealtime>()));
                services.AddHostedService<ChannelJobWorker>();
            }
        }
        static string configWebClient(IServiceProvider sp) => sp.GetRequiredService<IConfiguration>()["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("The Web application client ID is required for source-backed admission.");
    }
    public static void MapGraphCallbacks(this WebApplication app)
    {
        foreach (var route in new[] { "/callbacks/graph/notifications", "/callbacks/graph/lifecycle" })
        {
            var lifecycle = route.EndsWith("/lifecycle", StringComparison.Ordinal);
            app.MapPost(route, async (HttpContext context, NotificationIntake intake) =>
            {
                var validation = context.Request.Query["validationToken"].ToString();
                if (validation.Length > 0)
                    return validation.Length <= 2048 ? Results.Content(validation, "text/plain") : Results.BadRequest();
                try
                {
                    using var body = await JsonDocument.ParseAsync(context.Request.Body, new() { MaxDepth = 32 }, context.RequestAborted);
                    var count = await intake.AcceptAsync(body.RootElement, lifecycle, context.RequestAborted);
                    app.Logger.LogInformation("Graph callback durably processed; lifecycle={Lifecycle}, admitted={Count}, trace {Trace}",
                        lifecycle, count, context.TraceIdentifier);
                    return Results.Accepted();
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or SecurityTokenException ||
                    ex is AppError { Status: < 500 })
                {
                    // Graph's webhook protocol conceals invalid-payload validation results from publishers.
                    app.Logger.LogWarning("Graph callback rejected without enqueue: {Code}, lifecycle={Lifecycle}, trace {Trace}",
                        ex is AppError error ? error.Code : ex.GetType().Name, lifecycle, context.TraceIdentifier);
                    return Results.Accepted();
                }
            });
        }
    }
}
public sealed class LiveCommandBudget(LiveOptions options) : ICommandBudget
{
    public Task AcquireAsync(Actor actor, Conversation conversation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.AdmissionEnabled || conversation.Epoch != options.WriterEpoch)
            throw new AppError(503, "writer_fenced", "This runtime does not own the approved command epoch.");
        return Task.CompletedTask;
    }
}
