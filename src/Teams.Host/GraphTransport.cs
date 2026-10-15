using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Web;
using Teams.Core;

namespace Teams.Host;

public interface IGraphAccessTokenSource
{
    Task<string> AcquireAsync(bool channel);
}
public sealed class DelegatedGraphTokens(ITokenAcquisition tokens) : IGraphAccessTokenSource
{
    public Task<string> AcquireAsync(bool channel) =>
        tokens.GetAccessTokenForUserAsync([channel ? "ChannelMessage.Send" : "ChatMessage.Send"]);
}
public sealed class GraphTransport(HttpClient http, IGraphAccessTokenSource tokens) : IGraphTransport
{
    public bool Simulated => false;
    public async Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct)
    {
        var channel = conversation.Kind == "channel";
        var path = channel
            ? $"teams/{Part(conversation.GraphTeamId)}/channels/{Part(conversation.GraphChannelId)}/messages" +
              (operation.ParentId is null ? "" : $"/{Part(operation.ParentId)}/replies")
            : $"chats/{Part(conversation.GraphChatId)}/messages";
        var token = await tokens.AcquireAsync(channel);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("https://graph.microsoft.com/v1.0/"), path));
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { body = new { contentType = "text", content = operation.Body } });
        // Deliberately no automatic POST retry: Graph does not promise this operation is idempotent.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!data.RootElement.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } messageId)
                return new(OperationState.Unknown, null, "Graph created a message but confirmation evidence is incomplete.");
            var at = data.RootElement.GetProperty("createdDateTime").GetDateTimeOffset();
            return new(OperationState.Confirmed, new(messageId, actor.Id, actor.Name, operation.Body, at, at, operation.ParentId));
        }
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            return new(OperationState.Rejected, null, $"Graph rejected this send ({(int)response.StatusCode}).");
        return new(OperationState.Unknown, null, "Graph commitment is uncertain. Check the original operation; do not resend.");
    }
    public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
        Task.FromResult<WriteResult?>(null);
    private static string Part(string? value) => value is { Length: > 0 }
        ? Uri.EscapeDataString(value) : throw new AppError(503, "live_resource_unconfigured", "This conversation has no approved Teams resource mapping.");
}
