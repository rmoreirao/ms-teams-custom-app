using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Teams.Host;

namespace Teams.Tests;

public sealed class OidcCallbackTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string PublicOrigin = "https://public.example";
    private const string Authority = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private const string TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token";

    [Fact]
    public async Task ProxyAuthorizationAndActualCodeRedemptionUseTheSamePublicCallback()
    {
        var transport = new TokenTransport();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = Tenant,
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AzureAd:ClientSecret"] = "synthetic-test-credential-not-a-secret",
            ["AzureAd:CallbackPath"] = "/auth/callback"
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddDistributedMemoryCache();
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                    .AddMicrosoftIdentityWebApp(configuration.GetSection("AzureAd"))
                    .EnableTokenAcquisitionToCallDownstreamApi().AddDistributedTokenCaches();
                services.AddCanonicalOidcCallback(PublicOrigin);
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.UsePkce = true;
                    options.ResponseMode = "form_post";
                    options.Configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = Authority,
                        AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                        TokenEndpoint = TokenEndpoint
                    };
                });
                services.AddSingleton<IMsalHttpClientFactory>(transport);
                services.AddSingleton<IHttpClientFactory>(transport);
            });
            web.Configure(app =>
            {
                app.Use((context, next) =>
                {
                    // Simulate TLS termination and origin-host forwarding at the private proxy.
                    context.Request.Scheme = "http";
                    context.Request.Host = new HostString("origin.internal");
                    return next(context);
                });
                app.UseRouting();
                app.UseAuthentication();
                app.UseEndpoints(endpoints => endpoints.MapGet("/signin", context =>
                    context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
                        new AuthenticationProperties { RedirectUri = "/" })));
            });
        }).StartAsync();

        using var client = host.GetTestClient();
        client.BaseAddress = new Uri(PublicOrigin);
        var challenge = await client.GetAsync("/signin");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        Assert.Equal($"{PublicOrigin}/auth/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        var cookies = string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        using var callback = new HttpRequestMessage(HttpMethod.Post, "/auth/callback");
        callback.Headers.Add("Cookie", cookies);
        callback.Headers.Add("X-Forwarded-Host", "untrusted.example");
        callback.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "synthetic-authorization-code",
            ["state"] = query["state"]!
        });
        await Assert.ThrowsAsync<AuthenticationFailureException>(() => client.SendAsync(callback));
        Assert.NotNull(transport.TokenForm);
        var form = HttpUtility.ParseQueryString(transport.TokenForm);
        Assert.Equal($"{PublicOrigin}/auth/callback", form["redirect_uri"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.False(string.IsNullOrWhiteSpace(form["code_verifier"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://public.example")]
    [InlineData("https://public.example/extra")]
    [InlineData("https://public.example/?query=1")]
    [InlineData("https://public.example/#fragment")]
    [InlineData("https://user@public.example")]
    public void RejectsInvalidCanonicalOrigins(string? origin)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCanonicalOidcCallback(origin));
    }

    private sealed class TokenTransport : HttpMessageHandler, IMsalHttpClientFactory, IHttpClientFactory
    {
        public string? TokenForm { get; private set; }
        public HttpClient GetHttpClient() => new(this, disposeHandler: false);
        public HttpClient CreateClient(string name) => GetHttpClient();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsoluteUri.StartsWith(TokenEndpoint, StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                TokenForm = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "Synthetic token rejection after capturing the exact request." });
            }
            if (uri.AbsolutePath.EndsWith("/discovery/instance", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, new
                {
                    tenant_discovery_endpoint = $"{Authority}/.well-known/openid-configuration",
                    metadata = new[] { new { preferred_network = "login.microsoftonline.com", preferred_cache = "login.microsoftonline.com", aliases = new[] { "login.microsoftonline.com" } } }
                });
            if (uri.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, new
                {
                    issuer = Authority,
                    authorization_endpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                    token_endpoint = TokenEndpoint,
                    jwks_uri = $"https://login.microsoftonline.com/{Tenant}/discovery/v2.0/keys"
                });
            throw new InvalidOperationException($"Unexpected identity transport path: {uri.AbsolutePath}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
