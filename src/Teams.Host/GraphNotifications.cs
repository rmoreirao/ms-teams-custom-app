using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Teams.Core;

namespace Teams.Host;

public sealed record EncryptedNotification(string TenantId, string ResourceId, string ResourcePath,
    string ChangeType, JsonElement? EncryptedContent, DateTimeOffset ReceivedAt, string? LifecycleEvent = null, int Offset = 0,
    string? SubscriptionId = null, int SchemaVersion = 1);
public interface IEncryptedNotificationInbox
{
    Task EnqueueAsync(IReadOnlyList<EncryptedNotification> notifications, CancellationToken ct);
}
public interface INotificationTokenValidator
{
    Task ValidateAsync(IReadOnlyList<string> tokens, CancellationToken ct);
}
public sealed class NotificationTokenValidator(string tenant, string clientId,
    IConfigurationManager<OpenIdConnectConfiguration> configuration) : INotificationTokenValidator
{
    public async Task ValidateAsync(IReadOnlyList<string> tokens, CancellationToken ct)
    {
        if (tokens.Count is < 1 or > 10) throw new SecurityTokenValidationException("Validation tokens are required.");
        var metadata = await configuration.GetConfigurationAsync(ct);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 32768 };
        foreach (var token in tokens)
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidAudience = clientId,
                ValidIssuers = [$"https://sts.windows.net/{tenant}/", $"https://login.microsoftonline.com/{tenant}/v2.0"],
                IssuerSigningKeys = metadata.SigningKeys, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateAudience = true,
                ValidateIssuer = true, ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);
            var version = principal.FindFirst("ver")?.Value;
            var publisher = version == "1.0" ? principal.FindFirst("appid")?.Value :
                version == "2.0" ? principal.FindFirst("azp")?.Value : null;
            if (principal.FindFirst("tid")?.Value != tenant || publisher != "0bf30f3b-4a52-48df-9a82-234910c4a086")
                throw new SecurityTokenValidationException("Notification publisher or tenant is invalid.");
        }
    }
}
public sealed class NotificationIntake(LiveOptions options, string tenant, LiveControlStore control,
    INotificationTokenValidator tokens, IEncryptedNotificationInbox inbox, TimeProvider clock)
{
    public async Task AcceptAsync(JsonElement payload, bool lifecycle, CancellationToken ct)
    {
        using var measurement = LiveTelemetry.Measure("notification-intake");
        if (!options.SynchronizationEnabled)
            throw new AppError(503, "synchronization_disabled", "Notification persistence is awaiting synchronization approval.");
        var values = payload.GetProperty("value");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 100)
            throw new AppError(400, "notification_invalid", "Notification collection is invalid.");
        if (!lifecycle)
        {
            var validation = payload.GetProperty("validationTokens");
            await tokens.ValidateAsync(validation.EnumerateArray().Select(t => t.GetString()
                ?? throw new SecurityTokenValidationException("Invalid validation token.")).ToArray(), ct);
        }
        List<EncryptedNotification> accepted = [];
        foreach (var item in values.EnumerateArray())
        {
            var id = item.GetProperty("subscriptionId").GetString() ?? "";
            var subscription = await control.FindSubscriptionAsync(id, ct)
                ?? throw new AppError(403, "subscription_unknown", "Subscription is not registered.");
            var configured = options.SubscriptionScopes.SingleOrDefault(s => s.Id == subscription.ScopeId);
            var state = item.GetProperty("clientState").GetString() ?? "";
            if (configured is null || subscription.GraphId != id || configured.Path != subscription.Resource || item.GetProperty("tenantId").GetString() != tenant ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(state)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(subscription.ClientState))))
                throw new AppError(403, "notification_invalid", "Subscription scope, tenant or validation state does not match.");
            if (lifecycle)
            {
                var action = item.GetProperty("lifecycleEvent").GetString();
                if (action is not ("reauthorizationRequired" or "subscriptionRemoved" or "missed"))
                    throw new AppError(400, "lifecycle_unsupported", "Unsupported lifecycle event.");
                accepted.Add(new(tenant, $"scope:{configured.Id}", configured.Path, "", null, clock.GetUtcNow(), action,
                    SubscriptionId: id));
                continue;
            }
            var path = GraphMessages.NormalizeResource(item.GetProperty("resource").GetString() ?? "");
            var approved = options.Resources.Where(configured.Covers).SingleOrDefault(r =>
                path.StartsWith(r.Path + "/", StringComparison.Ordinal));
            // Aggregate scopes can cover resources outside the approved projection; never persist those payloads.
            if (approved is null) { LiveTelemetry.FilteredNotifications.Add(1); continue; }
            var suffix = path[(approved.Path.Length + 1)..].Split('/');
            if (suffix.Length != 1 && (suffix.Length != 3 || suffix[1] != "replies" || approved.Kind != "channel"))
                throw new AppError(400, "notification_resource_invalid", "Message resource context is invalid.");
            var change = item.GetProperty("changeType").GetString();
            if (change is not ("created" or "updated" or "deleted")) throw new AppError(400, "change_unsupported", "Unsupported message change.");
            var encrypted = item.GetProperty("encryptedContent");
            var key = encrypted.GetProperty("encryptionCertificateId").GetString() ?? "";
            if (!options.NotificationKeys.ContainsKey(key))
                throw new AppError(503, "notification_key_unknown", "The notification key version is not available.");
            accepted.Add(new(tenant, approved.Id, path, change, encrypted.Clone(), clock.GetUtcNow()));
        }
        if (accepted.Count > 0)
        {
            await inbox.EnqueueAsync(accepted, ct);
            LiveTelemetry.AcceptedNotifications.Add(accepted.Count);
        }
    }
}
public interface INotificationKeyDecryptor
{
    Task<byte[]> DecryptAsync(string certificateId, byte[] encryptedKey, CancellationToken ct);
}
public sealed class VaultNotificationKeys(LiveOptions options, TokenCredential credential) : INotificationKeyDecryptor
{
    public async Task<byte[]> DecryptAsync(string certificateId, byte[] encryptedKey, CancellationToken ct)
    {
        if (!options.NotificationKeys.TryGetValue(certificateId, out var uri))
            throw new AppError(503, "notification_key_unknown", "Notification decryption key is not configured.");
        var client = new CryptographyClient(new Uri(uri), credential);
        return (await client.DecryptAsync(EncryptionAlgorithm.RsaOaep, encryptedKey, ct)).Plaintext;
    }
}
public sealed class NotificationDecryptor(INotificationKeyDecryptor keys)
{
    public async Task<JsonElement> DecryptAsync(JsonElement encrypted, CancellationToken ct)
    {
        var key = await keys.DecryptAsync(encrypted.GetProperty("encryptionCertificateId").GetString() ?? "",
            Convert.FromBase64String(encrypted.GetProperty("dataKey").GetString() ?? ""), ct);
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid Graph AES key size.");
            var data = Convert.FromBase64String(encrypted.GetProperty("data").GetString() ?? "");
            var signature = Convert.FromBase64String(encrypted.GetProperty("dataSignature").GetString() ?? "");
            if (data.Length is < 16 or > 524288 || !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, data), signature))
                throw new CryptographicException("Notification signature validation failed.");
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(data, key.AsSpan(0, 16), PaddingMode.PKCS7);
            try
            {
                using var document = JsonDocument.Parse(plain, new() { MaxDepth = 32 });
                return document.RootElement.Clone();
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
