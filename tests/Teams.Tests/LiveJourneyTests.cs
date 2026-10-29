using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class LiveJourneyTests
{
    [Theory]
    [InlineData("chat", false)]
    [InlineData("channel", false)]
    [InlineData("channel", true)]
    public async Task EncryptedExternalChangeTraversesIntakeProjectionOutboxAuthorizedPushAndCatchup(string kind, bool reply)
    {
        const string tenant = "test-tenant";
        var clock = new Clock();
        var documents = new MemoryPartitionDocuments();
        var store = new LiveStateStore(documents, clock);
        var actor = new Actor(tenant, "alex", "Alex");
        var resource = new ApprovedResource("approved", kind, "Approved", null,
            kind == "chat" ? "graph-chat" : null, kind == "channel" ? "team" : null, kind == "channel" ? "channel" : null);
        var options = new LiveOptions
        {
            Resources = [resource], SubscriptionScopes = [new("scope", resource.Path)], SynchronizationEnabled = true,
            NotificationKeys = new() { ["test-key"] = "https://example.vault.azure.net/keys/test/version" }
        };
        await store.CreateAsync(new(resource.Id, tenant, resource.Title, kind, null, [], [], [],
            GraphChatId: resource.GraphChatId, GraphTeamId: resource.GraphTeamId, GraphChannelId: resource.GraphChannelId,
            CoverageUntil: clock.GetUtcNow().AddMinutes(5)), default);
        await store.SetEntitlementAsync(new(tenant, resource.Id, actor.Id, actor.Name, clock.GetUtcNow().AddDays(-7),
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5), "synthetic-policy-evidence", true, true, 0), default);
        var control = new LiveControlStore(documents, clock, tenant);
        var subscription = new SubscriptionState("scope", resource.Path, "expected-state", "subscription",
            clock.GetUtcNow().AddMinutes(50), "test-key", true, "owner", clock.GetUtcNow());
        await control.SaveSubscriptionAsync(subscription, null, default);
        await control.RegisterLookupAsync(subscription, default);
        if (reply)
        {
            await store.ApplyAsync(tenant, resource.Id, new("root", "sam", "Sam", "Root",
                clock.GetUtcNow(), clock.GetUtcNow()), default);
            Assert.True(await store.SaveReadViewAsync(tenant, resource.Id, "root", null,
                new(0, true, null, clock.GetUtcNow()), default));
            foreach (var initialized in await store.PendingOutboxAsync(tenant, default))
                await store.CompleteOutboxAsync(initialized, default);
        }
        var before = (await store.GetAsync(tenant, resource.Id, default))!.Value.Revision;
        var key = RandomNumberGenerator.GetBytes(32);
        var content = JsonSerializer.Serialize(new
        {
            id = "external", createdDateTime = clock.GetUtcNow(), lastModifiedDateTime = clock.GetUtcNow(),
            body = new { contentType = "text", content = "From native Teams (synthetic dependency)" },
            from = new { user = new { id = "sam", displayName = "Sam" } }
        });
        using var aes = Aes.Create();
        aes.Key = key;
        var encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(content), key.AsSpan(0, 16), PaddingMode.PKCS7);
        var inbox = new Inbox();
        var intake = new NotificationIntake(options, tenant, control, new TestTokens(), inbox, clock);
        var path = resource.Path + (reply ? "/root/replies/external" : "/external");
        await intake.AcceptAsync(JsonSerializer.SerializeToElement(new
        {
            validationTokens = new[] { "synthetic-signed-token-boundary" },
            value = new[] { new {
                subscriptionId = "subscription", tenantId = tenant, clientState = "expected-state",
                resource = path, changeType = "created",
                encryptedContent = new {
                    encryptionCertificateId = "test-key", dataKey = Convert.ToBase64String([1]),
                    data = Convert.ToBase64String(encrypted),
                    dataSignature = Convert.ToBase64String(HMACSHA256.HashData(key, encrypted))
                }
            } }
        }), false, default);
        var notification = Assert.Single(inbox.Items);
        var projector = new NotificationProjector(options, tenant, store, control, new(new TestKeys(key)), inbox);
        await projector.ApplyAsync(notification, default);
        await projector.ApplyAsync(notification, default);
        Assert.Equal(before + 1, (await store.GetAsync(tenant, resource.Id, default))!.Value.Revision);
        var receipt = await control.ReceiptAsync(resource.Id, "external", reply ? "root" : null, default);
        Assert.NotNull(receipt);
        Assert.Equal(before + 1, receipt.Revision);
        var realtime = new LocalRealtime(store);
        var connection = realtime.Subscribe(actor);
        var outsider = realtime.Subscribe(actor with { Id = "not-a-member" });
        try
        {
            var queue = new TestQueue();
            var dispatcher = new OutboxDispatcher(tenant, options, queue, NullLogger<OutboxDispatcher>.Instance);
            var outbox = await documents.QueryAsync(new(tenant, "outbox", Pending: true), 100, null, default);
            await dispatcher.DispatchAsync(outbox.Items.Select(item => item.Value).ToArray(), default);
            var delivery = new DeliveryHandler(tenant, options, store, realtime, queue, clock, NullLogger<DeliveryHandler>.Instance);
            while (queue.Items.TryDequeue(out var work)) await delivery.HandleAsync(work, default);
            Assert.True(connection.Reader.TryRead(out var hint));
            var json = JsonDocument.Parse(hint);
            var revision = json.RootElement.GetProperty("revision").GetInt64();
            Assert.Equal(before + 1, revision);
            Assert.DoesNotContain("native Teams", hint);
            Assert.False(outsider.Reader.TryRead(out _));
            var messaging = new Messaging(store, new NoGraph(), new MemoryBudget(clock),
                new CommandTokens(new EphemeralDataProtectionProvider(), clock), clock);
            var changes = await messaging.ChangesAsync(actor, resource.Id, before, default);
            Assert.Equal("external", Assert.Single(changes.Messages!).Id);
            Assert.Equal(reply ? "root" : null, changes.Messages![0].ParentId);
            Assert.NotNull(changes.Window);
            var read = await messaging.ReadAsync(actor, resource.Id, null, reply ? "root" : null, revision, default);
            Assert.Contains(read.Messages, message => message.Id == "external");
            await store.SetBlockedAsync(tenant, actor.Id, true, default);
            Assert.Equal(401, (await Assert.ThrowsAsync<AppError>(() => messaging.ChangesAsync(actor, resource.Id, revision, default))).Status);
        }
        finally { realtime.Unsubscribe(connection.Id); realtime.Unsubscribe(outsider.Id); CryptographicOperations.ZeroMemory(key); }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    }
    private sealed class TestTokens : INotificationTokenValidator
    {
        public Task ValidateAsync(IReadOnlyList<string> tokens, CancellationToken ct)
        {
            Assert.Equal(["synthetic-signed-token-boundary"], tokens);
            return Task.CompletedTask;
        }
    }
    private sealed class TestKeys(byte[] key) : INotificationKeyDecryptor
    {
        public Task<byte[]> DecryptAsync(string certificateId, byte[] encryptedKey, CancellationToken ct) => Task.FromResult(key.ToArray());
    }
    private sealed class Inbox : IEncryptedNotificationInbox
    {
        public List<EncryptedNotification> Items { get; } = [];
        public Task EnqueueAsync(IReadOnlyList<EncryptedNotification> notifications, CancellationToken ct)
        { Items.AddRange(notifications); return Task.CompletedTask; }
    }
    private sealed class TestQueue : IDeliveryWorkQueue
    {
        public Queue<DeliveryWork> Items { get; } = new();
        public Task EnqueueAsync(DeliveryWork work, DateTimeOffset? scheduledAt, CancellationToken ct)
        { Items.Enqueue(work); return Task.CompletedTask; }
    }
    private sealed class NoGraph : IGraphTransport
    {
        public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Projected reads must not call Graph.");
        public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            throw new InvalidOperationException("Projected reads must not call Graph.");
    }
}
