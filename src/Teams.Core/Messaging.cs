using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Teams.Core;

public sealed class Messaging(ILiveStateStore store, IGraphTransport graph, ICommandBudget budget,
    ICommandTokens tokens, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public static Member Authorize(Actor actor, Conversation conversation)
    {
        if (actor.TenantId != conversation.TenantId)
            throw new AppError(404, "access_denied", "Conversation is not available.");
        return conversation.Members.SingleOrDefault(m => m.Id == actor.Id && m.Active)
            ?? throw new AppError(404, "access_denied", "Conversation is not available.");
    }

    public async Task<Versioned<Conversation>> GetAuthorizedAsync(Actor actor, string id, CancellationToken ct)
    {
        if (await store.IsBlockedAsync(actor.TenantId, actor.Id, ct))
            throw new AppError(401, "authentication_required", "This account no longer has access.");
        var value = await store.GetAsync(actor.TenantId, id, ct)
            ?? throw new AppError(404, "access_denied", "Conversation is not available.");
        value = value with { Value = value.Value with { Members = [await store.GetMemberAsync(actor, id, false, ct)] } };
        Authorize(actor, value.Value);
        return value;
    }

    public async Task<IReadOnlyList<ConversationView>> ListAsync(Actor actor, CancellationToken ct)
    {
        if (await store.IsBlockedAsync(actor.TenantId, actor.Id, ct))
            throw new AppError(401, "authentication_required", "This account no longer has access.");
        return (await store.CatalogAsync(actor, null, ct)).Items;
    }

    public async Task<MessagePage> ReadAsync(Actor actor, string id, string? cursor, string? parent,
        long minimumRevision, CancellationToken ct)
    {
        var conversation = (await GetAuthorizedAsync(actor, id, ct)).Value;
        if (!conversation.CoverageReady || conversation.Revision < minimumRevision)
            throw new AppError(503, "projection_preparing", "Recent content is being synchronized. Try again.");
        var member = Authorize(actor, conversation);
        var boundary = cursor is null ? null
            : JsonSerializer.Deserialize<PageBoundary>(tokens.Validate(actor, id, $"page:{conversation.Epoch}:{parent}", cursor));
        var parentMessage = parent is null ? null : await store.MessageAsync(actor.TenantId, id, parent, null, ct);
        if (parent is not null && (parentMessage is null || parentMessage.Deleted ||
            parentMessage.CreatedAt < member.HistoryFrom || parentMessage.CreatedAt < Now.AddDays(-7)))
            throw new AppError(404, "access_denied", "Thread is not available.");
        var messages = await store.MessagesAsync(actor, id, parent, member.HistoryFrom, boundary?.At, boundary?.Id, ct);
        var nextCursor = messages.Count > 20 ? tokens.Issue(actor, id, $"page:{conversation.Epoch}:{parent}",
            JsonSerializer.Serialize(new PageBoundary(messages[19].CreatedAt, messages[19].Id)), TimeSpan.FromMinutes(15)) : null;
        return new(messages.Take(20).OrderBy(m => m.CreatedAt).ToArray(), nextCursor, conversation.Revision,
            tokens.Issue(actor, id, $"command:{conversation.Epoch}", Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5)),
            parentMessage);
    }

    public async Task<ChangePage> ChangesAsync(Actor actor, string id, long after, CancellationToken ct)
    {
        if (after < 0) throw new AppError(400, "invalid_revision", "Revision cannot be negative.");
        var conversation = (await GetAuthorizedAsync(actor, id, ct)).Value;
        var member = Authorize(actor, conversation);
        var cutoff = member.HistoryFrom > Now.AddDays(-7) ? member.HistoryFrom : Now.AddDays(-7);
        var page = await store.ChangesAsync(actor, id, after, ct);
        return page with
        {
            HistoryFrom = page.HistoryFrom ?? cutoff,
            Window = tokens.Issue(actor, id, $"command:{conversation.Epoch}", Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5))
        };
    }

    public async Task<OperationView> SendAsync(Actor actor, string id, SendCommand command, CancellationToken ct)
    {
        if (!Guid.TryParseExact(command.OperationId, "D", out _) || string.IsNullOrWhiteSpace(command.Body) ||
            command.Body.Length > 4096 || command.Body.IndexOf('\0') >= 0)
            throw new AppError(400, "invalid_message", "Use a valid operation ID and 1-4096 characters.");
        var conversation = (await GetAuthorizedAsync(actor, id, ct)).Value;
        await store.GetMemberAsync(actor, id, true, ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { command.Body, command.ParentId }))));
        var existing = await store.GetOperationAsync(actor.TenantId, id, command.OperationId, ct);
        if (existing is not null)
        {
            if (existing.Value.ActorId != actor.Id)
                throw new AppError(404, "access_denied", "Operation is not available.");
            if (existing.Value.Fingerprint != fingerprint)
                throw new AppError(409, "idempotency_conflict", "This operation ID already has different content.");
            return View(existing.Value);
        }
        tokens.Validate(actor, id, $"command:{conversation.Epoch}", command.Window);
        if (command.ParentId is not null)
        {
            var member = Authorize(actor, conversation);
            var parentMessage = await store.MessageAsync(actor.TenantId, id, command.ParentId, null, ct);
            if (conversation.Kind != "channel" || parentMessage is null || parentMessage.Deleted ||
                parentMessage.CreatedAt < member.HistoryFrom || parentMessage.CreatedAt < Now.AddDays(-7))
                throw new AppError(400, "invalid_parent", "The reply's parent is not available.");
        }
        var operation = new SendOperation(command.OperationId, actor.TenantId, id, actor.Id, fingerprint,
            "", command.ParentId, OperationState.Pending, Now, Epoch: conversation.Epoch);
        if (!await store.CreateOperationAsync(operation, ct))
            return await SendAsync(actor, id, command, ct);
        var stored = await store.GetOperationAsync(actor.TenantId, id, operation.Id, ct)
            ?? throw new AppError(503, "operation_unavailable", "Operation evidence is temporarily unavailable.");
        try { await budget.AcquireAsync(actor, conversation, ct); }
        catch (AppError ex) when (ex.Status == 429)
        {
            await store.SaveOperationAsync(stored with { Value = stored.Value with { State = OperationState.Rejected, Error = ex.Message } }, ct);
            return View((await store.GetOperationAsync(actor.TenantId, id, operation.Id, ct))?.Value
                ?? throw new AppError(503, "operation_unavailable", "Operation evidence is temporarily unavailable."));
        }
        conversation = (await GetAuthorizedAsync(actor, id, ct)).Value;
        await store.GetMemberAsync(actor, id, true, ct);
        operation = stored.Value with { State = OperationState.Dispatching };
        if (!await store.SaveOperationAsync(stored with { Value = operation }, ct))
            return View((await store.GetOperationAsync(actor.TenantId, id, operation.Id, ct))!.Value);
        WriteResult result;
        try
        {
            result = await graph.SendAsync(actor, conversation, operation with { Body = command.Body }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Once dispatch began, transport failure says nothing about whether Graph committed.
            result = new(OperationState.Unknown, null, "Commitment could not be established. Do not resend.");
        }
        var outcome = await ApplyResultAsync(actor, conversation.Id, operation.Id, result, CancellationToken.None);
        LiveTelemetry.WriteOutcomes.Add(1, new KeyValuePair<string, object?>("state", result.State.ToString()));
        if (result.RetryAfter is { } delay && budget is IGraphThrottle throttle)
            await throttle.CooldownAsync(actor, delay, CancellationToken.None);
        return outcome;
    }

    public async Task<OperationView> StatusAsync(Actor actor, string conversationId, string operationId, CancellationToken ct)
    {
        var conversation = (await GetAuthorizedAsync(actor, conversationId, ct)).Value;
        var operation = await store.GetOperationAsync(actor.TenantId, conversationId, operationId, ct);
        if (operation is null || operation.Value.ActorId != actor.Id)
            throw new AppError(404, "access_denied", "Operation is not available.");
        if (operation.Value.State == OperationState.Pending && Now - operation.Value.CreatedAt > TimeSpan.FromSeconds(35))
        {
            await store.SaveOperationAsync(operation with
            {
                Value = operation.Value with
                {
                    State = OperationState.Rejected,
                    Error = "This operation expired before dispatch. No message was created."
                }
            }, ct);
            operation = await store.GetOperationAsync(actor.TenantId, conversationId, operationId, ct) ?? operation;
        }
        if (operation.Value.State == OperationState.Dispatching && Now - operation.Value.CreatedAt > TimeSpan.FromSeconds(35))
        {
            await store.SaveOperationAsync(operation with
            {
                Value = operation.Value with
                {
                    State = OperationState.Unknown,
                    Error = "Dispatch evidence is incomplete. Do not resend."
                }
            }, ct);
            operation = await store.GetOperationAsync(actor.TenantId, conversationId, operationId, ct) ?? operation;
        }
        if (operation.Value.State == OperationState.Unknown)
        {
            var resolution = await graph.ResolveAsync(actor, conversation, operation.Value, ct);
            if (resolution is not null)
                return await ApplyResultAsync(actor, conversationId, operationId, resolution, ct);
        }
        return View(operation.Value);
    }

    private async Task<OperationView> ApplyResultAsync(Actor actor, string id, string operationId, WriteResult result, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var conversation = await store.GetAsync(actor.TenantId, id, ct)
                ?? throw new AppError(503, "projection_unavailable", "Conversation state is unavailable.");
            var operation = await store.GetOperationAsync(actor.TenantId, id, operationId, ct)
                ?? throw new AppError(503, "operation_unavailable", "Operation evidence is unavailable.");
            var updated = operation.Value with
            {
                State = result.State,
                GraphMessageId = result.Message?.Id,
                Error = result.Error,
                Body = ""
            };
            var projection = result.Message is null || operation.Value.Epoch != conversation.Value.Epoch
                ? conversation.Value : Projection.Apply(conversation.Value, result.Message, Now);
            if (await store.CommitAsync(conversation with { Value = projection }, operation with { Value = updated }, ct))
                return View(updated);
        }
        throw new AppError(503, "confirmation_persistence_unavailable",
            "Confirmation could not be recorded. Check the original operation; do not resend.");
    }

    private static OperationView View(SendOperation op) => new(op.Id, op.State, op.GraphMessageId, op.Error);
    private sealed record PageBoundary(DateTimeOffset At, string Id);
}

