using System.Net;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class GraphTransportTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, OperationState.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, OperationState.Rejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, OperationState.Unknown)]
    [InlineData(HttpStatusCode.RequestTimeout, OperationState.Unknown)]
    public async Task PostOutcomesNeverTriggerAutomaticRetry(HttpStatusCode status, OperationState expected)
    {
        var handler = new Handler(status, "{}");
        var graph = new GraphTransport(new HttpClient(handler), new Tokens());
        var result = await graph.SendAsync(new("tenant", "alex", "Alex"), Conversation(), Operation(), default);
        Assert.Equal(expected, result.State);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://graph.microsoft.com/v1.0/chats/approved-chat/messages", handler.Url);
    }
    [Fact]
    public async Task CreationRequiresARealGraphMessageId()
    {
        var handler = new Handler(HttpStatusCode.Created, """{"id":"graph-message","createdDateTime":"2026-10-15T12:00:00Z"}""");
        var graph = new GraphTransport(new HttpClient(handler), new Tokens());
        var result = await graph.SendAsync(new("tenant", "alex", "Alex"), Conversation(), Operation(), default);
        Assert.Equal(OperationState.Confirmed, result.State);
        Assert.Equal("graph-message", result.Message?.Id);
        Assert.False(graph.Simulated);
    }
    [Fact]
    public async Task IncompleteCreationEvidenceRemainsUnknown()
    {
        var graph = new GraphTransport(new HttpClient(new Handler(HttpStatusCode.Created, "{}")), new Tokens());
        Assert.Equal(OperationState.Unknown, (await graph.SendAsync(new("tenant", "alex", "Alex"), Conversation(), Operation(), default)).State);
    }
    private static Conversation Conversation() => new("chat", "tenant", "Test", "chat", null, [], [], [], GraphChatId: "approved-chat");
    private static SendOperation Operation() => new(Guid.NewGuid().ToString(), "tenant", "chat", "alex", "fingerprint",
        "Synthetic contract test", null, OperationState.Dispatching, DateTimeOffset.UtcNow);
    private sealed class Tokens : IGraphAccessTokenSource
    {
        public Task<string> AcquireAsync(bool channel) => Task.FromResult("synthetic-token-not-a-credential");
    }
    private sealed class Handler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
