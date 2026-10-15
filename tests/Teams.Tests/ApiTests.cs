using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Teams.Tests;

public sealed class ApiTests : IClassFixture<ApiTests.Factory>
{
    private readonly Factory factory;
    public ApiTests(Factory factory) => this.factory = factory;
    [Fact]
    public async Task AnonymousRequestsFailClosed()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations")).StatusCode);
    }
    [Fact]
    public async Task LoginReadSendAndCsrfProtectionWork()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/auth/csrf");
        using var login = new HttpRequestMessage(HttpMethod.Post, "/auth/local");
        login.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        login.Content = JsonContent.Create(new { actorId = "alex", password = factory.Password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        var page = await client.GetFromJsonAsync<JsonElement>("/api/conversations/chat-handover/messages");
        Assert.Equal(20, page.GetProperty("messages").GetArrayLength());
        var body = new { operationId = Guid.NewGuid(), window = page.GetProperty("window").GetString(), body = "HTTP scenario message", parentId = (string?)null };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/conversations/chat-handover/messages", body)).StatusCode);
        using var send = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/chat-handover/messages");
        send.Headers.Add("X-CSRF-TOKEN", session.GetProperty("csrf").GetString());
        send.Content = JsonContent.Create(body);
        var response = await client.SendAsync(send);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("confirmed", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("simulated").GetBoolean());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly byte[] salt = RandomNumberGenerator.GetBytes(16);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalAuth:PasswordHash"] = Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(Password), salt, 210000, HashAlgorithmName.SHA256, 32)),
                ["LocalAuth:PasswordSalt"] = Convert.ToHexString(salt)
            }));
        }
    }
}
