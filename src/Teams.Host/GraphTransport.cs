using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Web;
using Microsoft.Identity.Client;
using Teams.Core;

namespace Teams.Host;

public interface IGraphAccessTokenSource
{
    Task<string> AcquireAsync(bool channel);
}
public sealed class DelegatedGraphTokens(ITokenAcquisition tokens) : IGraphAccessTokenSource
{
    public async Task<string> AcquireAsync(bool channel)
    {
        try { return await tokens.GetAccessTokenForUserAsync([channel ? "ChannelMessage.Send" : "ChatMessage.Send"]); }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            throw new AppError(401, "graph_interaction_required", "Sign in again to satisfy Graph consent or Conditional Access.");
        }
        catch (MsalUiRequiredException)
        {
            throw new AppError(401, "graph_interaction_required", "Sign in again to satisfy Graph consent or Conditional Access.");
        }
    }
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
        string token;
        try { token = await tokens.AcquireAsync(channel); }
        catch (AppError ex) when (ex.Status == 401)
        {
            return new(OperationState.Rejected, null, ex.Message);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("https://graph.microsoft.com/v1.0/"), path));
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { body = new { contentType = "text", content = operation.Body } });
        // Deliberately no automatic POST retry: Graph does not promise this operation is idempotent.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            try
            {
                using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (!data.RootElement.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } messageId ||
                    !data.RootElement.TryGetProperty("createdDateTime", out var created) || !created.TryGetDateTimeOffset(out var at))
                    return new(OperationState.Unknown, null, "Graph created a message but confirmation evidence is incomplete.");
                var version = data.RootElement.TryGetProperty("etag", out var etag) ? etag.GetString() : null;
                return new(OperationState.Confirmed, new(messageId, actor.Id, actor.Name, operation.Body, at, at,
                    operation.ParentId, SourceVersion: version ?? $"confirmed:{messageId}"));
            }
            catch (JsonException)
            {
                return new(OperationState.Unknown, null, "Graph created a message but returned invalid confirmation evidence. Do not resend.");
            }
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
