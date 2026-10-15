using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.WebPubSub;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.Distributed;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using StackExchange.Redis;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
var config = builder.Configuration;
if (config["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 } monitor)
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o => o.ConnectionString = monitor);
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("local-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "test",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LocalAuth")
            .LogWarning("Local sign-in rate limit exceeded, trace {Trace}", context.HttpContext.TraceIdentifier);
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsJsonAsync(new { code = "login_limited", message = "Too many sign-in attempts. Wait before trying again." }, ct);
    };
});
var local = config["App:Store"] == "Memory";
var tenant = config["App:TenantId"] ?? throw new InvalidOperationException("App:TenantId must be configured.");
var fixtureIds = config.GetSection("App:FixtureActorIds").Get<string[]>() ?? [];
var role = config["App:Role"] ?? "Api";
var localAuth = config["App:Auth"] == "Local";
if (fixtureIds.Length != 4 || fixtureIds.Distinct().Count() != 4 || fixtureIds.Any(string.IsNullOrWhiteSpace))
    throw new InvalidOperationException("Four distinct fixture actor IDs are required.");
if (role is not ("Api" or "All" or "Worker" or "Seed" or "Verify" or "Reset"))
    throw new InvalidOperationException("App:Role is not a supported host role.");
if ((local || localAuth || role == "All") && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Memory persistence, local credentials and combined hosting are development/test only.");
if (config["App:GraphMode"] != "Simulator")
    throw new InvalidOperationException("Live Teams readiness is blocked. Only explicit Simulator mode is enabled in this delivery.");
if (!local && localAuth) throw new InvalidOperationException("Azure deployments require Entra authentication.");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Simulator>();
builder.Services.AddSingleton<IPrincipalRevocations>(sp => sp.GetRequiredService<IStateStore>());
builder.Services.AddSingleton<IGraphTransport>(sp => sp.GetRequiredService<Simulator>());
builder.Services.AddScoped<Messaging>();
builder.Services.AddSingleton<ICommandTokens, CommandTokens>();
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient<GraphTransport>(c => c.Timeout = TimeSpan.FromSeconds(20));
if (!localAuth) builder.Services.AddScoped<IGraphAccessTokenSource, DelegatedGraphTokens>();
else builder.Services.AddScoped<IGraphAccessTokenSource, DisabledGraphTokens>();
var protection = builder.Services.AddDataProtection().SetApplicationName("TeamsReference.Dev.v1");
if (local)
{
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSingleton<IStateStore, MemoryStateStore>();
    builder.Services.AddSingleton<ICommandBudget, MemoryBudget>();
    builder.Services.AddSingleton<IEventInbox, MemoryInbox>();
    builder.Services.AddSingleton<LocalRealtime>();
    builder.Services.AddSingleton<IRealtime>(sp => sp.GetRequiredService<LocalRealtime>());
}
else
{
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = config["AzureClientId"] });
    if (role == "Api")
    {
        var redisOptions = ConfigurationOptions.Parse(config["Azure:Redis"] ?? throw new InvalidOperationException("Azure:Redis is required."));
        redisOptions.Ssl = true;
        redisOptions.AbortOnConnectFail = false;
        await redisOptions.ConfigureForAzureWithTokenCredentialAsync(credential);
        var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        builder.Services.AddStackExchangeRedisCache(o => o.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(redis));
        protection.PersistKeysToStackExchangeRedis(redis, "data-protection:dev:v1")
            .ProtectKeysWithAzureKeyVault(new Uri(config["Azure:ProtectionKey"] ?? throw new InvalidOperationException("Azure:ProtectionKey is required.")), credential);
        builder.Services.AddSingleton<ICommandBudget>(new RedisBudget(redis, config["AzureAd:ClientId"] ?? throw new InvalidOperationException("AzureAd:ClientId is required.")));
    }
    else
    {
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<ICommandBudget, MemoryBudget>();
    }
    var cosmos = new CosmosClient(config["Azure:CosmosEndpoint"], credential, new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
        RequestTimeout = TimeSpan.FromSeconds(10),
        MaxRetryAttemptsOnRateLimitedRequests = 2,
        MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(5)
    });
    builder.Services.AddSingleton(cosmos);
    builder.Services.AddSingleton<IStateStore>(new CosmosStateStore(cosmos.GetContainer("teams", "state")));
    var bus = new ServiceBusClient(config["Azure:ServiceBus"], credential);
    builder.Services.AddSingleton(bus);
    builder.Services.AddSingleton<IEventInbox>(new ServiceBusInbox(bus, "notifications"));
    var pubsub = new WebPubSubServiceClient(new Uri(config["Azure:PubSub"] ?? throw new InvalidOperationException("Azure:PubSub is required.")), "messaging", credential);
    builder.Services.AddSingleton(pubsub);
    builder.Services.AddSingleton<IRealtime>(sp => new AzureRealtime(pubsub, sp.GetRequiredService<IPrincipalRevocations>()));
}

