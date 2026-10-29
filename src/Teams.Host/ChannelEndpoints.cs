using System.Text.Json;
using Teams.Core;

namespace Teams.Host;

public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this RouteGroupBuilder api, LiveOptions options)
    {
        if (!options.ChannelDiscovery.RegistryEnabled) return;
        api.MapGet("/teams", (string requestId, string? cursor, HttpContext context) =>
            RequestAsync(context, options, requestId, "teams", null, null, cursor));
        api.MapGet("/teams/{teamId}/channels", (string teamId, string requestId, string? cursor, HttpContext context) =>
            RequestAsync(context, options, requestId, "channels", teamId, null, cursor));
        api.MapPost("/channels/open", (OpenChannel input, HttpContext context) =>
        {
            var actor = TeamsApplication.Current(context);
            var tokens = context.RequestServices.GetRequiredService<ICommandTokens>();
            var selection = JsonSerializer.Deserialize<ChannelSelection>(
                tokens.Validate(actor, "channel-discovery", "channel-selection", input.Selection))
                ?? throw new AppError(400, "channel_selection_invalid", "Choose a channel from Browse channels.");
            return RequestAsync(context, options, input.OperationId, "open", selection.TeamId, selection.ChannelId, null);
        });
        api.MapGet("/channel-operations/{operationId}", async (string operationId, HttpContext context) =>
        {
            if (!Guid.TryParseExact(operationId, "D", out _))
                throw new AppError(400, "channel_operation_invalid", "Use the original operation ID.");
            var actor = TeamsApplication.Current(context);
            var operation = await context.RequestServices.GetRequiredService<ChannelWorkflowStore>()
                .GetAsync(actor.Id, operationId, context.RequestAborted)
                ?? throw new AppError(404, "channel_operation_missing", "This channel operation is not available.");
            return await SnapshotAsync(context, operation.Value);
        });
        api.MapGet("/realtime/changes", (long? after, HttpContext context) =>
            context.RequestServices.GetRequiredService<ChannelWorkflowStore>().ChangesAsync(
                TeamsApplication.Current(context).Id, after ?? 0, context.RequestAborted));
    }
    private static async Task<IResult> RequestAsync(HttpContext context, LiveOptions options, string id, string kind,
        string? team, string? channel, string? cursor)
    {
        if (!options.ChannelDiscovery.Enabled || !options.SynchronizationEnabled)
            throw new AppError(403, "channel_discovery_disabled", "Browsing additional channels is not enabled for this workspace.");
        var actor = TeamsApplication.Current(context);
        var services = context.RequestServices;
        var continuation = cursor is null ? null : services.GetRequiredService<ICommandTokens>()
            .Validate(actor, "channel-discovery", $"channel-page:{kind}:{team}", cursor);
        var work = new ChannelWork(actor.TenantId, actor.Id, actor.Name, id, kind, team, channel, continuation);
        var operation = await services.GetRequiredService<ChannelWorkflowStore>().CreateAsync(work, context.RequestAborted);
        if (operation.State is "pending" or "deferred" or "preparing")
            await services.GetRequiredService<IChannelWorkQueue>().EnqueueAsync(work, null, context.RequestAborted);
        context.Response.Headers.Location = $"/api/channel-operations/{id}";
        return await SnapshotAsync(context, operation);
    }
    private static async Task<IResult> SnapshotAsync(HttpContext context, ChannelOperation operation)
    {
        var actor = TeamsApplication.Current(context);
        if (actor.Id != operation.ActorId)
            throw new AppError(404, "channel_operation_missing", "This channel operation is not available.");
        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        if (operation.Kind != "open" && operation.State == "ready" && operation.FreshUntil <= clock.GetUtcNow())
            throw new AppError(410, "channel_discovery_expired", "Channel discovery has expired. Browse again to verify current access.");
        var tokens = context.RequestServices.GetRequiredService<ICommandTokens>();
        var lifetime = operation.FreshUntil is { } until ? until - clock.GetUtcNow() : TimeSpan.FromMinutes(1);
        ConversationView? conversation = null;
        if (operation.ConversationId is { } id && operation.State == "ready")
        {
            var state = context.RequestServices.GetRequiredService<ILiveStateStore>();
            var member = await state.GetMemberAsync(actor, id, false, context.RequestAborted);
            var metadata = await state.GetAsync(actor.TenantId, id, context.RequestAborted)
                ?? throw new AppError(404, "channel_unavailable", "This channel is no longer available.");
            conversation = new(id, metadata.Value.Title, "channel", metadata.Value.Team, metadata.Value.Revision,
                TeamId: metadata.Value.GraphTeamId, CanPost: member.CanSend, CanReply: member.CanSend,
                CapabilityReason: member.CanSend ? null : "Posting is not enabled for this channel.");
        }
        var body = new
        {
            operation.Id, operation.Kind, operation.State, operation.Revision, operation.Error, conversation,
            teams = operation.Teams,
            channels = operation.Channels?.Select(channel => new
            {
                channel.Id, channel.TeamId, channel.Name, channel.TeamName,
                selection = tokens.Issue(actor, "channel-discovery", "channel-selection",
                    JsonSerializer.Serialize(new ChannelSelection(channel.TeamId, channel.Id)), lifetime)
            }),
            cursor = operation.Continuation is null ? null : tokens.Issue(actor, "channel-discovery",
                $"channel-page:{operation.Kind}:{operation.TeamId}",
                operation.Continuation, lifetime)
        };
        return Results.Json(body, statusCode: operation.State is "pending" or "deferred" or "preparing" ? 202 : 200);
    }
}
