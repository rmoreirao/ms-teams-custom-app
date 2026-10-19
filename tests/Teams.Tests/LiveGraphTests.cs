using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class LiveGraphTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly ApprovedResource Resource = new("chat", "chat", "Approved", null, "graph-chat", null, null);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static string MessageJson => """{"id":"one","chatId":"graph-chat","createdDateTime":"2026-10-16T12:00:00Z","lastModifiedDateTime":"2026-10-16T12:00:00Z","body":{"content":"<p>Hello<br>world</p>","contentType":"html"}}""";
    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/chats/a/messages")]
    [InlineData("https://graph.microsoft.com.evil.invalid/v1.0/chats/a/messages")]
    [InlineData("https://graph.microsoft.com/beta/chats/a/messages")]
    [InlineData("https://user@graph.microsoft.com/v1.0/chats/a/messages")]
    [InlineData("https://graph.microsoft.com/v1.0/chats/b/messages")]
    public void ContinuationsCannotEscapeAuthorityVersionOrResource(string path) =>
        Assert.Throws<AppError>(() => GraphUrls.Validate(path, "chats/a/messages"));
    [Fact]
    public void OpaqueContinuationQueryIsPreserved() =>
        Assert.Equal("https://graph.microsoft.com/v1.0/chats/a/messages?$skiptoken=opaque%2Btoken",
            GraphUrls.Validate("https://graph.microsoft.com/v1.0/chats/a/messages?$skiptoken=opaque%2Btoken", "chats/a/messages").AbsoluteUri);
    [Fact]
    public void AllMessageAndReplyReadsShareTheirConversationBudget()
    {
        Assert.Equal(GraphUrls.BudgetResource(GraphUrls.Validate("chats/chat/messages")),
            GraphUrls.BudgetResource(GraphUrls.Validate("chats/chat/messages/message")));
        Assert.Equal(GraphUrls.BudgetResource(GraphUrls.Validate("teams/team/channels/channel/messages")),
            GraphUrls.BudgetResource(GraphUrls.Validate("teams/team/channels/channel/messages/root/replies/reply")));
    }
    [Fact]
    public void EncodedAndRawODataIdsNormalizeToTheSameApprovedPath()
    {
        var expected = new ApprovedResource("channel", "channel", "Channel", "Team", null, "team",
            "19:channel@thread.tacv2").Path + "/one";
        Assert.Equal(expected, GraphMessages.NormalizeResource("teams('team')/channels('19:channel@thread.tacv2')/messages('one')"));
        Assert.Equal(expected, GraphMessages.NormalizeResource("teams('team')/channels('19%3Achannel%40thread.tacv2')/messages('one')"));
        var deletion = GraphMessages.Parse(Json("""{"id":"one","createdDateTime":"2026-10-16T12:00:00Z","deletedDateTime":"2026-10-16T13:00:00Z"}"""));
        Assert.True(deletion.Deleted);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 13, 0, 0, TimeSpan.Zero), deletion.ModifiedAt);
    }
    [Fact]
    public async Task StaleSubscriptionRemovalDoesNotResetItsReplacement()
    {
        var clock = new Clock();
        var documents = new MemoryPartitionDocuments();
        var control = new LiveControlStore(documents, clock, Tenant);
        await control.SaveSubscriptionAsync(new("all", "chats/getAllMessages", "new-state", "new-subscription",
            clock.GetUtcNow().AddMinutes(50), "cert", true, "owner", clock.GetUtcNow()), null, default);
        var store = new LiveStateStore(documents, clock);
        var projector = new NotificationProjector(Options(), Tenant, store, control,
            new(new Keys(RandomNumberGenerator.GetBytes(32))), new Inbox());
        await projector.ApplyAsync(new(Tenant, "scope:all", "chats/getAllMessages", "", null, clock.GetUtcNow(),
            "subscriptionRemoved", SubscriptionId: "old-subscription"), default);
        Assert.Null(await control.LifecycleAsync("all", default));
    }
    [Fact]
    public async Task BackgroundClientCannotPostUserMessages()
    {
        var calls = 0;
        var client = new GraphBackground(new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK); })),
            new Credential(), new Budget());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("chats/chat/messages", new { body = "Forbidden" }, default));
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task ThrottledGraphAttemptSetsSharedCooldownWithoutAutomaticRetry()
    {
        var calls = 0;
        var budget = new Budget();
        var client = new GraphBackground(new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
            return response;
        })), new Credential(), budget);
        Assert.Equal(429, (await Assert.ThrowsAsync<AppError>(() => client.GetAsync(Resource.Path, default))).Status);
        Assert.Equal(1, calls);
        Assert.Equal(TimeSpan.FromSeconds(17), budget.Cooldown);
    }
    [Theory]
    [InlineData("1.0", "appid")]
    [InlineData("2.0", "azp")]
    public async Task SignedNotificationRequiresCorrectTenantAudienceAndPublisher(string version, string callerClaim)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(key);
        var validator = new NotificationTokenValidator(Tenant, "client",
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
        string Token(string publisher) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            version == "1.0" ? $"https://sts.windows.net/{Tenant}/" : $"https://login.microsoftonline.com/{Tenant}/v2.0",
            "client", [new("tid", Tenant), new("ver", version), new(callerClaim, publisher)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new(key, SecurityAlgorithms.RsaSha256)));
        await validator.ValidateAsync([Token("0bf30f3b-4a52-48df-9a82-234910c4a086")], default);
        await Assert.ThrowsAsync<SecurityTokenValidationException>(() => validator.ValidateAsync([Token("untrusted")], default));
    }
    [Fact]
    public async Task RichPayloadIsAuthenticatedBeforeDecryptionAndKeyIsCleared()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var fake = new Keys(key);
        var encrypted = Encrypt(MessageJson, key);
        var decryptor = new NotificationDecryptor(fake);
        var message = GraphMessages.Parse(await decryptor.DecryptAsync(encrypted, default));
        Assert.Equal("Hello\nworld\n", message.Body);
        Assert.All(fake.LastReturned!, b => Assert.Equal(0, b));
        var modified = JsonSerializer.Deserialize<Dictionary<string, string>>(encrypted.GetRawText())!;
        modified["dataSignature"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<CryptographicException>(() => decryptor.DecryptAsync(JsonSerializer.SerializeToElement(modified), default));
    }
    [Fact]
    public async Task IntakeFiltersUnapprovedResourcesAndNeverPartiallyEnqueuesInvalidCollections()
    {
        var clock = new Clock();
        var documents = new MemoryPartitionDocuments();
        var control = new LiveControlStore(documents, clock, Tenant);
        var subscription = new SubscriptionState("all", "chats/getAllMessages", "test-state", "subscription",
            clock.GetUtcNow().AddMinutes(50), "cert", true, "owner", clock.GetUtcNow());
        await control.SaveSubscriptionAsync(subscription, null, default);
        await control.RegisterLookupAsync(subscription, default);
        var inbox = new Inbox();
        var options = Options();
        var intake = new NotificationIntake(options, Tenant, control, new Tokens(), inbox, clock);
        object Item(string resource, string state = "test-state") => new
        {
            subscriptionId = "subscription", tenantId = Tenant, clientState = state, resource,
            changeType = "created", encryptedContent = new { encryptionCertificateId = "cert" }
        };
        await intake.AcceptAsync(JsonSerializer.SerializeToElement(new
        {
            validationTokens = new[] { "test" }, value = new[] { Item("chats('outside')/messages('one')") }
        }), false, default);
        Assert.Empty(inbox.Items);
        await Assert.ThrowsAsync<AppError>(() => intake.AcceptAsync(JsonSerializer.SerializeToElement(new
        {
            validationTokens = new[] { "test" }, value = new[] { Item("chats('graph-chat')/messages('one')"), Item("chats('graph-chat')/messages('two')", "bad") }
        }), false, default));
        Assert.Empty(inbox.Items);
        await intake.AcceptAsync(JsonSerializer.SerializeToElement(new
        {
            validationTokens = new[] { "test" }, value = new[] { Item("chats('graph-chat')/messages('one')") }
        }), false, default);
        Assert.Equal("chats/graph-chat/messages/one", Assert.Single(inbox.Items).ResourcePath);
    }
    [Fact]
    public async Task ProjectorAppliesEncryptedChangeAndRejectsCrossTenantContext()
    {
        var clock = new Clock();
        var documents = new MemoryPartitionDocuments();
        var store = new LiveStateStore(documents, clock);
        await store.CreateAsync(new("chat", Tenant, "Approved", "chat", null, [], [], [], CoverageReady: false), default);
        var key = RandomNumberGenerator.GetBytes(32);
        var projector = new NotificationProjector(Options(), Tenant, store, new(documents, clock, Tenant), new(new Keys(key)));
        var envelope = new EncryptedNotification(Tenant, "chat", $"{Resource.Path}/one", "created", Encrypt(MessageJson, key), clock.GetUtcNow());
        await projector.ApplyAsync(envelope, default);
        await projector.ApplyAsync(envelope, default);
        Assert.Single(await store.PendingOutboxAsync(Tenant, default));
        await Assert.ThrowsAsync<AppError>(() => projector.ApplyAsync(envelope with { TenantId = "other" }, default));
        Assert.Equal("schema_unsupported", (await Assert.ThrowsAsync<AppError>(() =>
            projector.ApplyAsync(envelope with { SchemaVersion = 2 }, default))).Code);
    }
    [Fact]
    public async Task LostSubscriptionResponseIsRecoveredByExactStateWithoutSecondPost()
    {
        var clock = new Clock();
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        var graph = new FakeGraph
        {
            Post = (_, _) => throw new HttpRequestException("Lost response")
        };
        var controller = new SubscriptionController(graph, control, Options(), clock);
        var scope = Options().SubscriptionScopes[0];
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.StepAsync(scope, default));
        var pending = (await control.SubscriptionAsync(scope.Id, default))!;
        Assert.True(pending.Value.CreateAttempted);
        graph.Get = _ => JsonSerializer.SerializeToElement(new
        {
            value = new[] { new { id = "confirmed", resource = scope.Path, clientState = pending.Value.ClientState,
                expirationDateTime = clock.GetUtcNow().AddMinutes(50) } }
        });
        await controller.StepAsync(scope, default);
        Assert.Equal(1, graph.Posts);
        Assert.Equal("confirmed", (await control.SubscriptionAsync(scope.Id, default))!.Value.GraphId);
    }
    [Fact]
    public async Task RepairUsesPointVerificationToDiscoverMissedDeletion()
    {
        var clock = new Clock();
        var documents = new MemoryPartitionDocuments();
        var store = new LiveStateStore(documents, clock);
        await store.CreateAsync(new("chat", Tenant, "Approved", "chat", null, [], [], []), default);
        await store.ApplyAsync(Tenant, "chat", GraphMessages.Parse(Json(MessageJson)), default);
        var control = new LiveControlStore(documents, clock, Tenant);
        await control.EnqueueRepairAsync(Resource, null, null, default);
        var graph = new FakeGraph { Get = _ => Json("""{"value":[]}""") };
        var hydrator = new ResourceHydrator(graph, store, control, clock);
        await hydrator.StepAsync(Tenant, Resource, (await control.RepairAsync("chat", null, default))!.Value, default);
        graph.Get = _ => throw new AppError(503, "graph_404", "Verified not found");
        await hydrator.StepAsync(Tenant, Resource, (await control.RepairAsync("chat", null, default))!.Value, default);
        Assert.True((await store.MessageAsync(Tenant, "chat", "one", null, default))!.Deleted);
        Assert.False((await control.RepairAsync("chat", null, default))!.Value.Pending);
    }
    [Fact]
    public async Task RootRepairAlsoVerifiesPreviouslyProjectedReplies()
    {
        var clock = new Clock();
        var documents = new MemoryPartitionDocuments();
        var store = new LiveStateStore(documents, clock);
        var resource = new ApprovedResource("channel", "channel", "Channel", "Team", null, "team", "channel");
        await store.CreateAsync(new("channel", Tenant, "Channel", "channel", "Team", [], [], []), default);
        await store.ApplyAsync(Tenant, "channel", GraphMessages.Parse(Json(MessageJson), "parent"), default);
        var control = new LiveControlStore(documents, clock, Tenant);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var graph = new FakeGraph { Get = _ => Json("""{"value":[]}""") };
        var hydrator = new ResourceHydrator(graph, store, control, clock);
        await hydrator.StepAsync(Tenant, resource, (await control.RepairAsync("channel", null, default))!.Value, default);
        string? requested = null;
        graph.Get = path => { requested = path; throw new AppError(503, "graph_404", "Verified missing reply"); };
        await hydrator.StepAsync(Tenant, resource, (await control.RepairAsync("channel", null, default))!.Value, default);
        Assert.EndsWith("/messages/parent/replies/one", requested);
        Assert.True((await store.MessageAsync(Tenant, "channel", "one", "parent", default))!.Deleted);
    }
    private static LiveOptions Options() => new()
    {
        Resources = [Resource], SubscriptionScopes = [new("all", "chats/getAllMessages")],
        SynchronizationEnabled = true,
        NotificationCertificateId = "cert", NotificationCertificate = "public-test-certificate",
        NotificationKeys = new() { ["cert"] = "https://example.vault.azure.net/keys/test/version" },
        NotificationUrl = "https://example.invalid/callbacks/graph/notifications", LifecycleUrl = "https://example.invalid/callbacks/graph/lifecycle"
    };
    private static JsonElement Encrypt(string content, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var data = aes.EncryptCbc(Encoding.UTF8.GetBytes(content), key.AsSpan(0, 16), PaddingMode.PKCS7);
        return JsonSerializer.SerializeToElement(new
        {
            encryptionCertificateId = "cert", dataKey = Convert.ToBase64String([1]),
            data = Convert.ToBase64String(data), dataSignature = Convert.ToBase64String(HMACSHA256.HashData(key, data))
        });
    }
    private sealed class Keys(byte[] key) : INotificationKeyDecryptor
    {
        public byte[]? LastReturned { get; private set; }
        public Task<byte[]> DecryptAsync(string certificateId, byte[] encryptedKey, CancellationToken ct)
        {
            LastReturned = key.ToArray();
            return Task.FromResult(LastReturned);
        }
    }
    private sealed class Tokens : INotificationTokenValidator
    {
        public Task ValidateAsync(IReadOnlyList<string> tokens, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Inbox : IEncryptedNotificationInbox
    {
        public List<EncryptedNotification> Items { get; } = [];
        public Task EnqueueAsync(IReadOnlyList<EncryptedNotification> notifications, CancellationToken ct)
        {
            Items.AddRange(notifications); return Task.CompletedTask;
        }
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Budget : IGraphCallBudget
    {
        public TimeSpan? Cooldown { get; private set; }
        public Task AcquireAsync(string lane, string resource, CancellationToken ct) => Task.CompletedTask;
        public Task CooldownAsync(TimeSpan duration, CancellationToken ct) { Cooldown = duration; return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
    private sealed class FakeGraph : IGraphBackground
    {
        public Func<string, JsonElement> Get { get; set; } = _ => throw new InvalidOperationException("Unexpected GET");
        public Func<string, object, JsonElement> Post { get; set; } = (_, _) => throw new InvalidOperationException("Unexpected POST");
        public int Posts { get; private set; }
        public Task<JsonElement> GetAsync(string path, CancellationToken ct) => Task.FromResult(Get(path));
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) { Posts++; return Task.FromResult(Post(path, body)); }
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Unexpected PATCH");
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 12, 1, 0, TimeSpan.Zero);
    }
}