if (localAuth)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
}
else
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(config.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi().AddDistributedTokenCaches();
    builder.Services.Configure<MsalDistributedTokenCacheAdapterOptions>(o =>
    {
        o.Encrypt = true;
        o.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(8);
    });
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        o.UsePkce = true;
        o.ResponseMode = "form_post";
        o.CallbackPath = "/auth/callback";
        o.Events.OnTokenValidated += context =>
        {
            var id = context.Principal?.FindFirstValue("oid") ??
                context.Principal?.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
            if (id is null || !fixtureIds.Contains(id))
            {
                context.Fail("This dev application is limited to the approved fixture identities.");
                return Task.CompletedTask;
            }
            if (context.Principal?.Identity is ClaimsIdentity identity)
                identity.AddClaim(new Claim("app_sid", Guid.NewGuid().ToString("N")));
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToIdentityProvider += context =>
        {
            var origin = config["App:PublicOrigin"] ?? throw new InvalidOperationException("App:PublicOrigin is required.");
            context.ProtocolMessage.RedirectUri = $"{origin}/auth/callback";
            return Task.CompletedTask;
        };
    });
    builder.Services.AddAuthentication().AddJwtBearer("PubSub", options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
        options.Audience = config["Callbacks:Audience"] ?? throw new InvalidOperationException("Callbacks:Audience is required.");
        options.TokenValidationParameters.ValidAudiences = [options.Audience, config["Callbacks:ClientId"]
            ?? throw new InvalidOperationException("Callbacks:ClientId is required.")];
        options.MapInboundClaims = false;
    });
    builder.Services.AddAuthorizationBuilder().AddPolicy("PubSubCallback", policy =>
        policy.AddAuthenticationSchemes("PubSub").RequireAuthenticatedUser().RequireClaim("roles", "PubSub.Callback")
            .RequireAssertion(context => context.User.FindFirstValue("oid") == config["Callbacks:PrincipalId"]));
}
builder.Services.AddSingleton<SessionTickets>();
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<SessionTickets>((o, tickets) =>
    {
        o.SessionStore = tickets;
        o.Cookie.Name = "teams-reference-session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
if (role is "Worker" or "All") builder.Services.AddHostedService<ProjectionWorker>();

var app = builder.Build();
if (localAuth && (string.IsNullOrWhiteSpace(config["LocalAuth:PasswordHash"]) || string.IsNullOrWhiteSpace(config["LocalAuth:PasswordSalt"])))
    throw new InvalidOperationException("LocalAuth:PasswordHash and PasswordSalt are required; there is no default development password.");
if (local || role is "Seed" or "Verify" or "Reset")
{
    var stateStore = app.Services.GetRequiredService<IStateStore>();
    if (role == "Reset")
    {
        if (!config.GetValue<bool>("App:AllowSyntheticReset"))
            throw new InvalidOperationException("Explicit synthetic reset approval is required.");
        await Fixtures.ResetAsync(stateStore, tenant, fixtureIds, TimeProvider.System, CancellationToken.None);
    }
    var created = role is "Verify" or "Reset" ? 0
        : await Fixtures.SeedAsync(stateStore, tenant, fixtureIds, TimeProvider.System, CancellationToken.None);
    await Fixtures.VerifyAsync(stateStore, tenant, fixtureIds, CancellationToken.None);
    app.Logger.LogInformation("Fixture dataset {Version}: {Count} conversations created.", Fixtures.Version, created);
    if (role is "Seed" or "Verify" or "Reset") return;
}
if (role == "Worker")
{
    app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
    app.Run();
    return;
}
if (!local)
{
    var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
    foreach (var cidr in config.GetSection("App:TrustedProxyNetworks").Get<string[]>() ?? [])
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    app.UseForwardedHeaders(options);
    app.UseHsts();
}
var indexFile = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html");
var inlineScripts = File.Exists(indexFile) ? Regex.Matches(File.ReadAllText(indexFile), @"<script\b[^>]*>([\s\S]*?)</script>")
    .Select(m => m.Groups[1].Value).Where(s => !string.IsNullOrWhiteSpace(s))
    .Select(s => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)))}'") : [];
