using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class ApiTests : IClassFixture<ApiTests.Factory>
{
    private readonly Factory factory;
    public ApiTests(Factory factory) => this.factory = factory;
    [Fact]
    public async Task AnonymousRequestsFailClosed()
    {
        using var client = factory.Client(false);
        foreach (var path in new[] { "/api/session", "/api/conversations", "/api/people" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task LocalAuthenticationAndScenarioRoutesAreAbsentEvenForAnAuthenticatedCaller()
    {
        using var client = factory.Client();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        foreach (var path in new[] { "/auth/local", "/api/simulator/next", "/api/simulator/offboarding", "/api/simulator/conversations/chat-handover" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-CSRF-TOKEN", session.GetProperty("csrf").GetString());
            request.Content = JsonContent.Create(new { action = "confirm" });
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/config")).StatusCode);
    }
    [Fact]
    public async Task SessionDescribesOnlyTheTeamsConnectedDevelopmentContract()
    {
        using var client = factory.Client();
        var response = await client.GetAsync("/api/session");
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Development", session.GetProperty("environment").GetString());
        Assert.Equal("Microsoft Teams", session.GetProperty("source").GetString());
        Assert.False(session.GetProperty("canCreateDirectChats").GetBoolean());
        foreach (var field in new[] { "simulated", "localAuth", "scenarioAdmin", "basicValidation" })
            Assert.False(session.TryGetProperty(field, out _));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    [Fact]
    public async Task LiveReadSendAndCsrfProtectionUseTheRealApiPipeline()
    {
        using var client = factory.Client();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        var page = await client.GetFromJsonAsync<JsonElement>("/api/conversations/chat-handover/messages");
        Assert.Equal(20, page.GetProperty("messages").GetArrayLength());
        Assert.False(page.TryGetProperty("simulated", out _));
        var body = new { operationId = Guid.NewGuid(), window = page.GetProperty("window").GetString(), body = "HTTP dependency-test message", parentId = (string?)null };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/conversations/chat-handover/messages", body)).StatusCode);
        using var send = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/chat-handover/messages");
        send.Headers.Add("X-CSRF-TOKEN", session.GetProperty("csrf").GetString());
        send.Content = JsonContent.Create(body);
        var response = await client.SendAsync(send);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("confirmed", result.GetProperty("state").GetString());
        Assert.False(result.TryGetProperty("simulated", out _));
    }
    [Fact]
    public async Task CacheDiagnosticsUsesAuthenticatedChatAccessCsrfAndRecoverableCleanupStatus()
    {
        using var isolated = new Factory();
        using var anonymous = isolated.Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/conversations/chat-handover/cache-diagnostics")).StatusCode);
        using var client = isolated.Client();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        var snapshot = await client.GetFromJsonAsync<CacheSnapshot>("/api/conversations/chat-handover/cache-diagnostics");
        Assert.Equal(45, snapshot!.CosmosMessages);
        var plain = await client.GetFromJsonAsync<JsonElement>("/api/conversations/chat-handover/messages");
        Assert.False(plain.TryGetProperty("diagnostics", out _));
        var read = await client.GetFromJsonAsync<JsonElement>("/api/conversations/chat-handover/messages?diagnostics=true");
        var evidence = read.GetProperty("diagnostics");
        Assert.Equal("page", evidence.GetProperty("kind").GetString());
        Assert.Contains(evidence.GetProperty("steps").EnumerateArray(), step =>
            step.GetProperty("layer").GetString() == "cosmos" && step.GetProperty("outcome").GetString() == "content-query");
        var command = new CacheCleanupCommand(Guid.NewGuid().ToString(), true, false);
        var path = "/api/conversations/chat-handover/cache-diagnostics/cleanup";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, command)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/conversations/channel-general/cache-diagnostics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/conversations/unknown/cache-diagnostics")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", session.GetProperty("csrf").GetString());
        request.Content = JsonContent.Create(command);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CacheCleanup>();
        Assert.True(result!.Complete);
        Assert.Equal(45, result.RemovedMessages);
        Assert.Equal(result, await client.GetFromJsonAsync<CacheCleanup>($"{path}/{command.OperationId}"));
        snapshot = await client.GetFromJsonAsync<CacheSnapshot>("/api/conversations/chat-handover/cache-diagnostics");
        Assert.Equal(0, snapshot!.CosmosMessages);
        Assert.Equal(result, snapshot.Cleanup);
        Assert.False(snapshot.CoverageComplete);
        Assert.False(snapshot.ReadViewComplete);
    }
    [Fact]
    public async Task RevisionLagIsExplicitAndIncludesOnlyVerifiedRetentionAuthority()
    {
        using var client = factory.Client();
        var snapshot = await client.GetFromJsonAsync<MessagePage>("/api/conversations/chat-handover/messages");
        foreach (var path in new[]
        {
            "/api/conversations/chat-handover/messages?minimumRevision=900000",
            "/api/conversations/chat-handover/changes?after=900000",
            "/api/conversations/chat-handover/changes?after=0&minimumRevision=900000"
        })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("projection_behind", error.GetProperty("code").GetString());
            var access = error.GetProperty("access");
            Assert.Equal(snapshot!.Access!.ValidUntil, access.GetProperty("validUntil").GetDateTimeOffset());
            Assert.Equal(1, access.GetProperty("epoch").GetInt64());
            Assert.False(error.TryGetProperty("resetRequired", out _));
        }
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/conversations/chat-handover/messages?minimumRevision=-1")).StatusCode);
    }

    [Fact]
    public async Task ColdReadHasADistinct202ContractAndTheNextAuthorizedReadReturnsHydratedContent()
    {
        using var isolated = new Factory();
        isolated.Refresh = new AcceptedRefresh();
        var source = isolated.Fixture;
        source.Clock.Advance(TimeSpan.FromSeconds(1));
        var now = source.Clock.GetUtcNow();
        await source.Store.SetCoverageAsync(new(Factory.Tenant, "chat-handover", false, null, null, "cold-test"), default);
        await source.Store.SetEntitlementAsync(new(Factory.Tenant, "chat-handover", Factory.Actor, "Alex", now,
            now, now.AddMinutes(5), "late-history", true, true, 1), default);
        using var client = isolated.Client();
        var response = await client.GetAsync("/api/conversations/chat-handover/messages");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter!.Delta);
        var pending = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal(1000, pending.GetProperty("retryAfterMs").GetInt32());
        foreach (var name in new[] { "messages", "window", "refresh", "code" }) Assert.False(pending.TryGetProperty(name, out _));
        await source.Store.ApplyAsync(Factory.Tenant, "chat-handover",
            new("hydrated", Factory.Actor, "Alex", "Hydrated context", now, now), default);
        var available = await client.GetAsync("/api/conversations/chat-handover/messages");
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        Assert.Equal("Hydrated context", Assert.Single((await available.Content.ReadFromJsonAsync<MessagePage>())!.Messages).Body);
    }
    [Fact]
    public async Task InlineScriptHashesMatchBrowserNormalizedHtml()
    {
        using var client = factory.Client(false);
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var policy = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));
        foreach (Match match in Regex.Matches(html, @"<script\b[^>]*>([\s\S]*?)</script>"))
        {
            if (string.IsNullOrWhiteSpace(match.Groups[1].Value)) continue;
            var normalized = match.Groups[1].Value.Replace("\r\n", "\n").Replace('\r', '\n');
            Assert.Contains($"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))}'", policy);
        }
        Assert.DoesNotContain("'unsafe-inline'", policy);
    }
    [Fact]
    public async Task SignInUsesThePinnedRealEntraAuthorityAndCanonicalCallback()
    {
        using var client = factory.Client(false);
        var response = await client.GetAsync("/auth/signin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith($"https://login.microsoftonline.com/{Factory.Tenant}/oauth2/v2.0/authorize", response.Headers.Location!.AbsoluteUri);
        Assert.Contains("redirect_uri=https%3A%2F%2Fexample.test%2Fauth%2Fcallback", response.Headers.Location.Query);
    }
    [Theory]
    [InlineData("Simulator", "Cosmos", "Entra", "Api")]
    [InlineData("GraphPilot", "Cosmos", "Entra", "Api")]
    [InlineData("Live", "Memory", "Entra", "Api")]
    [InlineData("Live", "Cosmos", "Local", "Api")]
    [InlineData("Live", "Cosmos", "Entra", "Seed")]
    [InlineData("Live", "Cosmos", "Entra", "All")]
    [InlineData("Live", "Cosmos", "Entra", "Worker")]
    [InlineData("Live", "Cosmos", "Entra", "Reset")]
    [InlineData("Live", "Cosmos", "Entra", "Verify")]
    [InlineData("", "Cosmos", "Entra", "Api")]
    public void RemovedModesAndHostingCannotBeEnabledByConfiguration(string mode, string store, string auth, string role)
    {
        var settings = Factory.Settings();
        settings["App:GraphMode"] = mode; settings["App:Store"] = store; settings["App:Auth"] = auth; settings["App:Role"] = role;
        Assert.Throws<InvalidOperationException>(() => LiveHostConfiguration.Read(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }
    [Fact]
    public void ShippedAssembliesDoNotContainTheRetiredRuntimesOrTestDoubles()
    {
        var names = typeof(Program).Assembly.GetTypes().Concat(typeof(Teams.Infrastructure.LiveStateStore).Assembly.GetTypes())
            .Select(type => type.Name).ToArray();
        Assert.DoesNotContain(names, name => name.StartsWith("GraphPilot", StringComparison.Ordinal) ||
            name is "Simulator" or "Fixtures" or "MemoryStateStore" or "MemoryPartitionDocuments" or "MemoryBudget" or "LocalRealtime");
        Assert.DoesNotContain(typeof(IStateStore).GetMethods(), method => method.Name.Contains("Simulator", StringComparison.Ordinal));
    }
    public sealed class Factory : IDisposable
    {
        public const string Tenant = "11111111-1111-1111-1111-111111111111";
        public const string Actor = "33333333-3333-3333-3333-333333333333";
        private readonly LiveFixture fixture = new(Tenant, Actor);
        public LiveFixture Fixture => fixture;
        public IReadRefresh? Refresh { get; set; }
        private WebApplication? application;
        public HttpClient Client(bool signedIn = true)
        {
            application ??= Start();
            var client = new HttpClient(new CookieHandler(application.GetTestServer().CreateHandler()));
            client.BaseAddress = new("https://localhost");
            if (signedIn) client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
            return client;
        }
        public static Dictionary<string, string?> Settings()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Test notification", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var options = new LiveOptions
            {
                ReadConsistency = "Session",
                ApprovalReference = "test-only-fixture",
                SyncClientId = "22222222-2222-2222-2222-222222222222",
                ControlIdentityClientId = "44444444-4444-4444-4444-444444444444",
                WriterEpoch = 1,
                NotificationUrl = "https://example.test/callbacks/graph/notifications",
                LifecycleUrl = "https://example.test/callbacks/graph/lifecycle",
                EntitlementAuthority = "https://policy.example.test",
                EntitlementScope = "api://policy/read",
                NotificationCertificateId = "test",
                NotificationCertificate = Convert.ToBase64String(cert.Export(X509ContentType.Cert)),
                NotificationKeys = new() { ["test"] = "https://example.vault.azure.net/keys/test/version" },
                AdmissionEnabled = true,
                SynchronizationEnabled = true,
                Resources = [new("chat-handover", "chat", "Chat", null, "chat-handover", null, null)],
                SubscriptionScopes = [new("scope", "chats/chat-handover/messages")]
            };
            return new()
            {
                ["App:GraphMode"] = "Live",
                ["App:Store"] = "Cosmos",
                ["App:Auth"] = "Entra",
                ["App:Role"] = "Api",
                ["App:TenantId"] = Tenant,
                ["App:PublicOrigin"] = "https://example.test",
                ["Live:Configuration"] = JsonSerializer.Serialize(options, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ["AzureAd:TenantId"] = Tenant,
                ["AzureAd:ClientId"] = options.SyncClientId,
                ["Azure:ProtectionKey"] = "https://example.vault.azure.net/keys/session",
                ["Azure:PubSub"] = "https://example.webpubsub.azure.com",
                ["Callbacks:Audience"] = "api://test",
                ["Callbacks:ClientId"] = options.SyncClientId
            };
        }
        private WebApplication Start()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
                ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Teams.Host"))
            });
            builder.Configuration.AddInMemoryCollection(Settings());
            builder.WebHost.UseTestServer();
            var app = TeamsApplication.Build(builder, services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IConfigureOptions<KeyManagementOptions>>();
                services.RemoveAll<IConfigureOptions<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions>>();
                services.RemoveAll<IDistributedCache>();
                services.AddDistributedMemoryCache();
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddSingleton<TimeProvider>(fixture.Clock);
                services.AddSingleton<ILiveStateStore>(fixture.Store);
                services.AddSingleton<IStateStore>(fixture.Store);
                services.AddSingleton<IPrincipalRevocations>(fixture.Store);
                services.AddSingleton<IRealtime, LocalRealtime>();
                services.AddSingleton<IGraphTransport>(new GraphResultStub(fixture.Clock));
                services.AddSingleton<ICommandBudget>(new MemoryBudget(fixture.Clock));
                services.RemoveAll<IReadRefresh>();
                if (Refresh is not null) services.AddSingleton(Refresh);
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    o.Configuration = new OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize"
                    });
            });
            app.StartAsync().GetAwaiter().GetResult();
            return app;
        }
        public void Dispose() => application?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Actor")) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new("oid", Factory.Actor), new("tid", Factory.Tenant),
                new("name", "Alex"), new("app_sid", "test-session")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer cookies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var header = cookies.GetCookieHeader(uri);
            if (header.Length > 0) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) cookies.SetCookies(uri, value);
            return response;
        }
    }
    private sealed class AcceptedRefresh : IReadRefresh
    {
        public Task RequestAsync(ReadRefreshRequest request, bool required, CancellationToken ct) => Task.CompletedTask;
    }
}
