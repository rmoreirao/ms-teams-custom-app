using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;
using Teams.Core;

namespace Teams.Host;

public sealed class CommandTokens(IDataProtectionProvider protection, TimeProvider clock) : ICommandTokens
{
    private sealed record Payload(string Value, DateTimeOffset Expires);
    private IDataProtector Protector(Actor actor, string conversation, string purpose) =>
        protection.CreateProtector("teams-reference-v1", actor.TenantId, actor.Id, actor.SessionId, conversation, purpose);
    public string Issue(Actor actor, string conversation, string purpose, string value, TimeSpan lifetime) =>
        Protector(actor, conversation, purpose).Protect(JsonSerializer.Serialize(new Payload(value, clock.GetUtcNow() + lifetime)));
    public string Validate(Actor actor, string conversation, string purpose, string token)
    {
        try
        {
            if (token.Length > 4096) throw new CryptographicException();
            var payload = JsonSerializer.Deserialize<Payload>(Protector(actor, conversation, purpose).Unprotect(token));
            if (payload is null || payload.Expires <= clock.GetUtcNow()) throw new CryptographicException();
            return payload.Value;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            throw new AppError(410, "command_window_expired", "This view or command window expired. Refresh the conversation.");
        }
    }
}

public sealed class SessionTickets(IDistributedCache cache, IDataProtectionProvider protection, ILogger<SessionTickets> logger) : ITicketStore
{
    private readonly IDataProtector protector = protection.CreateProtector("teams-reference-session-v1");
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = $"session:{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
        await RenewAsync(key, ticket);
        return key;
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket) =>
        cache.SetAsync(key, protector.Protect(TicketSerializer.Default.Serialize(ticket)), new DistributedCacheEntryOptions
        { AbsoluteExpiration = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8) });
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var value = await cache.GetAsync(key);
        if (value is null) return null;
        try { return TicketSerializer.Default.Deserialize(protector.Unprotect(value)); }
        catch (CryptographicException)
        {
            logger.LogWarning("A session ticket could not be unprotected; invalidating it and requiring sign-in.");
            await cache.RemoveAsync(key);
            return null;
        }
    }
    public Task RemoveAsync(string key) => cache.RemoveAsync(key);
}