var scriptPolicy = string.Join(" ", inlineScripts);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = context.Request.Path.StartsWithSegments("/assets")
        ? "public,max-age=31536000,immutable" : "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' {scriptPolicy}; style-src 'self'; connect-src 'self' wss://*.webpubsub.azure.com; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    try { await next(); }
    catch (AppError ex)
    {
        app.Logger.LogWarning("Request failed with {Code}, status {Status}, trace {Trace}", ex.Code, ex.Status, context.TraceIdentifier);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex.Status;
        if (ex.Status == 429) context.Response.Headers.RetryAfter = "2";
        await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message, traceId = context.TraceIdentifier });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Request failed: {ErrorType}, trace {Trace}", ex.GetType().Name, context.TraceIdentifier);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { code = "dependency_unavailable", message = "A required service is unavailable. Try again; do not resend uncertain messages.", traceId = context.TraceIdentifier });
    }
});
app.UseAuthentication();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && context.User.Identity?.IsAuthenticated != true)
        throw new AppError(401, "authentication_required", "Sign in to continue.");
    if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments("/api"))
    {
        var actor = Current(context);
        if (actor.TenantId != tenant || !fixtureIds.Contains(actor.Id) || await context.RequestServices.GetRequiredService<IPrincipalRevocations>()
            .IsBlockedAsync(tenant, actor.Id, context.RequestAborted))
            throw new AppError(401, "authentication_required", "This session no longer has access. Sign in again.");
    }
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw new AppError(400, "csrf_invalid", "Refresh the page before submitting."); }
    }
    await next();
});
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IStateStore store, HttpContext c, CancellationToken ct) =>
{
    await Fixtures.VerifyAsync(store, tenant, fixtureIds, ct);
    if (!local) await c.RequestServices.GetRequiredService<IConnectionMultiplexer>().GetDatabase().PingAsync();
    return Results.Ok(new { status = "ready", mode = "simulator", fixtureVersion = Fixtures.Version });
});
app.MapGet("/auth/csrf", (HttpContext c, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(c).RequestToken }));
app.MapGet("/auth/signin", () => localAuth ? Results.Redirect("/") :
    Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapPost("/auth/local", async (HttpContext c, IAntiforgery csrf) =>
{
    if (!localAuth || c.Connection.RemoteIpAddress is not null && !IPAddress.IsLoopback(c.Connection.RemoteIpAddress))
        return Results.NotFound();
    try { await csrf.ValidateRequestAsync(c); }
    catch (AntiforgeryValidationException) { throw new AppError(400, "csrf_invalid", "Refresh the sign-in page."); }
    var input = await c.Request.ReadFromJsonAsync<LocalLogin>() ?? throw new AppError(400, "invalid_login", "Credentials are required.");
    var index = Array.IndexOf(fixtureIds, input.ActorId);
    if (input.Password.Length > 1024) throw new AppError(401, "invalid_login", "The development credentials are not valid.");
    var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(input.Password),
        Convert.FromHexString(config["LocalAuth:PasswordSalt"]!), 210000, HashAlgorithmName.SHA256, 32);
    var expected = Convert.FromHexString(config["LocalAuth:PasswordHash"]!);
    if (index < 0 || !CryptographicOperations.FixedTimeEquals(actual, expected))
        throw new AppError(401, "invalid_login", "The development credentials are not valid.");
    if (await c.RequestServices.GetRequiredService<IPrincipalRevocations>().IsBlockedAsync(tenant, fixtureIds[index], c.RequestAborted))
        throw new AppError(403, "access_denied", "This fixture account is offboarded.");
    var claims = new[] { new Claim("oid", fixtureIds[index]), new Claim("tid", tenant), new Claim("name", Fixtures.Names[index]),
        new Claim("app_sid", Guid.NewGuid().ToString("N")) };
    await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role)),
        new AuthenticationProperties { IsPersistent = false });
    return Results.Ok();
}).RequireRateLimiting("local-login");
app.MapPost("/auth/signout", async (HttpContext c, IAntiforgery csrf) =>
{
    try { await csrf.ValidateRequestAsync(c); }
    catch (AntiforgeryValidationException) { throw new AppError(400, "csrf_invalid", "Refresh the page before signing out."); }
    await c.RequestServices.GetRequiredService<IRealtime>().RevokeAsync(Current(c).TenantId, Current(c).Id, c.RequestAborted);
    await c.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/session", (HttpContext c, IAntiforgery csrf) =>
{
    var actor = Current(c);
    return Results.Ok(new
    {
        actor.Id,
        actor.Name,
        simulated = true,
        localAuth,
        scenarioAdmin = fixtureIds.Length > 0 && actor.Id == fixtureIds[0],
        csrf = csrf.GetAndStoreTokens(c).RequestToken
    });
});
api.MapGet("/conversations", (HttpContext c, Messaging service, CancellationToken ct) => service.ListAsync(Current(c), ct));
api.MapGet("/conversations/{id}/messages", (string id, string? cursor, string? parent, long? minimumRevision,
    HttpContext c, Messaging service, CancellationToken ct) => service.ReadAsync(Current(c), id, cursor, parent, minimumRevision ?? 0, ct));
api.MapGet("/conversations/{id}/changes", (string id, long? after, HttpContext c, Messaging service, CancellationToken ct) =>
    service.ChangesAsync(Current(c), id, after ?? 0, ct));
api.MapPost("/conversations/{id}/messages", async (string id, SendCommand command, HttpContext c, Messaging service, CancellationToken ct) =>
{
    var result = await service.SendAsync(Current(c), id, command, ct);
    return Results.Json(result, statusCode: result.State == OperationState.Confirmed ? 201 : result.State == OperationState.Rejected ? 422 : 202);
});
api.MapGet("/conversations/{id}/operations/{operationId}", (string id, string operationId, HttpContext c, Messaging service, CancellationToken ct) =>
    service.StatusAsync(Current(c), id, operationId, ct));
api.MapPost("/realtime/negotiate", async (HttpContext c) =>
{
    if (local) return Results.Ok(new { transport = "sse", url = "/api/realtime/events" });
    if (!config.GetValue<bool>("App:RealtimeAdmissionReady"))
        throw new AppError(503, "realtime_preparing", "Real-time admission is awaiting deployment readiness.");
    var actor = Current(c);
    var client = c.RequestServices.GetRequiredService<WebPubSubServiceClient>();
    var access = await client.GetClientAccessUriAsync(TimeSpan.FromMinutes(5), AzureRealtime.User(actor.TenantId, actor.Id),
        roles: [], cancellationToken: c.RequestAborted);
    return Results.Ok(new { transport = "webpubsub", url = access.AbsoluteUri });
});
if (local)
{
    api.MapGet("/realtime/events", async (HttpContext c, LocalRealtime realtime) =>
    {
        c.Response.ContentType = "text/event-stream";
        var subscription = realtime.Subscribe(Current(c));
        try
        {
            await c.Response.WriteAsync(": connected\n\n", c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
            await foreach (var change in subscription.Reader.ReadAllAsync(c.RequestAborted))
            {
                await c.Response.WriteAsync($"data: {change}\n\n", c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { }
        finally { realtime.Unsubscribe(subscription.Id); }
    });
}
api.MapPost("/simulator/next", (ScenarioRequest request, HttpContext c, Simulator simulator) =>
{
    RequireAdmin(c);
    simulator.Next(Current(c).Id, request.Action);
    return Results.Ok();
});
api.MapPost("/simulator/offboarding", async (ScenarioRequest request, HttpContext c, IPrincipalRevocations revocations, IRealtime realtime, CancellationToken ct) =>
{
    RequireAdmin(c);
    if (request.Action is not ("offboard" or "restore"))
        throw new AppError(400, "invalid_scenario", "Choose offboard or restore.");
    await revocations.SetBlockedAsync(tenant, fixtureIds[1], request.Action == "offboard", ct);
    if (request.Action == "offboard") await realtime.RevokeAsync(tenant, fixtureIds[1], ct);
    return Results.Ok();
});
api.MapPost("/simulator/conversations/{id}", async (string id, ScenarioRequest request, HttpContext c,
    Messaging service, IStateStore store, IEventInbox inbox, IRealtime realtime, CancellationToken ct) =>
{
    RequireAdmin(c);
    var actor = Current(c);
    var conversation = await service.GetAuthorizedAsync(actor, id, ct);
    if (request.Action is "revoke" or "restore")
    {
        var target = fixtureIds[1];
        var changed = conversation.Value with { Members = conversation.Value.Members.Select(m => m.Id == target ? m with { Active = request.Action == "restore" } : m).ToList() };
        if (!await store.SaveAsync(conversation with { Value = changed }, ct))
            throw new AppError(409, "concurrent_change", "The fixture changed. Retry this control action.");
        if (request.Action == "revoke") await realtime.RevokeAsync(tenant, target, ct);
        return Results.Ok();
    }
    var original = conversation.Value.Messages.LastOrDefault(m => !m.Deleted && m.ParentId is null);
    var now = DateTimeOffset.UtcNow;
    var message = request.Action switch
    {
        "external" => new Message($"external-{Guid.NewGuid():N}", fixtureIds[1], Fixtures.Names[1],
            "A new synthetic update arrived through the notification pipeline.", now, now),
        "edit" when original is not null => original with { Body = "Edited synthetic update.", ModifiedAt = now, SourceVersion = Guid.NewGuid().ToString("N") },
        "delete" when original is not null => original with { Body = "", Deleted = true, ModifiedAt = now, SourceVersion = Guid.NewGuid().ToString("N") },
        _ => throw new AppError(400, "invalid_scenario", "Choose external, edit, delete, revoke, or restore.")
    };
    await inbox.EnqueueAsync(new(tenant, id, message), ct);
    return Results.Accepted();
});
app.Map("/callbacks/graph/{**path}", () => Results.Json(new
{
    code = "live_graph_disabled",
    message = "Teams licensing, consent and live callback readiness are not approved."
}, statusCode: 503));
if (!local)
{
    app.MapMethods("/callbacks/pubsub", ["OPTIONS", "POST"], async (HttpContext c, IPrincipalRevocations revocations) =>
    {
        if (HttpMethods.IsOptions(c.Request.Method))
        {
            var origin = c.Request.Headers["WebHook-Request-Origin"].ToString();
            var expected = new Uri(config["Azure:PubSub"]!).Host;
            if (!string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
                throw new AppError(403, "callback_origin_invalid", "Callback origin is not configured.");
            c.Response.Headers["WebHook-Allowed-Origin"] = origin;
            return Results.Ok();
        }
        if (c.Request.Headers["ce-type"] != "azure.webpubsub.sys.connect")
            return Results.Ok();
        using var payload = await JsonDocument.ParseAsync(c.Request.Body, cancellationToken: c.RequestAborted);
        var user = payload.RootElement.GetProperty("userId").GetString();
        var parts = user?.Split(':');
        if (parts is not { Length: 2 } || parts[0] != tenant || !fixtureIds.Contains(parts[1]) ||
            await revocations.IsBlockedAsync(tenant, parts[1], c.RequestAborted))
            throw new AppError(403, "access_denied", "This connection is not authorized.");
        return Results.Ok(new { userId = user, groups = Array.Empty<string>() });
    }).RequireAuthorization("PubSubCallback");
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.Map("/api/{**path}", () => Results.NotFound(new { code = "route_not_found", message = "This API route does not exist." }));
app.MapFallbackToFile("index.html");
app.Run();

static Actor Current(HttpContext c) => new(
    c.User.FindFirstValue("tid") ?? c.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid")
        ?? throw new AppError(401, "authentication_required", "The tenant claim is missing. Sign in again."),
    c.User.FindFirstValue("oid") ?? c.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
        ?? throw new AppError(401, "authentication_required", "The principal claim is missing. Sign in again."),
    c.User.FindFirstValue("name") ?? c.User.Identity?.Name ?? "Test member",
    c.User.FindFirstValue("app_sid") ?? throw new AppError(401, "authentication_required", "Sign in to establish a new session."));
void RequireAdmin(HttpContext c)
{
    if (fixtureIds.Length == 0 || Current(c).Id != fixtureIds[0])
        throw new AppError(403, "access_denied", "This test control requires the designated fixture administrator.");
}
public partial class Program;
public sealed record LocalLogin(string ActorId, string Password);
public sealed record ScenarioRequest(string Action);
public sealed class DisabledGraphTokens : IGraphAccessTokenSource
{
    public Task<string> AcquireAsync(bool channel) => throw new AppError(503, "live_graph_disabled", "Local mode cannot acquire Graph tokens.");
}
