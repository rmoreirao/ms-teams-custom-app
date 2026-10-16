using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Client;

namespace Teams.Host;

public static class PublicAuthenticationOrigin
{
    public static IServiceCollection AddCanonicalOidcCallback(this IServiceCollection services, string? publicOrigin)
    {
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("App:PublicOrigin must be an HTTPS origin without a path, credentials, query or fragment.");

        var callback = new Uri(origin, "/auth/callback").AbsoluteUri;
        services.Configure<ConfidentialClientApplicationOptions>(OpenIdConnectDefaults.AuthenticationScheme,
            options => options.RedirectUri = callback);
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.CallbackPath = "/auth/callback";
            var previous = options.Events.OnRedirectToIdentityProvider;
            options.Events.OnRedirectToIdentityProvider = async context =>
            {
                await previous(context);
                context.ProtocolMessage.RedirectUri = callback;
            };
        });
        return services;
    }
}
