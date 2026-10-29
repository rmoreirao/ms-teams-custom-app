using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Azure.Cosmos;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public static class LiveDiagnostics
{
    public static Task HoldLogsAsync(IConfiguration configuration)
    {
        var seconds = configuration.GetValue<int>("LiveDiagnostics:HoldLogSeconds");
        if (seconds is < 0 or > 60) throw new InvalidOperationException("Diagnostic log retention must be bounded to sixty seconds.");
        return Task.Delay(TimeSpan.FromSeconds(seconds));
    }
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    public static async Task PrepareNotificationAsync(IConfiguration configuration)
    {
        var value = configuration["LivePreparation:KeyUri"];
        if (configuration["App:GraphMode"] != "Live" || string.IsNullOrWhiteSpace(configuration["LivePreparation:ApprovalReference"]) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host != $"{configuration["LivePreparation:VaultName"]}.vault.azure.net" ||
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["keys", var name, var version] ||
            !name.StartsWith("graph-synthetic-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("Notification preparation requires the explicit versioned synthetic key and recorded target approval.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = configuration["AzureClientId"] });
        var cryptography = new CryptographyClient(uri, credential);
        using var rsa = cryptography.CreateRSA(deadline.Token);
        var key = await new Azure.Security.KeyVault.Keys.KeyClient(new Uri($"https://{uri.Host}/"), credential)
            .GetKeyAsync(name, version, deadline.Token);
        var starts = key.Value.Properties.CreatedOn?.AddMinutes(-5) ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var expires = key.Value.Properties.ExpiresOn ?? starts.AddDays(30);
        if (expires <= DateTimeOffset.UtcNow.AddHours(1)) throw new InvalidOperationException("The notification key expires too soon.");
        var subject = new X500DistinguishedName("CN=Teams Reference Synthetic Notifications");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
        var serial = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))[..16];
        serial[0] &= 0x7f;
        using var certificate = request.Create(subject, X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1), starts, expires, serial);
        var plaintext = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var publicKey = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("Notification certificate has no RSA key.");
            var encrypted = publicKey.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
            var decrypted = await cryptography.DecryptAsync(EncryptionAlgorithm.RsaOaep, encrypted, deadline.Token);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(plaintext, decrypted.Plaintext))
                    throw new InvalidOperationException("The notification certificate does not match its Key Vault private key.");
            }
            finally { CryptographicOperations.ZeroMemory(decrypted.Plaintext); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Print(new { operation = "PrepareNotification", certificateId = $"synthetic-{version}",
            certificate = Convert.ToBase64String(certificate.Export(X509ContentType.Cert)), keyUri = uri.AbsoluteUri,
            expires, remoteKeyRoundTrip = true, privateKeyExported = false });
    }
    public static async Task RunAsync(IServiceProvider services, IConfiguration configuration, LiveOptions options,
        string tenant, string operation)
    {
        var serviceInspect = options.EntitlementProvider == "Graph" && configuration["App:Role"] == "Control" && operation == "Inspect";
        var projectionCheck = options.EntitlementProvider == "Graph" && configuration["App:Role"] == "Api" && operation == "ProjectionCheck";
        var sessionCheck = options.EntitlementProvider == "Graph" && configuration["App:Role"] == "Api" && operation == "SessionCheck";
        if (!serviceInspect && !projectionCheck && !sessionCheck && (!options.DelegatedSynthetic || configuration["App:Role"] != "Api"))
            throw new InvalidOperationException("Inspect requires Control, ProjectionCheck/SessionCheck require Api, and legacy write probes require their explicit bounded API profile.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILiveStateStore>();
        if (projectionCheck)
        {
            var proof = await ProjectionChecks.RunAsync(store, options, tenant, services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ILogger<LiveDiscovery>>(), ct, services.GetRequiredService<IResourceRegistry>());
            Print(new { operation, proof, evidence = "API-identity projected data access, not browser authentication or delegated sends",
                sourceWrites = 0, messageBodiesLogged = false });
            return;
        }
        if (operation == "Inspect")
        {
            var results = new List<object>();
            var registry = services.GetRequiredService<IResourceRegistry>();
            foreach (var registration in await registry.ListAsync(ct))
            {
                var resource = registration.Resource;
                var item = await store.GetAsync(tenant, resource.Id, ct);
                var control = services.GetRequiredService<LiveControlStore>();
                var root = await control.RepairAsync(resource.Id, null, ct);
                var repairs = await control.UnresolvedRepairsAsync(resource.Id, ct);
                var coverageCheckpoint = await store.ReadCoverageAsync(tenant, resource.Id, ct);
                bool? channelMetadataEligible = null;
                bool? channelMetadataListed = null;
                string? channelMetadataError = null;
                if (serviceInspect && resource.Kind == "channel" && options.Resources.Any(seed => seed.Id == resource.Id))
                    try
                    {
                        var metadata = await services.GetRequiredService<IGraphPolicyReader>().ReadChannelAsync(resource, ct);
                        channelMetadataEligible = ChannelEligibility.Supported(metadata);
                        var page = await services.GetRequiredService<GraphBackground>()
                            .GetChannelMetadataAsync(resource.GraphTeamId!, null, null, ct);
                        if (!page.TryGetProperty("value", out var channels) || channels.ValueKind != JsonValueKind.Array ||
                            channels.GetArrayLength() > 1000)
                            throw new AppError(503, "channel_source_invalid", "The channel metadata check returned an invalid page.");
                        channelMetadataListed = channels.EnumerateArray().Any(channel =>
                            ChannelEligibility.Supported(channel) && channel.TryGetProperty("id", out var id) &&
                            id.ValueKind == JsonValueKind.String && id.GetString() == resource.GraphChannelId);
                    }
                    catch (AppError error) { channelMetadataError = error.Code; }
                var members = 0;
                var denials = new List<string>();
                if (item is not null)
                    foreach (var actor in serviceInspect ? options.GraphPolicy!.AdmittedUserIds : options.SyntheticActorIds)
                    {
                        try { await store.GetMemberAsync(new(tenant, actor, ""), resource.Id, false, ct); members++; }
                        catch (AppError error) when (error.Status == 404 || error.Code == "authorization_stale")
                        { denials.Add(error.Code); }
                    }
                results.Add(new { resourceId = resource.Id, exists = item is not null, revision = item?.Value.Revision,
                    publishedRevision = item?.Value.PublishedRevision,
                    coverage = item?.Value.CoverageReady, coverageUntil = item?.Value.CoverageUntil,
                    coverageCheckpoint = coverageCheckpoint is null ? null : new { coverageCheckpoint.Reason,
                        coverageCheckpoint.Ready, coverageCheckpoint.VerifiedAt, coverageCheckpoint.FreshUntil,
                        coverageCheckpoint.RepairGeneration },
                    repairGeneration = item?.Value.RepairGeneration, freshMembers = members, denials,
                    channelMetadata = channelMetadataEligible is null && channelMetadataError is null ? null :
                        new { api = "beta", eligible = channelMetadataEligible, listedInFirstPage = channelMetadataListed,
                            error = channelMetadataError, sourceWrites = 0, messageBodiesRead = 0 },
                    rootRepair = root is null ? null : new { status = root.Value.Status, root.Value.Generation,
                        root.Value.LastProgressAt, root.Value.Failure, root.Value.Recovery },
                    unresolvedRepairs = repairs.Count,
                    repairStates = repairs.Select(repair => new { checkpoint = LiveControlStore.RepairId(repair.ParentId),
                        stage = repair.Verifying ? "verification" : "hydration",
                        status = repair.Status, repair.Generation, repair.LastProgressAt, repair.Failure, repair.Recovery }) });
            }
            var subscriptions = new List<object>();
            foreach (var subscriptionScope in await registry.ScopesAsync(ct))
            {
                var subscription = await services.GetRequiredService<LiveControlStore>().SubscriptionAsync(subscriptionScope.Id, ct);
                var lifecycle = await services.GetRequiredService<LiveControlStore>().LifecycleAsync(subscriptionScope.Id, ct);
                JsonElement? remote = null;
                string? remoteReadError = null;
                if (subscription?.Value.GraphId is { } graphId)
                    try
                    {
                        remote = await services.GetRequiredService<IGraphBackground>().ForResource(subscriptionScope.Path)
                            .GetAsync($"subscriptions/{Uri.EscapeDataString(graphId)}", ct);
                    }
                    catch (AppError error) when (error.Code == "graph_404") { }
                    catch (AppError error) { remoteReadError = error.Code; }
                subscriptions.Add(new { scopeId = subscriptionScope.Id, registered = subscription?.Value.GraphId is not null,
                    lifecycle = lifecycle is null ? null : new { lifecycle.Value.Action, lifecycle.Value.At, lifecycle.Value.Handled },
                    expires = subscription?.Value.Expires, creationUnresolved = subscription?.Value is { GraphId: null, CreateAttempted: true },
                    remoteExists = remoteReadError is null ? remote is not null : (bool?)null, remoteReadError,
                    remoteRich = remote is { } r && r.TryGetProperty("includeResourceData", out var rich) ? rich.GetBoolean() : (bool?)null,
                    remoteResourceMatches = remote is { } p && p.TryGetProperty("resource", out var path) &&
                        GraphMessages.NormalizeResource(path.GetString() ?? "") == subscriptionScope.Path,
                    remoteNotificationMatches = remote is { } n && n.TryGetProperty("notificationUrl", out var url) &&
                        url.GetString() == options.NotificationUrl,
                    remoteLifecycleMatches = remote is { } l && l.TryGetProperty("lifecycleNotificationUrl", out var life) &&
                        life.GetString() == options.LifecycleUrl,
                    remoteClientStateMatches = remote is { } s && s.TryGetProperty("clientState", out var state) &&
                        state.GetString() == subscription?.Value.ClientState });
            }
            Print(new { operation, resources = results, subscriptions });
            return;
        }
        if (operation == "Reauthorize")
        {
            var control = services.GetRequiredService<LiveControlStore>();
            var subscriptionGraph = services.GetRequiredService<IGraphBackground>();
            var recovered = new List<object>();
            foreach (var subscriptionScope in options.SubscriptionScopes)
            {
                var subscription = await control.SubscriptionAsync(subscriptionScope.Id, ct);
                if (subscription?.Value.GraphId is not { } id || subscription.Value.Expires <= DateTimeOffset.UtcNow)
                    throw new AppError(503, "subscription_unavailable", "A current registered subscription is required for reauthorization.");
                await subscriptionGraph.ForResource(subscriptionScope.Path).PostAsync($"subscriptions/{Uri.EscapeDataString(id)}/reauthorize", new { }, ct);
                foreach (var resource in options.Resources.Where(subscriptionScope.Covers))
                {
                    await store.SetCoverageAsync(new(tenant, resource.Id, false, null, null, "reauthorized_after_gap"), ct);
                    await control.EnqueueRepairAsync(resource, null, null, ct);
                }
                recovered.Add(new { scopeId = subscriptionScope.Id, reauthorized = true, gapRepairScheduled = true });
            }
            Print(new { operation, scopes = recovered });
            return;
        }
        if (operation == "SessionCheck")
        {
            var name = configuration["LiveDiagnostics:Container"] ?? throw new InvalidOperationException("Select the synthetic validation container.");
            SessionReadProbe.ValidateContainerName(name);
            var first = services.GetRequiredService<CosmosClient>();
            var account = await first.ReadAccountAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
            options.ValidateStorageProfile(account.Consistency.DefaultConsistencyLevel.ToString(),
                account.ReadableRegions.Count(), account.WritableRegions.Count());
            TokenCredential credential = options.EntitlementProvider == "Graph"
                ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(configuration["AzureClientId"]!))
                : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = configuration["AzureClientId"] });
            using var other = new CosmosClient(configuration["Azure:CosmosEndpoint"],
                credential,
                new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, ConsistencyLevel = ConsistencyLevel.Session,
                    SerializerOptions = new() { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase }, RequestTimeout = TimeSpan.FromSeconds(10) });
            var proof = await SessionReadProbe.RunAsync(first, other, name, ct);
            Print(new { operation, proof });
            return;
        }
        if (operation == "RealtimeCheck")
        {
            var actor = configuration["LiveDiagnostics:ActorId"];
            if (actor is null || !options.SyntheticActorIds.Contains(actor))
                throw new InvalidOperationException("Select an approved synthetic realtime actor.");
            var user = AzureRealtime.User(tenant, actor);
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("json.reliable.webpubsub.azure.v1");
            socket.Options.SetRequestHeader("Origin", configuration["App:PublicOrigin"]);
            socket.Options.CollectHttpResponseDetails = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var client = services.GetRequiredService<Azure.Messaging.WebPubSub.WebPubSubServiceClient>();
                var uri = await client.GetClientAccessUriAsync(TimeSpan.FromMinutes(1), user, roles: [], cancellationToken: timeout.Token);
                await socket.ConnectAsync(uri, timeout.Token);
                var bytes = new byte[4096];
                var count = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    if (count == bytes.Length) throw new InvalidOperationException("The realtime admission frame exceeds its bound.");
                    received = await socket.ReceiveAsync(bytes.AsMemory(count), timeout.Token);
                    if (received.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("The realtime admission frame is not JSON text.");
                    count += received.Count;
                } while (!received.EndOfMessage);
                using var frame = JsonDocument.Parse(bytes.AsMemory(0, count));
                if (frame.RootElement.GetProperty("type").GetString() != "system" ||
                    frame.RootElement.GetProperty("event").GetString() != "connected" ||
                    frame.RootElement.GetProperty("userId").GetString() != user)
                    throw new InvalidOperationException("The realtime connection did not confirm the approved actor.");
                Print(new { operation, connected = true, protocol = socket.SubProtocol, actorConfirmed = true });
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Validation complete", timeout.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                Print(new { operation, connected = false, httpStatus = (int)socket.HttpStatusCode, error = error.GetType().Name });
                await HoldLogsAsync(configuration);
                throw;
            }
            return;
        }
        if (operation == "EnsurePairs")
        {
            if (!DateTimeOffset.TryParse(configuration["LiveDiagnostics:DirectChatsNotBefore"], out var cutoff) ||
                cutoff < options.SyntheticNotBefore || cutoff > DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Pair preparation requires the existing approved direct-chat history cutoff.");
            var contexts = services.GetRequiredService<SyntheticUserContexts>();
            var pairGraph = services.GetRequiredService<DelegatedSyntheticGraph>();
            if (options.DirectChats.Length != 3)
                throw new InvalidOperationException("Supply the three approved existing direct-chat mappings in Live configuration. This operation never creates Teams resources.");
            var resources = new List<object>();
            for (var first = 0; first < options.SyntheticActorIds.Length; first++)
                for (var second = first + 1; second < options.SyntheticActorIds.Length; second++)
                {
                    var firstId = options.SyntheticActorIds[first];
                    var recipient = options.SyntheticActorIds[second];
                    var pairPrincipal = await contexts.ReadAsync(firstId, ct);
                    var other = await contexts.ReadAsync(recipient, ct);
                    var pair = options.DirectChats.Single(mapping =>
                        new[] { mapping.FirstActorId, mapping.SecondActorId }.Order(StringComparer.Ordinal)
                            .SequenceEqual(new[] { firstId, recipient }.Order(StringComparer.Ordinal)));
                    var resource = options.Resources.Single(value => value.Id == pair.ResourceId);
                    var root = $"chats/{Uri.EscapeDataString(resource.GraphChatId!)}";
                    var metadata = await pairGraph.ReadAsAsync(firstId, resource, root, ct);
                    var members = await pairGraph.ReadAsAsync(firstId, resource, $"{root}/members", ct);
                    if (metadata.GetProperty("chatType").GetString() != "oneOnOne" ||
                        members.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind != JsonValueKind.Null ||
                        !members.GetProperty("value").EnumerateArray().Select(member => member.GetProperty("userId").GetString())
                            .Order(StringComparer.Ordinal).SequenceEqual(new[] { firstId, recipient }.Order(StringComparer.Ordinal)))
                        throw new InvalidOperationException("The approved direct chat no longer has the exact expected pair.");
                    resources.Add(new {
                        resource = new ApprovedResource(resource.Id, "chat",
                            $"{pairPrincipal.FindFirst("name")?.Value ?? "Test member"} and {other.FindFirst("name")?.Value ?? "Test member"}",
                            null, resource.GraphChatId, null, null),
                        directChat = pair, owner = firstId, historyFrom = options.SyntheticHistoryFrom[resource.Id]
                    });
                }
            Print(new { operation, resources, messagesSent = 0 });
            return;
        }
        if (operation is not ("Probe" or "VerifyProbe")) throw new InvalidOperationException("Unsupported synthetic diagnostic operation.");
        if (!Guid.TryParse(configuration["LiveDiagnostics:RunId"], out var run) ||
            configuration["LiveDiagnostics:ActorId"] is not { } actorId || !options.SyntheticActorIds.Contains(actorId))
            throw new InvalidOperationException("External-source validation requires an explicit run ID and approved actor.");
        var graph = services.GetRequiredService<DelegatedSyntheticGraph>();
        var principal = await services.GetRequiredService<SyntheticUserContexts>().ReadAsync(actorId, ct);
        var actorName = principal.FindFirst("name")?.Value ?? "Test actor";
        var actorValue = new Actor(tenant, actorId, actorName);
        var resourceIds = configuration.GetSection("LiveDiagnostics:ResourceIds").Get<string[]>() ?? [];
        if (resourceIds.Length != 2 || resourceIds.Distinct().Count() != 2)
            throw new InvalidOperationException("Select exactly one approved chat and one approved channel for source probes.");
        var selected = resourceIds.Select(id => options.Resources.Single(resource => resource.Id == id)).ToArray();
        if (selected.Count(resource => resource.Kind == "chat") != 1 || selected.Count(resource => resource.Kind == "channel") != 1)
            throw new InvalidOperationException("The source probe must contain one chat and one channel.");
        var proofs = new List<object>();
        foreach (var resource in selected)
        {
            await store.GetMemberAsync(actorValue, resource.Id, true, ct);
            var metadata = (await store.GetAsync(tenant, resource.Id, ct))!.Value;
            var phase = resource.Kind;
            var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{run:D}:{resource.Id}:{phase}"))[..16];
            var id = new Guid(idBytes).ToString();
            var existing = await store.GetOperationAsync(tenant, resource.Id, id, ct);
            if (operation == "VerifyProbe")
            {
                if (existing?.Value.State != OperationState.Confirmed || existing.Value.GraphMessageId is not { } sourceId)
                    throw new AppError(503, "probe_unconfirmed", "The original external Graph creation is not confirmed.");
                var receipt = await services.GetRequiredService<LiveControlStore>().ReceiptAsync(resource.Id, sourceId, null, ct);
                var source = await store.MessageAsync(tenant, resource.Id, sourceId, null, ct);
                if (receipt is null || source is null || receipt.SourceVersion != source.SourceVersion ||
                    metadata.PublishedRevision < receipt.Revision || source.ActorId != actorId)
                {
                    Print(new { operation, runId = run, resourceId = resource.Id, verified = false,
                        receiptFound = receipt is not null, projectedSourceFound = source is not null,
                        sourceVersionMatches = receipt is not null && source?.SourceVersion == receipt.SourceVersion,
                        authorMatches = source?.ActorId == actorId, metadata.PublishedRevision, receiptRevision = receipt?.Revision });
                    await HoldLogsAsync(configuration);
                    throw new AppError(503, "event_path_unverified", "Authenticated notification, projection or delivery provenance is not complete.");
                }
                proofs.Add(new { resourceId = resource.Id, messageId = sourceId, receipt.Revision,
                    receipt.ReceivedAt, receipt.ProjectedAt, publishedRevision = metadata.PublishedRevision,
                    authenticatedNotification = true, sourceVersionMatches = true });
                continue;
            }
            if (existing is not null)
            {
                if (existing.Value.State != OperationState.Confirmed)
                    throw new AppError(503, "source_probe_unknown", "The previous source probe is unresolved; no POST was replayed.");
                proofs.Add(new { resourceId = resource.Id, operationId = id, messageId = existing.Value.GraphMessageId, reused = true });
                continue;
            }
            var body = $"Azure event-driven validation {run:D} ({phase})";
            var command = new SendOperation(id, tenant, resource.Id, actorId,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "", null, OperationState.Pending,
                DateTimeOffset.UtcNow, Epoch: options.WriterEpoch);
            if (!await store.CreateOperationAsync(command, ct)) throw new AppError(409, "probe_conflict", "The source probe already exists.");
            var pending = (await store.GetOperationAsync(tenant, resource.Id, id, ct))!;
            await services.GetRequiredService<ICommandBudget>().AcquireAsync(actorValue, metadata, ct);
            if (!await store.SaveOperationAsync(pending with { Value = command with { State = OperationState.Dispatching } }, ct))
                throw new AppError(409, "probe_conflict", "The source probe dispatch claim changed.");
            var dispatch = (await store.GetOperationAsync(tenant, resource.Id, id, ct))!;
            var transport = new GraphTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient("live-graph"),
                new ProbeTokens(graph, actorId, resource));
            WriteResult outcome;
            try { outcome = await transport.SendAsync(actorValue, metadata, command with { Body = body }, ct); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            { outcome = new(OperationState.Unknown, null, "External Graph commitment is unknown; do not replay."); }
            if (!await store.SaveOperationAsync(dispatch with { Value = dispatch.Value with { State = outcome.State,
                GraphMessageId = outcome.Message?.Id, Error = outcome.Error } }, CancellationToken.None))
                throw new AppError(503, "probe_evidence_unavailable", "Source confirmation could not be journaled; do not replay.");
            if (outcome.State != OperationState.Confirmed)
                throw new AppError(503, "source_probe_unconfirmed", "Graph did not confirm the original source probe; no retry was attempted.");
            // Deliberately no projection call: only the real Graph notification can display this source write.
            proofs.Add(new { resourceId = resource.Id, operationId = id, messageId = outcome.Message!.Id,
                sourceCreatedAt = outcome.Message.CreatedAt, reused = false });
            await Task.Delay(TimeSpan.FromMilliseconds(1400), ct);
        }
        Print(new { operation, runId = run, proofs });
    }
    private sealed class ProbeTokens(DelegatedSyntheticGraph graph, string actor, ApprovedResource resource) : IGraphAccessTokenSource
    {
        public Task<string> AcquireAsync(bool channel) => graph.TokenAsync(actor, resource, true, CancellationToken.None);
    }
}
