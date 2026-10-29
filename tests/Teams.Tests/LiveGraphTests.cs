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
            new Credential());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("chats/chat/messages", new { body = "Forbidden" }, default));
        Assert.Equal(0, calls);
    }
    [Fact]
    public async Task OnlyDedicatedReadOnlyChannelMetadataUsesBeta()
    {
        var requests = new List<(HttpMethod Method, Uri Uri)>();
        var client = new GraphBackground(new HttpClient(new Handler(request =>
        {
            requests.Add((request.Method, request.RequestUri!));
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        })), new Credential());
        await client.GetChannelMetadataAsync("team", "19:channel@thread.tacv2", null, default);
        await client.GetAsync(Resource.Path, default);
        await client.GetAsync("subscriptions", default);
        Assert.All(requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Equal("/beta/teams/team/channels/19%3Achannel%40thread.tacv2", requests[0].Uri.AbsolutePath);
        Assert.Equal("?$select=id,displayName,membershipType,isArchived,moderationSettings", requests[0].Uri.Query);
        Assert.StartsWith("/v1.0/chats/", requests[1].Uri.AbsolutePath);
        Assert.Equal("/v1.0/subscriptions", requests[2].Uri.AbsolutePath);
        await Assert.ThrowsAsync<AppError>(() => client.GetAsync(requests[0].Uri.AbsoluteUri, default));
        await Assert.ThrowsAsync<AppError>(() => client.PostAsync(requests[0].Uri.AbsoluteUri, new { }, default));
        Assert.Equal(3, requests.Count);
    }
    [Theory]
    [InlineData("http://graph.microsoft.com/beta/teams/team/channels")]
    [InlineData("https://graph.microsoft.com.evil.invalid/beta/teams/team/channels")]
    [InlineData("https://graph.microsoft.com:444/beta/teams/team/channels")]
    [InlineData("https://user@graph.microsoft.com/beta/teams/team/channels")]
    [InlineData("https://graph.microsoft.com/v1.0/teams/team/channels")]
    [InlineData("https://graph.microsoft.com/beta/teams/other/channels")]
    [InlineData("https://graph.microsoft.com/beta/teams/team/channels/channel/messages")]
    [InlineData("https://graph.microsoft.com/beta/subscriptions")]
    public void MetadataContinuationsCannotEscapeTheSelectedTeamVersionOrMetadataPath(string path) =>
        Assert.Throws<AppError>(() => GraphUrls.ChannelMetadata("team", null, path));
    [Fact]
    public void MetadataContinuationPreservesOpaqueTokensButCannotExpandContent()
    {
        var initial = GraphUrls.ChannelMetadata("team", null).AbsoluteUri;
        var next = initial + "&$skiptoken=opaque%2Btoken%3d%3d+literal";
        Assert.Equal(next, GraphUrls.ChannelMetadata("team", null, next).OriginalString);
        foreach (var invalid in new[] {
            initial + "&$expand=messages", initial + "&$select=body",
            initial.Replace("id,displayName,membershipType,isArchived,moderationSettings", "id,body", StringComparison.Ordinal),
            initial + "&$top=1001", initial + "#fragment"
        }) Assert.Throws<AppError>(() => GraphUrls.ChannelMetadata("team", null, invalid));
        Assert.Throws<AppError>(() => GraphUrls.ChannelMetadata("..", null));
        Assert.Throws<AppError>(() => GraphUrls.ChannelMetadata("team", ".."));
    }
    [Fact]
    public async Task ThrottledGraphAttemptHonorsRetryBudgetWithoutSharedCooldown()
    {
        var calls = 0;
        var client = new GraphBackground(new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return response;
        })), new Credential());
        Assert.Equal(429, (await Assert.ThrowsAsync<AppError>(() => client.GetAsync(Resource.Path, default))).Status);
        Assert.Equal(4, calls);
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
    public void FormattedAndAttachmentContentIsExplicitAndSourceLinksCannotLeaveTeams()
    {
        var formatted = GraphMessages.Parse(Json("""
            {"id":"one","createdDateTime":"2026-10-19T10:00:00Z",
             "body":{"content":"<p>Hello <b>world</b></p>","contentType":"html"},
             "webUrl":"https://teams.microsoft.com/l/message/approved","attachments":[{"id":"file"}]}
            """));
        Assert.Equal("Hello world\n", formatted.Body);
        Assert.Contains("Attachments", formatted.ContentNotice);
        Assert.Equal("https://teams.microsoft.com/l/message/approved", formatted.TeamsUrl);
        var unsafeLink = GraphMessages.Parse(Json("""
            {"id":"two","createdDateTime":"2026-10-19T10:00:00Z",
             "body":{"content":"Text","contentType":"text"},"webUrl":"https://teams.microsoft.com.evil.invalid/"}
            """));
        Assert.Null(unsafeLink.TeamsUrl);
        Assert.Null(unsafeLink.ContentNotice);
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
        var missingTokens = await Assert.ThrowsAsync<AppError>(() => intake.AcceptAsync(Json("""{"value":[{}]}"""), false, default));
        Assert.Equal("notification_missing_validationTokens", missingTokens.Code);
        Assert.Empty(inbox.Items);
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
    public async Task OnlySubscriptionReauthorizationAllowsAnEmptySuccessfulEntity()
    {
        using var empty = new StringContent("");
        await Assert.ThrowsAnyAsync<JsonException>(() => BoundedJson.ReadAsync(empty, 1024, default));
        using var control = new StringContent("");
        Assert.Equal(JsonValueKind.Object, (await BoundedJson.ReadAsync(control, 1024, default, allowEmpty: true)).ValueKind);
        var path = GraphUrls.Validate("subscriptions/approved/reauthorize");
        Assert.True(GraphUrls.IsReauthorization(HttpMethod.Post, path));
        Assert.False(GraphUrls.IsReauthorization(HttpMethod.Get, path));
        Assert.False(GraphUrls.IsReauthorization(HttpMethod.Post, GraphUrls.Validate("subscriptions")));
        Assert.False(GraphUrls.IsReauthorization(HttpMethod.Post, GraphUrls.Validate("chats/approved/messages")));
    }
    [Fact]
    public async Task LifecycleWithoutTenantIsBoundToTheRegisteredTenantAndSecretState()
    {
        var clock = new Clock();
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        var subscription = new SubscriptionState("all", "chats/getAllMessages", "test-state", "subscription",
            clock.GetUtcNow().AddMinutes(50), "cert", true, "owner", clock.GetUtcNow());
        await control.SaveSubscriptionAsync(subscription, null, default);
        await control.RegisterLookupAsync(subscription, default);
        var inbox = new Inbox();
        var intake = new NotificationIntake(Options(), Tenant, control, new Tokens(), inbox, clock);
        await intake.AcceptAsync(Json("""
            {"value":[{"subscriptionId":"subscription","clientState":"test-state","lifecycleEvent":"reauthorizationRequired"}]}
            """), true, default);
        Assert.Equal(Tenant, Assert.Single(inbox.Items).TenantId);
        Assert.Equal("reauthorizationRequired", inbox.Items[0].LifecycleEvent);
        foreach (var json in new[] {
            """{"value":[{"subscriptionId":"subscription","clientState":"wrong","lifecycleEvent":"reauthorizationRequired"}]}""",
            """{"value":[{"subscriptionId":"subscription","clientState":"test-state","tenantId":"other","lifecycleEvent":"reauthorizationRequired"}]}""",
            """{"value":[{"subscriptionId":"unknown","clientState":"test-state","lifecycleEvent":"reauthorizationRequired"}]}"""
        }) await Assert.ThrowsAsync<AppError>(() => intake.AcceptAsync(Json(json), true, default));
        Assert.Single(inbox.Items);
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
    public async Task SubscriptionDefinitionsPreserveTeamsIdentifierPunctuationAndCanonicalStoredScope()
    {
        var resource = Resource with { GraphChatId = "19:approved@thread.v2" };
        var options = Options();
        options.Resources = [resource];
        options.SubscriptionScopes = [new("chat-messages", resource.Path)];
        var clock = new Clock();
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        var graph = new FakeGraph
        {
            Post = (_, body) =>
            {
                var definition = JsonSerializer.SerializeToElement(body);
                Assert.Equal("chats/19:approved@thread.v2/messages", definition.GetProperty("resource").GetString());
                return JsonSerializer.SerializeToElement(new { id = "confirmed", expirationDateTime = clock.GetUtcNow().AddMinutes(50) });
            }
        };
        await new SubscriptionController(graph, control, options, clock).StepAsync(options.SubscriptionScopes[0], default);
        Assert.Equal(resource.Path, (await control.SubscriptionAsync("chat-messages", default))!.Value.Resource);
        Assert.Equal(resource.Path, GraphMessages.NormalizeResource(GraphUrls.SubscriptionResource(resource.Path)));
        Assert.Contains("%2F", GraphUrls.SubscriptionResource("chats/escaped%2Fsegment/messages"));
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
    public async Task MissingSubscriptionDuringReauthorizationIsReplacedOnceWithFreshCorrelation()
    {
        var clock = new Clock();
        var options = Options();
        var scope = options.SubscriptionScopes[0];
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        await control.SaveSubscriptionAsync(new(scope.Id, scope.Path, "old-state", "expired",
            clock.GetUtcNow().AddMinutes(-1), "cert", true, "", DateTimeOffset.MinValue), null, default);
        await control.SaveLifecycleAsync(scope.Id, new("reauthorizationRequired", clock.GetUtcNow()), null, default);
        var creates = 0;
        var graph = new FakeGraph { Patch = (_, _) => throw new AppError(404, "graph_404", "The original subscription is gone."),
            Post = (path, _) =>
        {
            Assert.Equal("subscriptions", path);
            creates++;
            return JsonSerializer.SerializeToElement(new { id = "replacement", expirationDateTime = clock.GetUtcNow().AddMinutes(50) });
        } };
        var controller = new SubscriptionController(graph, control, options, clock);
        await controller.StepAsync(scope, default);
        var current = (await control.SubscriptionAsync(scope.Id, default))!.Value;
        Assert.Equal("replacement", current.GraphId);
        Assert.NotEqual("old-state", current.ClientState);
        Assert.True((await control.LifecycleAsync(scope.Id, default))!.Value.Handled);
        Assert.Equal(scope.Path, (await control.FindSubscriptionAsync("replacement", default))!.Resource);
        await controller.StepAsync(scope, default);
        Assert.Equal(1, creates);
        Assert.Equal(1, graph.Posts);
        Assert.Equal(1, graph.Patches);
    }
    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    public async Task ReauthorizationAndRenewalUseOnePatchWithoutAConcurrentPost(int remainingMinutes)
    {
        var clock = new Clock();
        var options = Options();
        var scope = options.SubscriptionScopes[0];
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        await control.SaveSubscriptionAsync(new(scope.Id, scope.Path, "state", "original",
            clock.GetUtcNow().AddMinutes(remainingMinutes), "cert", true, "", DateTimeOffset.MinValue), null, default);
        await control.SaveLifecycleAsync(scope.Id, new("reauthorizationRequired", clock.GetUtcNow()), null, default);
        var graph = new FakeGraph { Patch = (path, body) =>
        {
            Assert.Equal("subscriptions/original", path);
            Assert.Equal(clock.GetUtcNow().AddMinutes(50),
                JsonSerializer.SerializeToElement(body).GetProperty("expirationDateTime").GetDateTimeOffset());
            return JsonSerializer.SerializeToElement(new { id = "original", expirationDateTime = clock.GetUtcNow().AddMinutes(50) });
        } };
        var controller = new SubscriptionController(graph, control, options, clock);
        await controller.StepAsync(scope, default);
        await controller.StepAsync(scope, default);
        Assert.Equal(0, graph.Posts);
        Assert.Equal(1, graph.Patches);
        Assert.True((await control.LifecycleAsync(scope.Id, default))!.Value.Handled);
        Assert.Equal(clock.GetUtcNow().AddMinutes(50), (await control.SubscriptionAsync(scope.Id, default))!.Value.Expires);
        Assert.Equal(scope.Path, (await control.FindSubscriptionAsync("original", default))!.Resource);
    }
    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task OtherReauthorizationFailuresDoNotReplaceOrAcknowledgeTheOriginalSubscription(int status)
    {
        var clock = new Clock();
        var options = Options();
        var scope = options.SubscriptionScopes[0];
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        await control.SaveSubscriptionAsync(new(scope.Id, scope.Path, "old-state", "original",
            clock.GetUtcNow().AddMinutes(-1), "cert", true, "", DateTimeOffset.MinValue), null, default);
        await control.SaveLifecycleAsync(scope.Id, new("reauthorizationRequired", clock.GetUtcNow()), null, default);
        var graph = new FakeGraph { Patch = (path, _) =>
        {
            Assert.Equal("subscriptions/original", path);
            throw new AppError(status, $"graph_{status}", "Reauthorization was not confirmed.");
        } };
        var error = await Assert.ThrowsAsync<AppError>(() =>
            new SubscriptionController(graph, control, options, clock).StepAsync(scope, default));
        Assert.Equal(status, error.Status);
        Assert.Equal("original", (await control.SubscriptionAsync(scope.Id, default))!.Value.GraphId);
        Assert.False((await control.LifecycleAsync(scope.Id, default))!.Value.Handled);
        Assert.Equal(0, graph.Posts);
        Assert.Equal(1, graph.Patches);
    }
    [Fact]
    public async Task LostReplacementResponseRecoversTheSameIntentWithoutAnotherCreate()
    {
        var clock = new Clock();
        var options = Options();
        var scope = options.SubscriptionScopes[0];
        var control = new LiveControlStore(new MemoryPartitionDocuments(), clock, Tenant);
        await control.SaveSubscriptionAsync(new(scope.Id, scope.Path, "old-state", "expired",
            clock.GetUtcNow().AddMinutes(-1), "cert", true, "", DateTimeOffset.MinValue), null, default);
        await control.SaveLifecycleAsync(scope.Id, new("reauthorizationRequired", clock.GetUtcNow()), null, default);
        var graph = new FakeGraph { Patch = (_, _) => throw new AppError(404, "graph_404", "The original subscription is gone."),
            Post = (path, _) =>
        {
            Assert.Equal("subscriptions", path);
            throw new HttpRequestException("The replacement response was lost.");
        } };
        var controller = new SubscriptionController(graph, control, options, clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.StepAsync(scope, default));
        var intent = (await control.SubscriptionAsync(scope.Id, default))!.Value;
        Assert.Null(intent.GraphId);
        Assert.True(intent.CreateAttempted);
        Assert.True((await control.LifecycleAsync(scope.Id, default))!.Value.Handled);
        graph.Get = _ => JsonSerializer.SerializeToElement(new { value = new[] { new
        {
            id = "replacement", resource = scope.Path, clientState = intent.ClientState,
            expirationDateTime = clock.GetUtcNow().AddMinutes(50)
        } } });
        await controller.StepAsync(scope, default);
        Assert.Equal("replacement", (await control.SubscriptionAsync(scope.Id, default))!.Value.GraphId);
        Assert.Equal(1, graph.Posts);
        Assert.Equal(1, graph.Patches);
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
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
    private sealed class FakeGraph : IGraphBackground
    {
        public Func<string, JsonElement> Get { get; set; } = _ => throw new InvalidOperationException("Unexpected GET");
        public Func<string, object, JsonElement> Post { get; set; } = (_, _) => throw new InvalidOperationException("Unexpected POST");
        public Func<string, object, JsonElement> Patch { get; set; } = (_, _) => throw new InvalidOperationException("Unexpected PATCH");
        public int Posts { get; private set; }
        public int Patches { get; private set; }
        public Task<JsonElement> GetAsync(string path, CancellationToken ct) => Task.FromResult(Get(path));
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) { Posts++; return Task.FromResult(Post(path, body)); }
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) { Patches++; return Task.FromResult(Patch(path, body)); }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 12, 1, 0, TimeSpan.Zero);
    }
}
