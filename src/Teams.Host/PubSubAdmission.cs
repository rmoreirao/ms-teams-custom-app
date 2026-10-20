using System.Text.Json;
using Teams.Core;

namespace Teams.Host;

public sealed record PubSubConnectResult(string UserId, string? Subprotocol);

public static class PubSubAdmission
{
    private static readonly string[] Protocols = ["json.reliable.webpubsub.azure.v1", "json.webpubsub.azure.v1"];
    public static void ValidateOrigin(HttpRequest request, string expectedHost)
    {
        if (!string.Equals(request.Headers["WebHook-Request-Origin"].ToString(), expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new AppError(403, "callback_origin_invalid", "The callback origin is not the configured Web PubSub service.");
    }
    public static async Task<PubSubConnectResult> ConnectAsync(HttpRequest request, string expectedHost, string tenant,
        IReadOnlySet<string>? admittedActors, IPrincipalRevocations revocations, bool admissionEnabled, CancellationToken ct)
    {
        ValidateOrigin(request, expectedHost);
        if (request.Headers["ce-specversion"] != "1.0" || request.Headers["ce-type"] != "azure.webpubsub.sys.connect" ||
            request.Headers["ce-hub"] != "messaging")
            throw new AppError(400, "callback_event_invalid", "The callback is not the expected connect event.");
        if (!admissionEnabled) throw new AppError(403, "live_admission_disabled", "Live admission is disabled.");
        var user = request.Headers["ce-userId"].ToString();
        var parts = user.Split(':');
        if (parts is not { Length: 2 } || parts[0] != tenant || !Guid.TryParseExact(parts[1], "D", out _) ||
            admittedActors is not null && !admittedActors.Contains(parts[1]) ||
            await revocations.IsBlockedAsync(tenant, parts[1], ct))
            throw new AppError(403, "access_denied", "This connection is not authorized.");
        JsonDocument payload;
        try { payload = await JsonDocument.ParseAsync(request.Body, new() { MaxDepth = 16 }, ct); }
        catch (JsonException) { throw new AppError(400, "callback_payload_invalid", "The connect-event payload is invalid."); }
        using (payload)
        {
            var requested = payload.RootElement.TryGetProperty("subprotocols", out var values) ?
                values.ValueKind == JsonValueKind.Array && values.GetArrayLength() <= 16
                    ? values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null).ToArray()
                    : throw new AppError(400, "callback_protocol_invalid", "The requested client protocols are invalid.") : [];
            if (requested.Length == 0) return new(user, null);
            var protocol = Protocols.FirstOrDefault(value => requested.Contains(value));
            return protocol is null ? throw new AppError(400, "callback_protocol_invalid", "The client protocol is not supported.") : new(user, protocol);
        }
    }
}
