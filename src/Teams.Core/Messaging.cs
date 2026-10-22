using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Teams.Core;

public sealed class Messaging(ILiveStateStore store, IGraphTransport graph, ICommandBudget budget,
    ICommandTokens tokens, TimeProvider clock, IReadRefresh? refresh = null, GraphRetry? retry = null)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private readonly GraphRetry retries = retry ?? new(clock: clock);

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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        MessagePage? pending = null;
        var accepted = false;
        try
        {
            pending = await ReadPageAsync(actor, id, cursor, parent, minimumRevision, linked.Token);
            if (pending.Refresh is null) return pending;
            if (!pending.Pending)
            {
                if (refresh is not null) await refresh.RequestAsync(pending.Refresh, false, linked.Token);
                return pending with { Refresh = null };
            }
            if (refresh is null)
                throw new AppError(503, "read_refresh_unavailable", "History could not be loaded. Refresh is currently unavailable.");
            await refresh.RequestAsync(pending.Refresh, true, linked.Token);
            accepted = true;
            foreach (var delay in new[] { 0, 100, 300, 800, 1600 })
            {
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), clock, linked.Token);
                var page = await ReadPageAsync(actor, id, cursor, parent, minimumRevision, linked.Token);
                if (!page.Pending) return page with { Refresh = null };
                pending = page;
            }
            return pending;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested && pending is { Pending: false })
        {
            return pending with { Refresh = null };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested && accepted && pending is not null)
        {
            return pending;
        }
    }

    private async Task<MessagePage> ReadPageAsync(Actor actor, string id, string? cursor, string? parent,
        long minimumRevision, CancellationToken ct)
    {
        if (minimumRevision < 0)
            throw new AppError(400, "invalid_revision", "Revision cannot be negative.");
        var conversation = (await GetAuthorizedAsync(actor, id, ct)).Value;
        var member = Authorize(actor, conversation);
        var boundary = cursor is null ? null
            : JsonSerializer.Deserialize<PageBoundary>(tokens.Validate(actor, id, $"page:{conversation.Epoch}:{parent}", cursor));
        var access = ReadAccess.Create(member, conversation, Now);
        if (parent is not null && conversation.Kind != "channel")
            throw new AppError(404, "access_denied", "Thread is not available.");
        var observed = await store.ReadViewAsync(actor.TenantId, id, parent, ct);
        var view = observed?.Value.Epoch == conversation.Epoch ? observed.Value : null;
        var parentMessage = parent is null ? null : await store.CurrentMessageAsync(actor.TenantId, id, parent, null, ct);
        if (parentMessage is not null && (parentMessage.Deleted || parentMessage.CreatedAt < access.HistoryFrom) ||
            parent is not null && parentMessage is null && view?.ParentMissing == true)
            throw new AppError(404, "access_denied", "Thread is not available.");
        if (conversation.Revision < minimumRevision)
            throw new AppError(503, "projection_behind", "The reader has not reached the requested revision. Synchronizing recent context.",
                parent is not null && parentMessage is null ? null : access);
        var request = new ReadRefreshRequest(actor.TenantId, id, conversation.Epoch, parent, boundary?.At, boundary?.Id, Now);
        MessagePage Pending() => new([], null, conversation.Revision, "",
            Access: parent is not null && parentMessage is null ? null : access, Refresh: request, Pending: true);
        if (parent is not null && parentMessage is null) return Pending();
        var loaded = await store.MessagesAsync(actor, id, parent, member.HistoryFrom, boundary?.At, boundary?.Id, ct);
        var current = (await GetAuthorizedAsync(actor, id, ct)).Value;
        if (current.Epoch != conversation.Epoch)
            throw new AppError(503, "writer_fenced", "Conversation ownership changed during the read.");
        access = ReadAccess.Create(Authorize(actor, current), current, Now);
        if (parent is not null)
        {
            parentMessage = await store.CurrentMessageAsync(actor.TenantId, id, parent, null, ct);
            if (parentMessage is null || parentMessage.Deleted || parentMessage.CreatedAt < access.HistoryFrom)
                throw new AppError(404, "access_denied", "Thread is not available.");
        }
        var messages = loaded.Where(message => message.CreatedAt >= access.HistoryFrom).ToArray();
        var coverage = view is null ? await store.ReadCoverageAsync(actor.TenantId, id, ct) : null;
        var complete = view is { Complete: true, ParentMissing: false } || current.CoverageReady ||
            coverage is { Ready: true, VerifiedAt: not null } ||
            boundary?.At < access.HistoryFrom ||
            conversation.Kind == "chat" && view?.OldestAt < access.HistoryFrom;
        if (messages.Length == 0 && !complete) return Pending();
        var last = messages.Take(20).LastOrDefault();
        var nextCursor = last is not null && (messages.Length > 20 || !complete) ? tokens.Issue(actor, id, $"page:{conversation.Epoch}:{parent}",
            JsonSerializer.Serialize(new PageBoundary(last.CreatedAt, last.Id)), TimeSpan.FromMinutes(15)) : null;
        return new(messages.Take(20).OrderBy(m => m.CreatedAt).ToArray(), nextCursor, conversation.Revision,
            tokens.Issue(actor, id, $"command:{conversation.Epoch}", Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5)),
            parentMessage, access, complete ? null : request);
    }

    public async Task<ChangePage> ChangesAsync(Actor actor, string id, long after, CancellationToken ct,
        string? parent = null, long minimumRevision = 0)
    {
        if (after < 0 || minimumRevision < 0) throw new AppError(400, "invalid_revision", "Revision cannot be negative.");
        var conversation = (await GetAuthorizedAsync(actor, id, ct)).Value;
        var member = Authorize(actor, conversation);
        var cutoff = member.HistoryFrom > Now.AddDays(-7) ? member.HistoryFrom : Now.AddDays(-7);
        if (parent is not null)
        {
            var root = await store.CurrentMessageAsync(actor.TenantId, id, parent, null, ct);
            if (root is null || root.Deleted || root.CreatedAt < cutoff)
                throw new AppError(404, "access_denied", "Thread is not available.");
        }
        var access = ReadAccess.Create(member, conversation, Now);
        if (conversation.Revision < minimumRevision)
            throw new AppError(503, "projection_behind", "The reader has not reached the requested revision. Synchronizing recent context.", access);
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
        await ValidateParentAsync(actor, conversation, command.ParentId, ct);
        var operation = new SendOperation(command.OperationId, actor.TenantId, id, actor.Id, fingerprint,
            "", command.ParentId, OperationState.Pending, Now, Epoch: conversation.Epoch, Deadline: retries.Deadline());
        if (!await store.CreateOperationAsync(operation, ct))
            return await SendAsync(actor, id, command, ct);
        var stored = await store.GetOperationAsync(actor.TenantId, id, operation.Id, ct)
            ?? throw new AppError(503, "operation_unavailable", "Operation evidence is temporarily unavailable.");
        var remaining = operation.Deadline!.Value - Now;
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        for (;;)
        {
            if (stored.Value.State != OperationState.Pending) return View(stored.Value);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                conversation = (await GetAuthorizedAsync(actor, id, linked.Token)).Value;
                await store.GetMemberAsync(actor, id, true, linked.Token);
                if (conversation.Epoch != stored.Value.Epoch)
                    throw new AppError(503, "writer_fenced", "Conversation ownership changed before dispatch.");
                await ValidateParentAsync(actor, conversation, command.ParentId, linked.Token);
                await budget.AcquireAsync(actor, conversation, linked.Token);
                if (Now >= stored.Value.Deadline)
                    throw new OperationCanceledException("The send deadline expired before dispatch.");
            }
            catch (Exception ex) when (ex is AppError or OperationCanceledException)
            {
                return await ApplyResultAsync(actor, id, operation.Id,
                    new(OperationState.Rejected, null, ex is AppError ? ex.Message :
                        "The send stopped before another Graph attempt. No new message was posted."), CancellationToken.None);
            }
            operation = stored.Value with
            {
                State = OperationState.Dispatching, Attempts = stored.Value.Attempts + 1,
                AttemptUntil = new[] { Now.AddSeconds(retries.Options.AttemptTimeoutSeconds), stored.Value.Deadline!.Value }.Min(),
                RetryAt = null
            };
            try
            {
                if (!await store.SaveOperationAsync(stored with { Value = operation }, linked.Token))
                    return View((await store.GetOperationAsync(actor.TenantId, id, operation.Id, CancellationToken.None))!.Value);
            }
            catch (Exception ex) when (ex is AppError or OperationCanceledException)
            {
                return await ApplyResultAsync(actor, id, operation.Id,
                    new(OperationState.Rejected, null, ex is AppError ? ex.Message :
                        "The send stopped before Graph dispatch. No new message was posted."),
                    CancellationToken.None, attempts: stored.Value.Attempts);
            }
            WriteResult result;
            try { result = await graph.SendAsync(actor, conversation, operation with { Body = command.Body }, linked.Token); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                result = new(OperationState.Unknown, null, "Commitment could not be established. Do not resend.");
            }
            if (result.Status != 429)
                return await ApplyResultAsync(actor, id, operation.Id, result, CancellationToken.None);

            var delay = retries.Delay(operation.Attempts, result.RetryAfter);
            if (linked.IsCancellationRequested || !retries.CanRetry(operation.Attempts, delay, operation.Deadline!.Value))
            {
                LiveTelemetry.GraphRetryExhausted.Add(1, new KeyValuePair<string, object?>("mode", "delegated"));
                return await ApplyResultAsync(actor, id, operation.Id, result with
                { Error = "Graph throttled this send. The retry limit or operation deadline was reached; no message was created." },
                    CancellationToken.None);
            }
            stored = await store.GetOperationAsync(actor.TenantId, id, operation.Id, CancellationToken.None)
                ?? throw new AppError(503, "operation_unavailable", "Operation evidence is temporarily unavailable.");
            if (stored.Value.State != OperationState.Dispatching)
                return await ApplyResultAsync(actor, id, operation.Id, result with
                { Error = "Graph throttled this send, but the dispatch claim expired before a retry. No message was created." },
                    CancellationToken.None);
            var waiting = stored.Value with
            {
                State = OperationState.Pending, RetryAt = Now + delay, AttemptUntil = null,
                Throttles = stored.Value.Throttles + 1, FirstStatus = stored.Value.FirstStatus ?? 429
            };
            if (!await store.SaveOperationAsync(stored with { Value = waiting }, CancellationToken.None))
                return await ApplyResultAsync(actor, id, operation.Id, result with
                { Error = "Graph throttled this send, but the dispatch claim changed before a retry. No message was created." },
                    CancellationToken.None);
            stored = await store.GetOperationAsync(actor.TenantId, id, operation.Id, CancellationToken.None)
                ?? throw new AppError(503, "operation_unavailable", "Operation evidence is temporarily unavailable.");
            LiveTelemetry.GraphRetries.Add(1, new KeyValuePair<string, object?>("mode", "delegated"));
            LiveTelemetry.GraphRetryWait.Record(delay.TotalMilliseconds, new KeyValuePair<string, object?>("mode", "delegated"));
            try { await retries.WaitAsync(delay, linked.Token); }
            catch (OperationCanceledException)
            {
                return await ApplyResultAsync(actor, id, operation.Id,
                    new(OperationState.Rejected, null, "The send stopped while waiting after Graph throttled it. No message was created."),
                    CancellationToken.None);
            }
        }
    }

    public async Task<OperationView> StatusAsync(Actor actor, string conversationId, string operationId, CancellationToken ct)
    {
        var conversation = (await GetAuthorizedAsync(actor, conversationId, ct)).Value;
        var operation = await store.GetOperationAsync(actor.TenantId, conversationId, operationId, ct);
        if (operation is null || operation.Value.ActorId != actor.Id)
            throw new AppError(404, "access_denied", "Operation is not available.");
        if (operation.Value.State == OperationState.Pending &&
            Now > (operation.Value.Deadline ?? operation.Value.CreatedAt.AddSeconds(35)))
        {
            await store.SaveOperationAsync(operation with
            {
                Value = operation.Value with
                {
                    State = OperationState.Rejected,
                    Error = "This operation expired before dispatch. No message was created.", RetryAt = null
                }
            }, ct);
            operation = await store.GetOperationAsync(actor.TenantId, conversationId, operationId, ct) ?? operation;
        }
        if (operation.Value.State == OperationState.Dispatching &&
            Now > (operation.Value.AttemptUntil ?? operation.Value.CreatedAt.AddSeconds(35)))
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

    private async Task ValidateParentAsync(Actor actor, Conversation conversation, string? parentId, CancellationToken ct)
    {
        if (parentId is null) return;
        var member = Authorize(actor, conversation);
        var parent = await store.CurrentMessageAsync(actor.TenantId, conversation.Id, parentId, null, ct);
        if (conversation.Kind != "channel" || parent is null || parent.Deleted ||
            parent.CreatedAt < member.HistoryFrom || parent.CreatedAt < Now.AddDays(-7))
            throw new AppError(400, "invalid_parent", "The reply's parent is not available.");
    }

    private async Task<OperationView> ApplyResultAsync(Actor actor, string id, string operationId, WriteResult result,
        CancellationToken ct, int? attempts = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(retries.Options.AttemptTimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            for (;;)
            {
                var conversation = await store.GetAsync(actor.TenantId, id, linked.Token)
                    ?? throw new AppError(503, "projection_unavailable", "Conversation state is unavailable.");
                var operation = await store.GetOperationAsync(actor.TenantId, id, operationId, linked.Token)
                    ?? throw new AppError(503, "operation_unavailable", "Operation evidence is unavailable.");
                if (operation.Value.State == OperationState.Confirmed) return View(operation.Value);
                var updated = operation.Value with
                {
                    State = result.State,
                    GraphMessageId = result.Message?.Id,
                    Error = result.Error,
                    Body = "", RetryAt = null, AttemptUntil = null,
                    Attempts = attempts ?? Math.Max(0, operation.Value.Attempts - (result.Dispatched ? 0 : 1)),
                    FirstStatus = operation.Value.FirstStatus ?? result.Status,
                    Throttles = operation.Value.Throttles + (result.Status == 429 ? 1 : 0)
                };
                var projection = result.Message is null || operation.Value.Epoch != conversation.Value.Epoch
                    ? conversation.Value : Projection.Apply(conversation.Value, result.Message, Now);
                if (await store.CommitAsync(conversation with { Value = projection }, operation with { Value = updated }, linked.Token))
                {
                    LiveTelemetry.WriteOutcomes.Add(1, new KeyValuePair<string, object?>("state", result.State.ToString()));
                    return View(updated);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, 11)), clock, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppError(503, "confirmation_persistence_unavailable",
                "Confirmation could not be recorded. Check the original operation; do not resend.");
        }
    }

    private static OperationView View(SendOperation op) => new(op.Id, op.State, op.GraphMessageId, op.Error,
        op.Attempts, op.Throttles, op.FirstStatus, op.RetryAt);
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
