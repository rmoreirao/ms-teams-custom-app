using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class ServiceBusStandardTests
{
    private const int StandardLimit = 256 * 1024;

    [Fact]
    public async Task NotificationsHonorNegotiatedBatchCapacityWithoutSplittingEnvelopes()
    {
        var sender = new BoundedSender();
        await using var inbox = new ServiceBusNotificationInbox(new Bus(sender));
        var first = Notification(150_000);
        var second = Notification(150_000);

        await inbox.EnqueueAsync([first, second], default);

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, batch => Assert.Single(batch));
        Assert.All(sender.Sent.SelectMany(batch => batch), message =>
        {
            Assert.InRange(EncodedSize(message), 1, StandardLimit);
            Assert.Equal("application/json", message.ContentType);
            Assert.Equal(TimeSpan.FromHours(1), message.TimeToLive);
            Assert.Equal(first.EncryptedContent!.Value.GetRawText(),
                message.Body.ToObjectFromJson<EncryptedNotification>()!.EncryptedContent!.Value.GetRawText());
        });
    }

    [Fact]
    public async Task OversizedEnvelopeFailsBeforeAnyBatchIsSent()
    {
        var sender = new BoundedSender();
        await using var inbox = new ServiceBusNotificationInbox(new Bus(sender));

        var error = await Assert.ThrowsAsync<AppError>(() =>
            inbox.EnqueueAsync([Notification(100), Notification(StandardLimit)], default));

        Assert.Equal(503, error.Status);
        Assert.Equal("notification_too_large", error.Code);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task BrokerPropertiesCountTowardTheNegotiatedLimit()
    {
        var notification = Notification(200);
        var body = BinaryData.FromObjectAsJson(notification);
        var sender = new BoundedSender(body.ToMemory().Length);
        await using var inbox = new ServiceBusNotificationInbox(new Bus(sender));

        Assert.Equal("notification_too_large",
            (await Assert.ThrowsAsync<AppError>(() => inbox.EnqueueAsync([notification], default))).Code);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task FailedBatchSendDoesNotClaimDurableAcceptance()
    {
        var sender = new BoundedSender { FailSend = true };
        await using var inbox = new ServiceBusNotificationInbox(new Bus(sender));

        await Assert.ThrowsAsync<ServiceBusException>(() => inbox.EnqueueAsync([Notification(100)], default));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void LifecycleDeliveryAndReadRefreshEnvelopesFitStandardWithHeaderHeadroom()
    {
        var now = DateTimeOffset.UtcNow;
        object[] work =
        [
            new EncryptedNotification("tenant", "scope:approved", "chats/approved/messages", "", null, now, "missed"),
            new DeliveryWork(new("outbox", "tenant", "approved", long.MaxValue, now), long.MaxValue,
                Guid.NewGuid().ToString("N")),
            new ReadRefreshRequest("tenant", "approved", 1, new string('p', 128), now, new string('i', 512), now)
        ];
        foreach (var item in work)
        {
            var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(item))
            {
                MessageId = new string('m', 128), ContentType = "application/json", TimeToLive = TimeSpan.FromHours(1)
            };
            Assert.InRange(EncodedSize(message), 1, StandardLimit - 16 * 1024);
        }
    }

    private static int EncodedSize(ServiceBusMessage message) =>
        message.GetRawAmqpMessage().ToBytes().ToMemory().Length;

    private static EncryptedNotification Notification(int length) => new("tenant", "approved",
        "chats/approved/messages/message", "updated",
        JsonSerializer.SerializeToElement(new
        {
            data = new string('A', length), dataKey = new string('A', 344),
            dataSignature = new string('A', 44), encryptionCertificateId = "approved-key"
        }), DateTimeOffset.UtcNow);

    private sealed class Bus(BoundedSender sender) : ServiceBusClient
    {
        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            Assert.Equal("graph-notifications", queueOrTopicName);
            return sender;
        }
    }

    private sealed class BoundedSender(int limit = StandardLimit) : ServiceBusSender
    {
        private readonly Dictionary<ServiceBusMessageBatch, List<ServiceBusMessage>> batches = [];
        public List<ServiceBusMessage[]> Sent { get; } = [];
        public bool FailSend { get; init; }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override ValueTask<ServiceBusMessageBatch> CreateMessageBatchAsync(CancellationToken cancellationToken = default)
        {
            var used = 0;
            var messages = new List<ServiceBusMessage>();
            var batch = ServiceBusModelFactory.ServiceBusMessageBatch(0, messages,
                new CreateMessageBatchOptions { MaxSizeInBytes = limit }, message =>
                {
                    var size = EncodedSize(message);
                    if (used + size > limit) return false;
                    used += size;
                    return true;
                });
            batches.Add(batch, messages);
            return ValueTask.FromResult(batch);
        }

        public override Task SendMessagesAsync(ServiceBusMessageBatch messageBatch, CancellationToken cancellationToken = default)
        {
            if (FailSend) throw new ServiceBusException("Unavailable.", ServiceBusFailureReason.ServiceCommunicationProblem);
            Sent.Add(batches[messageBatch].ToArray());
            return Task.CompletedTask;
        }
    }
}
