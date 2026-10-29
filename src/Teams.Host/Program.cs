using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Messaging.WebPubSub;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.Distributed;
using OpenTelemetry.Metrics;
using Teams.Core;
using Teams.Host;
using Teams.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("TEAMS_CONFIGURATION_PATH") is { Length: > 0 } configurationPath)
{
    if (!Path.IsPathFullyQualified(configurationPath))
        throw new InvalidOperationException("TEAMS_CONFIGURATION_PATH must be an absolute private configuration path.");
    builder.Configuration.AddJsonFile(configurationPath, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables().AddCommandLine(args);
}
if (builder.Configuration["App:Operation"] == "PrepareNotification")
{
    await LiveDiagnostics.PrepareNotificationAsync(builder.Configuration);
    await LiveDiagnostics.HoldLogsAsync(builder.Configuration);
    return;
}
var application = TeamsApplication.Build(builder);
if (builder.Configuration["App:Operation"] is { Length: > 0 } diagnostic)
{
    await LiveDiagnostics.RunAsync(application.Services, builder.Configuration, application.Services.GetRequiredService<LiveOptions>(),
        builder.Configuration["App:TenantId"]!, diagnostic);
    await LiveDiagnostics.HoldLogsAsync(builder.Configuration);
    return;
}
await application.RunAsync();

public static class TeamsApplication
{
    public static WebApplication Build(WebApplicationBuilder builder, Action<IServiceCollection>? configureServices = null)
    {
        var config = builder.Configuration;
        var (tenant, role, liveOptions) = LiveHostConfiguration.Read(config);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        if (config["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 } monitor)
            builder.Services.AddOpenTelemetry().WithMetrics(o => o.AddMeter(LiveTelemetry.MeterName))
                .UseAzureMonitor(o => o.ConnectionString = monitor);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICommandTokens, CommandTokens>();
        builder.Services.AddSingleton<IPrincipalRevocations>(sp => sp.GetRequiredService<ILiveStateStore>());
        builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; });
        builder.Services.AddAuthorization();
        builder.Services.AddAzureRuntime(config, liveOptions, tenant, role);
        if (role == "Api")
        {
            builder.Services.AddScoped<Messaging>();
            builder.Services.AddHttpClient<GraphTransport>(c => c.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddScoped<IGraphTransport>(sp => sp.GetRequiredService<GraphTransport>());
            builder.Services.AddScoped<IGraphAccessTokenSource, DelegatedGraphTokens>();
            if (liveOptions.DelegatedSynthetic) builder.Services.AddSingleton<SyntheticUserContexts>();
            builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(config.GetSection("AzureAd"))
                .EnableTokenAcquisitionToCallDownstreamApi(liveOptions.DelegatedSynthetic
                    ? DevelopmentTeamsScope.Scopes.Concat(["Chat.Create"])
                    : liveOptions.Resources.Select(r => r.Kind == "chat" ? "ChatMessage.Send" : "ChannelMessage.Send")
                        .Concat(liveOptions.ChannelDiscovery.RegistryEnabled ? ["ChannelMessage.Send"] : []).Distinct())
                .AddDistributedTokenCaches();
            builder.Services.AddCanonicalOidcCallback(config["App:PublicOrigin"]);
            builder.Services.Configure<MsalDistributedTokenCacheAdapterOptions>(o =>
            {
                o.Encrypt = true;
                o.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(8);
            });
            builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.UsePkce = true;
                o.ResponseMode = "form_post";
                o.Events.OnTokenValidated += context =>
                {
                    var id = context.Principal?.FindFirstValue("oid") ??
                        context.Principal?.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
                    if (id is null || !liveOptions.Admits(id))
                    {
                        context.Fail("This development application is limited to its approved identities.");
                        return Task.CompletedTask;
                    }
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                        identity.AddClaim(new Claim("app_sid", Guid.NewGuid().ToString("N")));
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
                options.Events = new JwtBearerEvents
                {
                    OnForbidden = context =>
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PubSubAdmission")
                            .LogWarning("Web PubSub callback authorization denied; required role={RolePresent}, expected principal={PrincipalMatches}.",
                                context.HttpContext.User.HasClaim("roles", "PubSub.Callback"),
                                context.HttpContext.User.FindFirstValue("oid") == config["Callbacks:PrincipalId"]);
                        return Task.CompletedTask;
                    }
                };
            });
            builder.Services.AddAuthorizationBuilder().AddPolicy("PubSubCallback", policy =>
                policy.AddAuthenticationSchemes("PubSub").RequireAuthenticatedUser().RequireClaim("roles", "PubSub.Callback")
                    .RequireAssertion(context => context.User.FindFirstValue("oid") == config["Callbacks:PrincipalId"]));
            builder.Services.AddSingleton<SessionTickets>();
            builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
                .Configure<SessionTickets>((o, tickets) =>
                {
                    o.SessionStore = tickets;
                    o.Cookie.Name = "teams-reference-session";
                    o.Cookie.HttpOnly = true;
                    o.Cookie.SameSite = SameSiteMode.Lax;
                    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    o.ExpireTimeSpan = TimeSpan.FromHours(8);
                    o.SlidingExpiration = false;
                    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
                });
        }
        else builder.Services.AddAuthentication();

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        if (liveOptions.GraphIntervalMilliseconds is not null || liveOptions.DataIntervalMilliseconds is not null)
            app.Logger.LogWarning("Legacy Graph interval settings are ignored. Remove them and configure Live:GraphRetry instead; requests are no longer paced.");
        var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
        foreach (var cidr in config.GetSection("App:TrustedProxyNetworks").Get<string[]>() ?? [])
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        app.UseForwardedHeaders(forwarded);
        app.UseHsts();
        var indexFile = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html");
        var inlineScripts = File.Exists(indexFile) ? Regex.Matches(File.ReadAllText(indexFile), @"<script\b[^>]*>([\s\S]*?)</script>")
            .Select(m => m.Groups[1].Value).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Replace("\r\n", "\n").Replace('\r', '\n'))
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
                await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message, access = ex.Access,
                    diagnostics = context.Items["cache-read-evidence"],
                    traceId = context.TraceIdentifier });
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
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && context.User.Identity?.IsAuthenticated != true)
                throw new AppError(401, "authentication_required", "Sign in to continue.");
            if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments("/api"))
            {
                var actor = Current(context);
                if (!liveOptions.AdmissionEnabled && context.Request.Path != "/api/session")
                    throw new AppError(503, "live_admission_disabled", "Live user admission has not been approved.");
                if (actor.TenantId != tenant || !liveOptions.Admits(actor.Id) ||
                    await context.RequestServices.GetRequiredService<IPrincipalRevocations>().IsBlockedAsync(tenant, actor.Id, context.RequestAborted))
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
        app.MapGet("/health/ready", async (ILiveStateStore store, CancellationToken ct) =>
        {
            if (role != "Receiver")
            {
                var metadata = await store.GetAsync(tenant, liveOptions.Resources[0].Id, ct);
                if (metadata is null || metadata.Value.Epoch != liveOptions.WriterEpoch)
                    throw new AppError(503, "live_preparing", "Live onboarding or writer ownership is not ready.");
            }
            return Results.Ok(new { status = "ready", mode = "live", role, admission = liveOptions.AdmissionEnabled });
        });
        if (liveOptions.Hosts("Receiver", role)) app.MapGraphCallbacks();
        if (role != "Api")
        {
            return app;
        }
        app.MapGet("/auth/csrf", (HttpContext c, IAntiforgery antiforgery) =>
            Results.Ok(new { token = antiforgery.GetAndStoreTokens(c).RequestToken }));
        app.MapGet("/auth/signin", () =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
        app.MapPost("/auth/signout", async (HttpContext c, IAntiforgery csrf) =>
        {
            try { await csrf.ValidateRequestAsync(c); }
            catch (AntiforgeryValidationException) { throw new AppError(400, "csrf_invalid", "Refresh the page before signing out."); }
            await c.RequestServices.GetRequiredService<IRealtime>().RevokeAsync(Current(c).TenantId, Current(c).Id, c.RequestAborted);
            await c.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).RequireAuthorization();

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/session", async (HttpContext c, IAntiforgery csrf) =>
        {
            var actor = Current(c);
            if (liveOptions.DelegatedSynthetic)
            {
                var authentication = await c.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (authentication.Properties?.ExpiresUtc is not { } expiry)
                    throw new AppError(401, "synthetic_context_denied", "A bounded authenticated test session is required.");
                await c.RequestServices.GetRequiredService<SyntheticUserContexts>().SaveAsync(c.User, expiry, c.RequestAborted);
            }
            return Results.Ok(new
            {
                actor.Id,
                actor.Name,
                environment = "Development",
                source = "Microsoft Teams",
                scopedValidation = liveOptions.DelegatedSynthetic,
                backgroundAuthentication = liveOptions.DelegatedSynthetic ? "delegated-validation" : "application",
                entitlementProvider = liveOptions.EntitlementProvider,
                canStartDirectChats = liveOptions.DirectChats.Length > 0,
                canCreateDirectChats = false,
                canBrowseChannels = liveOptions.ChannelDiscovery.Enabled && liveOptions.SynchronizationEnabled,
                channelRegistry = liveOptions.ChannelDiscovery.RegistryEnabled,
                csrf = csrf.GetAndStoreTokens(c).RequestToken
            });
        });
        api.MapChannelEndpoints(liveOptions);
        api.MapGet("/conversations/{id}/access", async (string id, string? parent, HttpContext context, Messaging service,
            ILiveStateStore state, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = Current(context);
            var value = (await service.GetAuthorizedAsync(actor, id, ct)).Value;
            var access = ReadAccess.Create(Messaging.Authorize(actor, value), value, clock.GetUtcNow());
            if (parent is not null)
            {
                if (parent.Length is < 1 or > 512 || value.Kind != "channel")
                    throw new AppError(404, "access_denied", "Thread is not available.");
                var root = await state.CurrentMessageAsync(actor.TenantId, id, parent, null, ct);
                if (root is null || root.Deleted || root.CreatedAt < access.HistoryFrom)
                    throw new AppError(404, "access_denied", "Thread is not available.");
            }
            return access;
        });
        api.MapGet("/people", async (HttpContext context, ILiveStateStore state, CancellationToken ct) =>
            Results.Ok(await Discovery(context, state).PeopleAsync(Current(context), ct)));
        api.MapPost("/chats/direct", (OpenDirectChat input, HttpContext context, ILiveStateStore state, CancellationToken ct) =>
            Discovery(context, state).OpenAsync(Current(context), input.PersonId, ct));
        api.MapGet("/conversations", async (string? cursor, HttpContext c, ILiveStateStore state, ICommandTokens commands, CancellationToken ct) =>
        {
            var actor = Current(c);
            var decoded = cursor is null ? null : commands.Validate(actor, "catalog", "catalog-page", cursor);
            var page = await Discovery(c, state).CatalogAsync(actor, decoded, ct);
            if (page.Cursor is not null)
                c.Response.Headers["X-Next-Cursor"] = commands.Issue(actor, "catalog", "catalog-page", page.Cursor, TimeSpan.FromMinutes(5));
            return page.Items;
        });
        api.MapGet("/conversations/{id}/cache-diagnostics", async (string id, HttpContext c, ILiveStateStore state, CancellationToken ct) =>
        {
            var snapshot = await state.InspectCacheAsync(Current(c), id, ct);
            if (snapshot.CosmosError is not null || snapshot.Redis.Status == "unavailable")
                app.Logger.LogWarning("Cache inspection incomplete; cosmos={Cosmos}, redis={Redis}, trace={Trace}.",
                    snapshot.CosmosError, snapshot.Redis.Status, c.TraceIdentifier);
            return Results.Ok(snapshot);
        });
        api.MapPost("/conversations/{id}/cache-diagnostics/cleanup", async (string id, CacheCleanupCommand command,
            HttpContext c, ILiveStateStore state, CancellationToken ct) =>
        {
            var result = await state.CleanCacheAsync(Current(c), id, command, ct);
            if (result.CosmosError is not null || result.RedisError is not null)
                app.Logger.LogWarning("Cache cleanup incomplete; cosmos={Cosmos}, redis={Redis}, trace={Trace}.",
                    result.CosmosError, result.RedisError, c.TraceIdentifier);
            else app.Logger.LogInformation("Cache cleanup progress; cosmos={Cosmos}, redis={Redis}, trace={Trace}.",
                result.CosmosState, result.RedisState, c.TraceIdentifier);
            return Results.Json(result, statusCode: result.Complete ? 200 : 202);
        });
        api.MapGet("/conversations/{id}/cache-diagnostics/cleanup/{operationId}", (string id, string operationId,
            HttpContext c, ILiveStateStore state, CancellationToken ct) =>
            state.CacheCleanupAsync(Current(c), id, operationId, ct));
        api.MapGet("/conversations/{id}/messages", async (string id, string? cursor, string? parent, long? minimumRevision, bool? diagnostics, bool? preview,
            HttpContext c, Messaging service, CancellationToken ct) =>
        {
            var evidence = diagnostics == true || c.Request.Headers["X-Cache-Diagnostics"] == "true"
                ? new ReadEvidence("page", c.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()) : null;
            c.Items["cache-read-evidence"] = evidence;
            var page = await service.ReadAsync(Current(c), id, cursor, parent, minimumRevision ?? 0, ct, evidence, preview == true);
            if (!page.Pending) return Results.Ok(page with { Diagnostics = evidence });
            c.Response.Headers.RetryAfter = "1";
            return Results.Json(new PendingRead("pending", 1000, page.Access, evidence), statusCode: 202);
        });
        api.MapGet("/conversations/{id}/changes", async (string id, long? after, string? parent, long? minimumRevision, bool? diagnostics,
            HttpContext c, Messaging service, CancellationToken ct) =>
        {
            var evidence = diagnostics == true || c.Request.Headers["X-Cache-Diagnostics"] == "true"
                ? new ReadEvidence("delta", c.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()) : null;
            c.Items["cache-read-evidence"] = evidence;
            var page = await service.ChangesAsync(Current(c), id, after ?? 0, ct, parent, minimumRevision ?? 0, evidence);
            return page with { Diagnostics = evidence };
        });
        api.MapPost("/conversations/{id}/messages", async (string id, SendCommand command, HttpContext c, Messaging service, CancellationToken ct) =>
        {
            var result = await service.SendAsync(Current(c), id, command, ct);
            return Results.Json(result, statusCode: result.State == OperationState.Confirmed ? 201 : result.State == OperationState.Rejected ? 422 : 202);
        });
        api.MapGet("/conversations/{id}/operations/{operationId}", (string id, string operationId, HttpContext c, Messaging service, CancellationToken ct) =>
            service.StatusAsync(Current(c), id, operationId, ct));
        api.MapPost("/realtime/negotiate", async (HttpContext c) =>
        {
            if (!config.GetValue<bool>("App:RealtimeAdmissionReady"))
                throw new AppError(503, "realtime_preparing", "Real-time admission is awaiting deployment readiness.");
            var actor = Current(c);
            var client = c.RequestServices.GetRequiredService<WebPubSubServiceClient>();
            var access = await client.GetClientAccessUriAsync(TimeSpan.FromMinutes(5), AzureRealtime.User(actor.TenantId, actor.Id),
                roles: [], cancellationToken: c.RequestAborted);
            return Results.Ok(new { transport = "webpubsub", url = access.AbsoluteUri });
        });
        app.MapMethods("/callbacks/pubsub", ["OPTIONS"], (HttpContext c) =>
        {
            PubSubAdmission.ValidateOrigin(c.Request, new Uri(config["Azure:PubSub"]!).Host);
            c.Response.Headers["WebHook-Allowed-Origin"] = c.Request.Headers["WebHook-Request-Origin"];
            return Results.Ok();
        }).AllowAnonymous();
        app.MapPost("/callbacks/pubsub", async (HttpContext c, IPrincipalRevocations revocations) =>
        {
            if (c.Request.Headers["ce-type"] != "azure.webpubsub.sys.connect") return Results.NoContent();
            IReadOnlySet<string>? admitted = liveOptions.DelegatedSynthetic ? liveOptions.SyntheticActorIds.ToHashSet(StringComparer.Ordinal) : null;
            var result = await PubSubAdmission.ConnectAsync(c.Request, new Uri(config["Azure:PubSub"]!).Host, tenant,
                admitted, revocations, liveOptions.AdmissionEnabled, c.RequestAborted);
            return result.Subprotocol is null
                ? Results.Ok(new { userId = result.UserId, groups = Array.Empty<string>(), roles = Array.Empty<string>() })
                : Results.Ok(new { userId = result.UserId, groups = Array.Empty<string>(), roles = Array.Empty<string>(), subprotocol = result.Subprotocol });
        }).RequireAuthorization("PubSubCallback");
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.Map("/api/{**path}", () => Results.NotFound(new { code = "route_not_found", message = "This API route does not exist." }));
        app.Map("/auth/{**path}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");
        return app;

        LiveDiscovery Discovery(HttpContext context, ILiveStateStore state) => new(state, liveOptions, tenant,
            context.RequestServices.GetRequiredService<ILogger<LiveDiscovery>>(),
            context.RequestServices.GetRequiredService<TimeProvider>());
    }
    public static Actor Current(HttpContext c) => new(
            c.User.FindFirstValue("tid") ?? c.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid")
                ?? throw new AppError(401, "authentication_required", "The tenant claim is missing. Sign in again."),
            c.User.FindFirstValue("oid") ?? c.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                ?? throw new AppError(401, "authentication_required", "The principal claim is missing. Sign in again."),
            c.User.FindFirstValue("name") ?? c.User.Identity?.Name ?? "Member",
            c.User.FindFirstValue("app_sid") ?? throw new AppError(401, "authentication_required", "Sign in to establish a new session."));
}
public partial class Program;
