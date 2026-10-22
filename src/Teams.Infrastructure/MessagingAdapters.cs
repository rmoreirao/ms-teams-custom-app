using Azure.Messaging.WebPubSub;
using Teams.Core;

namespace Teams.Infrastructure;

public sealed class AzureRealtime(WebPubSubServiceClient client, IPrincipalRevocations revocations) : IRealtime
{
    public static string User(string tenant, string actor) => $"{tenant}:{actor}";
    public async Task PublishAsync(Conversation conversation, CancellationToken ct)
    {
        var body = BinaryData.FromObjectAsJson(new { conversationId = conversation.Id, revision = conversation.Revision });
        foreach (var member in conversation.Members.Where(m => m.Active))
        {
            if (await revocations.IsBlockedAsync(conversation.TenantId, member.Id, ct)) continue;
            await client.SendToUserAsync(User(conversation.TenantId, member.Id), Azure.Core.RequestContent.Create(body),
                Azure.Core.ContentType.ApplicationJson, context: new Azure.RequestContext { CancellationToken = ct });
        }
    }
    public async Task RevokeAsync(string tenant, string actorId, CancellationToken ct) =>
        await client.CloseUserConnectionsAsync(User(tenant, actorId), reason: "Access revoked",
            context: new Azure.RequestContext { CancellationToken = ct });
}