public static class Projection
{
    public static Conversation Apply(Conversation value, Message message, DateTimeOffset now)
    {
        if (message.CreatedAt < now.AddDays(-7)) return value;
        var tombstones = (value.Tombstones ?? []).Where(t => t.At >= now.AddDays(-14)).ToList();
        if (!message.Deleted && tombstones.Any(t => t.MessageId == message.Id)) return value;
        var previous = value.Messages.SingleOrDefault(m => m.Id == message.Id);
        if (previous is not null && (previous.SourceVersion == message.SourceVersion ||
            message.ModifiedAt < previous.ModifiedAt || previous.Deleted && !message.Deleted))
            return value;
        var revision = value.Revision + 1;
        if (message.Deleted && !tombstones.Any(t => t.MessageId == message.Id))
        {
            if (tombstones.Count >= 2000)
                return value with
                {
                    CoverageReady = false,
                    Messages = [],
                    Revision = revision,
                    Changes = [new(revision, message.Id, now)]
                };
            tombstones.Add(new(message.Id, now));
        }
        var messages = value.Messages.Where(m => m.Id != message.Id && m.CreatedAt >= now.AddDays(-7))
            .Append(message.Deleted ? message with { Body = "", ContentNotice = null, TeamsUrl = null, HtmlBody = null } : message)
            .OrderByDescending(m => m.CreatedAt).Take(200).ToList();
        var bytes = messages.Sum(m => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(m)));
        while (bytes > 1_000_000 && messages.Count > 1)
        {
            bytes -= Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(messages[^1]));
            messages.RemoveAt(messages.Count - 1);
        }
        var changes = value.Changes.Append(new Change(revision, message.Id, now, message.ParentId)).TakeLast(200).ToList();
        return value with { Messages = messages, Changes = changes, Revision = revision, Tombstones = tombstones };
    }
}
