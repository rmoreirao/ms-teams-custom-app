using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class GraphCursorTests
{
    private const string Expected = "chats/19%3AMixed%40thread.v2/messages";
    [Theory]
    [InlineData("chats/19:Mixed@thread.v2/messages")]
    [InlineData("chats/19%3aMixed%40thread.v2/messages")]
    [InlineData("chats('19:Mixed@thread.v2')/messages")]
    [InlineData("chats('19%3aMixed%40thread.v2')/messages")]
    public void DocumentedAndODataContinuationsKeepTheirOpaqueQueryAndIdentity(string path)
    {
        var link = $"https://graph.microsoft.com/v1.0/{path}?$top=50&$skiptoken=opaque%2Btoken%3d%3d+literal";
        var uri = GraphUrls.Validate(link, Expected);
        Assert.Equal(link, uri.OriginalString);
        Assert.Equal(Expected, GraphUrls.ResourcePath(uri));
        Assert.Equal(GraphUrls.BudgetResource(GraphUrls.Validate(Expected)), GraphUrls.BudgetResource(uri));
        Assert.Equal(GraphUrls.BudgetResource(uri), GraphUrls.BudgetResource(GraphUrls.Validate(Expected + "/message")));
    }
    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/chats/19:mixed@thread.v2/messages")]
    [InlineData("https://graph.microsoft.com/v1.0/chats/19:Other@thread.v2/messages")]
    [InlineData("https://graph.microsoft.com/v1.0/chats/19:Mixed@thread.v2/messages/extra")]
    [InlineData("https://graph.microsoft.com/v1.0/chats/19%253AMixed%2540thread.v2/messages")]
    [InlineData("https://graph.microsoft.com/beta/chats/19:Mixed@thread.v2/messages")]
    [InlineData("https://graph.microsoft.com.evil.invalid/v1.0/chats/19:Mixed@thread.v2/messages")]
    [InlineData("https://user@graph.microsoft.com/v1.0/chats/19:Mixed@thread.v2/messages")]
    [InlineData("https://graph.microsoft.com:444/v1.0/chats/19:Mixed@thread.v2/messages")]
    public void CanonicalizationDoesNotBroadenAuthorityVersionCaseOrResource(string link) =>
        Assert.Throws<AppError>(() => GraphUrls.Validate(link, Expected));
    [Fact]
    public void EncodedSlashesRemainInsideTheirIdentifierAndRepliesRemainScoped()
    {
        Assert.Equal("chats/id%2Fpart/messages",
            GraphUrls.ResourcePath(GraphUrls.Validate("chats/id%2fpart/messages", "chats/id%2Fpart/messages")));
        Assert.Throws<AppError>(() => GraphUrls.Validate("chats/id/part/messages", "chats/id%2Fpart/messages"));
        const string expected = "teams/team/channels/19%3AChannel%40thread.tacv2/messages/parent/replies";
        var uri = GraphUrls.Validate("teams('team')/channels('19:Channel@thread.tacv2')/messages('parent')/replies?$skiptoken=x", expected);
        Assert.Equal(expected, GraphUrls.ResourcePath(uri));
        Assert.Equal(GraphUrls.BudgetResource(GraphUrls.Validate(expected)), GraphUrls.BudgetResource(uri));
        Assert.Throws<AppError>(() => GraphUrls.Validate(expected.Replace("/parent/", "/other/", StringComparison.Ordinal), expected));
        Assert.True(GraphUrls.IsReauthorization(HttpMethod.Post, GraphUrls.Validate("subscriptions('approved')/reauthorize")));
        Assert.False(GraphUrls.IsReauthorization(HttpMethod.Get, GraphUrls.Validate("subscriptions('approved')/reauthorize")));
    }
    [Theory]
    [InlineData("chats/19:Mixed@thread.v2/messages?$skiptoken=opaque")]
    [InlineData("chats('19:Mixed@thread.v2')/messages?$skiptoken=opaque")]
    [InlineData("chats/19%3aMixed%40thread.v2/messages")]
    public async Task DelegatedGateAcceptsEquivalentPathsButStillRequiresTheOriginalUserContext(string path)
    {
        var resource = new ApprovedResource("chat", "chat", "Chat", null, "19:Mixed@thread.v2", null, null);
        var error = await ScopedRead(resource, path);
        Assert.Equal("graph_not_dispatched", error.Code);
    }
    [Theory]
    [InlineData("chats/19:Other@thread.v2/messages")]
    [InlineData("chats/19:mixed@thread.v2/messages")]
    [InlineData("chats/19:Mixed@thread.v2/messagesOther")]
    public async Task DelegatedGateStillRejectsDifferentResources(string path)
    {
        var resource = new ApprovedResource("chat", "chat", "Chat", null, "19:Mixed@thread.v2", null, null);
        Assert.Equal("synthetic_scope_denied", (await ScopedRead(resource, path)).Code);
    }
    private static async Task<AppError> ScopedRead(ApprovedResource resource, string path)
    {
        const string tenant = "11111111-1111-1111-1111-111111111111";
        const string actor = "22222222-2222-2222-2222-222222222222";
        var clock = TimeProvider.System;
        var options = new LiveOptions
        {
            AuthorityMode = "DelegatedSynthetic", Resources = [resource], SyntheticActorIds = [actor],
            SyntheticResourceOwners = new() { [resource.Id] = actor }
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["App:TenantId"] = tenant, ["AzureAd:ClientId"] = "33333333-3333-3333-3333-333333333333" }).Build();
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var contexts = new SyntheticUserContexts(services.GetRequiredService<IDistributedCache>(), new EphemeralDataProtectionProvider(),
            options, configuration, clock, NullLogger<SyntheticUserContexts>.Instance);
        var graph = new DelegatedSyntheticGraph(new NoHttp(), services.GetRequiredService<IServiceScopeFactory>(),
            contexts, options, configuration, new(new MemoryPartitionDocuments(), clock, tenant));
        return await Assert.ThrowsAsync<AppError>(() => graph.ReadAsAsync(actor, resource, path, default));
    }
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP request may bypass the retained user context.");
    }
}
