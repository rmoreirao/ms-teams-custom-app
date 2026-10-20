using System.Text;
using Microsoft.AspNetCore.Http;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

namespace Teams.Tests;

public sealed class PubSubAdmissionTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Actor = "22222222-2222-2222-2222-222222222222";
    private const string Host = "approved.webpubsub.azure.com";
    private static HttpRequest Request(string body = """{"claims":{},"query":{},"headers":{},"subprotocols":["json.reliable.webpubsub.azure.v1"]}""")
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["WebHook-Request-Origin"] = Host;
        request.Headers["ce-specversion"] = "1.0";
        request.Headers["ce-type"] = "azure.webpubsub.sys.connect";
        request.Headers["ce-hub"] = "messaging";
        request.Headers["ce-userId"] = $"{Tenant}:{Actor}";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return request;
    }
    [Fact]
    public async Task BinaryCloudEventsUseTheServiceHeaderNotAFictionalBodyUserId()
    {
        var result = await PubSubAdmission.ConnectAsync(Request(), Host, Tenant, new HashSet<string> { Actor },
            Store(), true, default);
        Assert.Equal($"{Tenant}:{Actor}", result.UserId);
        Assert.Equal("json.reliable.webpubsub.azure.v1", result.Subprotocol);
        var spoofed = Request("""{"userId":"different","subprotocols":["json.webpubsub.azure.v1"]}""");
        Assert.Equal($"{Tenant}:{Actor}", (await PubSubAdmission.ConnectAsync(spoofed, Host, Tenant, null,
            Store(), true, default)).UserId);
    }
    [Fact]
    public void OriginValidationDoesNotNeedAUserCredentialAndCannotAuthorizeAConnection()
    {
        var request = Request();
        PubSubAdmission.ValidateOrigin(request, Host);
        request.Headers["WebHook-Request-Origin"] = "other.webpubsub.azure.com";
        Assert.Equal(403, Assert.Throws<AppError>(() => PubSubAdmission.ValidateOrigin(request, Host)).Status);
    }
    [Fact]
    public async Task AdmissionActorsProtocolsAndPrincipalRevocationAreEnforced()
    {
        var store = Store();
        Assert.Equal(403, (await Assert.ThrowsAsync<AppError>(() =>
            PubSubAdmission.ConnectAsync(Request(), Host, Tenant, null, store, false, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppError>(() =>
            PubSubAdmission.ConnectAsync(Request(), Host, Tenant, new HashSet<string>(), store, true, default))).Status);
        await store.SetBlockedAsync(Tenant, Actor, true, default);
        Assert.Equal(403, (await Assert.ThrowsAsync<AppError>(() =>
            PubSubAdmission.ConnectAsync(Request(), Host, Tenant, null, store, true, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<AppError>(() =>
            PubSubAdmission.ConnectAsync(Request("""{"subprotocols":["unsupported"]}"""), Host, Tenant, null,
                Store(), true, default))).Status);
    }
    private static LiveStateStore Store() => new(new MemoryPartitionDocuments(), TimeProvider.System);
}
