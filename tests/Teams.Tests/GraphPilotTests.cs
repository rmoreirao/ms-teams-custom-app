using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class GraphPilotTests
{
    private readonly MemoryStateStore journal = new();
    private readonly Clock clock = new();
    private readonly Reader reader = new();
    private readonly Actor actor = new("tenant", "alex", "Alex", "session");
    private GraphPilotStore Store()
    {
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        context.HttpContext.User = new(new ClaimsIdentity([new("oid", "alex"), new("tid", "tenant"), new("name", "Alex")], "test"));
        return new(journal, reader, new() { NotBefore = clock.GetUtcNow().AddHours(-1),
            Resources = [new("graph-pilot-chat", "chat", GraphPilotOptions.ResourcePrefix + " Chat", null, "chat", null, null)] },
            context, "tenant", ["alex", "sam", "jordan"], clock, NullLogger<GraphPilotStore>.Instance);
    }
    [Fact]
    public async Task DelegatedMessagesAreReadFromGraphWithoutPersistingBodies()
    {
        var store = Store();
        var messaging = new Messaging(store, new Transport(clock), new MemoryBudget(clock),
            new CommandTokens(new EphemeralDataProtectionProvider(), clock), clock);
        var page = await messaging.ReadAsync(actor, "graph-pilot-chat", null, null, 0, default);
        Assert.Equal("Graph source", Assert.Single(page.Messages).Body);
        Assert.False(page.Simulated);
        Assert.Empty((await journal.GetAsync("tenant", "graph-pilot-chat", default))!.Value.Messages);
        var command = new SendCommand(Guid.NewGuid().ToString(), page.Window, "Real Graph send", null);
        Assert.Equal(OperationState.Confirmed, (await messaging.SendAsync(actor, "graph-pilot-chat", command, default)).State);
        Assert.Empty((await journal.GetAsync("tenant", "graph-pilot-chat", default))!.Value.Messages);
        Assert.Equal("", (await journal.GetOperationAsync("tenant", "graph-pilot-chat", command.OperationId, default))!.Value.Body);
    }
    [Fact]
    public async Task NonValidationResourceAndUnexpectedAuthorsFailClosed()
    {
        var store = Store();
        await Assert.ThrowsAsync<AppError>(() => store.GetAsync("tenant", "business-conversation", default));
        reader.Author = "outsider";
        await Assert.ThrowsAsync<AppError>(() => store.GetAsync("tenant", "graph-pilot-chat", default));
    }
    [Fact]
    public async Task ResourceTagMismatchAndSimulatorControlsAreRejected()
    {
        reader.Tagged = false;
        await Assert.ThrowsAsync<AppError>(() => Store().ListAsync("tenant", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().GetSimulatorReceiptAsync("tenant", "graph-pilot-chat", "operation", default));
    }
    private sealed class Reader : IGraphPilotReader
    {
        public string Author { get; set; } = "alex";
        public bool Tagged { get; set; } = true;
        public Task<JsonElement> GetAsync(ApprovedResource resource, string path, CancellationToken ct) =>
            Task.FromResult(path.Contains("/messages", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { value = new[] { new { id = "one", createdDateTime = "2026-10-19T10:00:00Z",
                    lastModifiedDateTime = "2026-10-19T10:00:00Z", from = new { user = new { id = Author, displayName = "Alex" } },
                    body = new { content = "Graph source", contentType = "text" } } } })
                : JsonSerializer.SerializeToElement(new { topic = Tagged ? GraphPilotOptions.ResourcePrefix + " Chat" : "Business chat" }));
    }
    private sealed class Transport(Clock clock) : IGraphTransport
    {
        public bool Simulated => false;
        public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            Task.FromResult(new WriteResult(OperationState.Confirmed, new(operation.Id, actor.Id, actor.Name, operation.Body, clock.GetUtcNow(), clock.GetUtcNow())));
        public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) => Task.FromResult<WriteResult?>(null);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 10, 10, 0, TimeSpan.Zero);
    }
}
