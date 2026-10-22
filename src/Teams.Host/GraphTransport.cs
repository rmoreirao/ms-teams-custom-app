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
    public Task<string> AcquireAsync(bool channel) => AcquireScopesAsync([channel ? "ChannelMessage.Send" : "ChatMessage.Send"]);
    private async Task<string> AcquireScopesAsync(string[] scopes)
    {
        try { return await tokens.GetAccessTokenForUserAsync(scopes); }
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
public sealed class GraphTransport(HttpClient http, IGraphAccessTokenSource tokens, GraphRetry? retry = null) : IGraphTransport
{
    private readonly GraphRetry retries = retry ?? new();
    public async Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct)
    {
        using var measurement = LiveTelemetry.Measure("graph-send");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(retries.Options.AttemptTimeoutSeconds), retries.Clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        ct = deadline.Token;
        var channel = conversation.Kind == "channel";
        var path = channel
            ? $"teams/{Part(conversation.GraphTeamId)}/channels/{Part(conversation.GraphChannelId)}/messages" +
              (operation.ParentId is null ? "" : $"/{Part(operation.ParentId)}/replies")
            : $"chats/{Part(conversation.GraphChatId)}/messages";
        string token;
        try { token = await tokens.AcquireAsync(channel).WaitAsync(ct); }
        catch (AppError ex) when (ex.Status == 401)
        {
            return new(OperationState.Rejected, null, ex.Message, Dispatched: false);
        }
        catch (OperationCanceledException)
        {
            return new(OperationState.Rejected, null, "Token acquisition ended before Graph dispatch. No message was posted.", Dispatched: false);
        }
        if (ct.IsCancellationRequested)
            return new(OperationState.Rejected, null, "The send stopped before Graph dispatch. No message was posted.", Dispatched: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("https://graph.microsoft.com/v1.0/"), path));
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { body = new { contentType = "text", content = operation.Body } });
        // Messaging retries only definite Graph 429 rejections, under the original operation owner.
        var attempt = operation.Attempts > 1 ? "retry" : "initial";
        LiveTelemetry.GraphAttempts.Add(1, new("mode", "delegated"), new KeyValuePair<string, object?>("attempt", attempt));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        LiveTelemetry.GraphResponses.Add(1, new("mode", "delegated"), new("attempt", attempt),
            new KeyValuePair<string, object?>("status", (int)response.StatusCode));
        if (response.StatusCode == HttpStatusCode.Created)
        {
            try
            {
                var data = await BoundedJson.ReadAsync(response.Content, 1024 * 1024, ct);
                if (data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    id.GetString() is not { Length: > 0 } messageId ||
                    !data.TryGetProperty("createdDateTime", out var created) || created.ValueKind != JsonValueKind.String ||
                    !created.TryGetDateTimeOffset(out var at))
                    return new(OperationState.Unknown, null, "Graph created a message but confirmation evidence is incomplete.", Status: 201);
                var version = data.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String ? etag.GetString() : null;
                return new(OperationState.Confirmed, new(messageId, actor.Id, actor.Name, operation.Body, at, at,
                    operation.ParentId, SourceVersion: version ?? $"confirmed:{messageId}"), Status: 201);
            }
            catch (JsonException)
            {
                return new(OperationState.Unknown, null, "Graph created a message but returned invalid confirmation evidence. Do not resend.", Status: 201);
            }
            catch (AppError ex) when (ex.Code == "dependency_payload_too_large")
            {
                return new(OperationState.Unknown, null, "Graph confirmation exceeded the evidence limit. Check the original operation; do not resend.", Status: 201);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return new(OperationState.Unknown, null, "Graph accepted the send but confirmation could not be read. Do not resend.", Status: 201);
            }
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = GraphRequests.RetryAfter(response, retries.Clock);
            return new(OperationState.Rejected, null, "Graph throttled this send.", delay, Status: 429);
        }
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            return new(OperationState.Rejected, null, $"Graph rejected this send ({(int)response.StatusCode}).", Status: (int)response.StatusCode);
        return new(OperationState.Unknown, null, "Graph commitment is uncertain. Check the original operation; do not resend.", Status: (int)response.StatusCode);
    }
    public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
        Task.FromResult<WriteResult?>(null);
    private static string Part(string? value) => value is { Length: > 0 }
        ? Uri.EscapeDataString(value) : throw new AppError(503, "live_resource_unconfigured", "This conversation has no approved Teams resource mapping.");
}
