using System.Net;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class GraphRetryTests
{
    [Fact]
    public async Task TwoUsersCanDispatchSixtyInitialOperationsBeforeAnyResponse()
    {
        var fixture = new LiveFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var graph = new Transport(async (actor, operation, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 60) entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Confirm(actor, operation, fixture.Clock);
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock);
        var commands = Enumerable.Range(0, 60).Select(i =>
        {
            var actor = i % 2 == 0 ? fixture.Alex : fixture.Sam;
            return (actor, command: Command(tokens, actor));
        }).ToArray();
        var running = commands.Select(pair => service.SendAsync(pair.actor, "chat-handover", pair.command, default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(60, calls);
        release.SetResult();
        var outcomes = await Task.WhenAll(running);
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(OperationState.Confirmed, outcome.State);
            Assert.Equal(1, outcome.Attempts);
            Assert.Equal(201, outcome.FirstStatus);
        });
        foreach (var pair in commands)
        {
            Assert.Equal("", (await fixture.Store.GetOperationAsync(fixture.Tenant, "chat-handover", pair.command.OperationId, default))!.Value.Body);
            Assert.Equal(OperationState.Confirmed,
                (await service.SendAsync(pair.actor, "chat-handover", pair.command, default)).State);
        }
        Assert.Equal(60, calls);
    }

    [Fact]
    public async Task RetryAfterIsAwaitedWithoutBlockingIndependentSendsOrReplayingDuplicates()
    {
        var fixture = new LiveFixture();
        var clock = new ManualClock(fixture.Clock.GetUtcNow());
        var calls = 0;
        var graph = new Transport((actor, operation, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(operation.Attempts == 1 && operation.Body == "throttle"
                ? new WriteResult(OperationState.Rejected, null, RetryAfter: TimeSpan.FromSeconds(10), Status: 429)
                : Confirm(actor, operation, fixture.Clock));
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock,
            retry: new(clock: clock));
        var command = Command(tokens, fixture.Alex) with { Body = "throttle" };
        var original = service.SendAsync(fixture.Alex, "chat-handover", command, default);
        Assert.False(original.IsCompleted);
        Assert.Equal(OperationState.Pending, (await service.SendAsync(fixture.Alex, "chat-handover", command, default)).State);
        var independent = await service.SendAsync(fixture.Sam, "chat-handover", Command(tokens, fixture.Sam), default);
        Assert.Equal(OperationState.Confirmed, independent.State);
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(original.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await original.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationState.Confirmed, result.State);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(1, result.Throttles);
        Assert.Equal(429, result.FirstStatus);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(180, 1)]
    public async Task ThrottlingStopsAtTheRetryCountOrTotalDeadline(int seconds, int expectedCalls)
    {
        var fixture = new LiveFixture();
        var calls = 0;
        var graph = new Transport((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new WriteResult(OperationState.Rejected, null,
                RetryAfter: TimeSpan.FromSeconds(seconds), Status: 429));
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock);
        var result = await service.SendAsync(fixture.Alex, "chat-handover", Command(tokens, fixture.Alex), default);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls, result.Attempts);
        Assert.Equal(expectedCalls, result.Throttles);
        Assert.Equal(OperationState.Rejected, result.State);
        Assert.Contains("retry limit", result.Error);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task AmbiguousWritesAreNotRetried(int status)
    {
        var fixture = new LiveFixture();
        var calls = 0;
        var graph = new Transport((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new WriteResult(OperationState.Unknown, null, Status: status));
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock);
        var command = Command(tokens, fixture.Alex);
        Assert.Equal(OperationState.Unknown, (await service.SendAsync(fixture.Alex, "chat-handover", command, default)).State);
        Assert.Equal(OperationState.Unknown, (await service.SendAsync(fixture.Alex, "chat-handover", command, default)).State);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("epoch")]
    [InlineData("cancel")]
    public async Task RetryingCannotBypassRevocationEpochOrCancellation(string change)
    {
        var fixture = new LiveFixture();
        var clock = new ManualClock(fixture.Clock.GetUtcNow());
        var calls = 0;
        var graph = new Transport((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new WriteResult(OperationState.Rejected, null, RetryAfter: TimeSpan.FromSeconds(10), Status: 429));
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock, retry: new(clock: clock));
        using var cancel = new CancellationTokenSource();
        var pending = service.SendAsync(fixture.Alex, "chat-handover", Command(tokens, fixture.Alex), cancel.Token);
        if (change == "revoke") await fixture.Store.SetBlockedAsync(fixture.Tenant, fixture.Alex.Id, true, default);
        if (change == "epoch")
        {
            var conversation = (await fixture.Store.GetAsync(fixture.Tenant, "chat-handover", default))!;
            await fixture.Store.SaveAsync(conversation with { Value = conversation.Value with { Epoch = 2 } }, default);
        }
        if (change == "cancel") cancel.Cancel();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationState.Rejected, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BackgroundGetRetriesTransientFailuresButSubscriptionPostDoesNot()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            var calls = 0;
            var graph = new GraphBackground(new HttpClient(new Handler(_ =>
            {
                var response = new HttpResponseMessage(++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = new StringContent("{}") };
                response.Headers.RetryAfter = new(TimeSpan.Zero);
                return response;
            })), new Credential());
            if (method == HttpMethod.Get) await graph.GetAsync("subscriptions", default);
            else await Assert.ThrowsAsync<AppError>(() => graph.PostAsync("subscriptions", new { }, default));
            Assert.Equal(method == HttpMethod.Get ? 2 : 1, calls);
        }
    }

    [Fact]
    public async Task SubscriptionPostCanRetryAnExplicitThrottle()
    {
        var calls = 0;
        var graph = new GraphBackground(new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(++calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Created)
                { Content = new StringContent("{}") };
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return response;
        })), new Credential());
        await graph.PostAsync("subscriptions", new { }, default);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(119, true)]
    public async Task RetryWaitUsesTheOperationDeadlineNotTheLegacyExpiry(int waitSeconds, bool expire)
    {
        var fixture = new LiveFixture();
        var clock = new ManualClock(fixture.Clock.GetUtcNow());
        var calls = 0;
        var graph = new Transport((actor, operation, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new WriteResult(OperationState.Rejected, null, RetryAfter: TimeSpan.FromSeconds(waitSeconds), Status: 429)
                : Confirm(actor, operation, clock));
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(clock), tokens, clock, retry: new(clock: clock));
        var command = Command(tokens, fixture.Alex);
        var pending = service.SendAsync(fixture.Alex, "chat-handover", command, default);
        clock.Advance(TimeSpan.FromSeconds(36));
        var waiting = await service.StatusAsync(fixture.Alex, "chat-handover", command.OperationId, default);
        Assert.Equal(OperationState.Pending, waiting.State);
        Assert.NotNull(waiting.RetryAt);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(expire ? 85 : 24));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expire ? OperationState.Rejected : OperationState.Confirmed, result.State);
        Assert.Equal(expire ? 1 : 2, calls);
    }

    [Fact]
    public async Task ADeletedReplyParentPreventsTheNextAttempt()
    {
        var fixture = new LiveFixture();
        var clock = new ManualClock(fixture.Clock.GetUtcNow());
        var calls = 0;
        var graph = new Transport((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new WriteResult(OperationState.Rejected, null, RetryAfter: TimeSpan.FromSeconds(10), Status: 429));
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(clock), tokens, clock, retry: new(clock: clock));
        var command = new SendCommand(Guid.NewGuid().ToString(),
            tokens.Issue(fixture.Alex, "channel-general", "command:1", "test", TimeSpan.FromMinutes(5)), "reply", "root");
        var pending = service.SendAsync(fixture.Alex, "channel-general", command, default);
        var root = (await fixture.Store.CurrentMessageAsync(fixture.Tenant, "channel-general", "root", null, default))!;
        await fixture.Store.ApplyAsync(fixture.Tenant, "channel-general", root with
            { Deleted = true, ModifiedAt = clock.GetUtcNow().AddSeconds(1), SourceVersion = "deleted" }, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationState.Rejected, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreparationFailuresDoNotInventGraphAttempts()
    {
        var fixture = new LiveFixture();
        var graph = new Transport((_, _, _) => Task.FromResult(
            new WriteResult(OperationState.Rejected, null, "Token unavailable.", Dispatched: false)));
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), fixture.Clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(fixture.Clock), tokens, fixture.Clock);
        var result = await service.SendAsync(fixture.Alex, "chat-handover", Command(tokens, fixture.Alex), default);
        Assert.Equal(OperationState.Rejected, result.State);
        Assert.Equal(0, result.Attempts);
        Assert.Null(result.FirstStatus);
    }

    [Fact]
    public async Task ALateExplicitThrottleUpdatesUnknownEvidenceWithoutReclaimingDispatch()
    {
        var fixture = new LiveFixture();
        var clock = new ManualClock(fixture.Clock.GetUtcNow());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var graph = new Transport(async (_, _, ct) =>
        {
            calls++;
            await release.Task.WaitAsync(ct);
            return new WriteResult(OperationState.Rejected, null, RetryAfter: TimeSpan.Zero, Status: 429);
        });
        var tokens = new CommandTokens(new EphemeralDataProtectionProvider(), clock);
        var service = new Messaging(fixture.Store, graph, new MemoryBudget(clock), tokens, clock, retry: new(clock: clock));
        var command = Command(tokens, fixture.Alex);
        var pending = service.SendAsync(fixture.Alex, "chat-handover", command, default);
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(OperationState.Unknown,
            (await service.StatusAsync(fixture.Alex, "chat-handover", command.OperationId, default)).State);
        release.SetResult();
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationState.Rejected, outcome.State);
        Assert.Equal(1, outcome.Throttles);
        Assert.Equal(429, outcome.FirstStatus);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AGetBodyTransportFailureCanRetryAfterSuccessfulHeaders()
    {
        var calls = 0;
        var graph = new GraphBackground(new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = ++calls == 1 ? new BrokenContent() : new StringContent("{}") };
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return response;
        })), new Credential());
        await graph.GetAsync("subscriptions", default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RetryAfterDatesAndFallbackBackoffPreserveBackendWaits()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-22T10:00:00Z"));
        var retry = new GraphRetry(clock: clock);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(17));
        Assert.Equal(TimeSpan.FromSeconds(17), GraphRequests.RetryAfter(response, clock));
        Assert.Equal(TimeSpan.FromMinutes(10), retry.Delay(1, TimeSpan.FromMinutes(10)));
        Assert.False(retry.CanRetry(1, TimeSpan.FromMinutes(10), retry.Deadline()));
        Assert.False(retry.CanRetry(1, TimeSpan.MaxValue, retry.Deadline()));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var maximum = 1000 * Math.Pow(2, attempt - 1);
            Assert.InRange(retry.Delay(attempt, null).TotalMilliseconds, maximum / 2, maximum);
        }
        Assert.Equal(TimeSpan.Zero, retry.Delay(1, TimeSpan.FromSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() => new GraphRetry(new() { MaxRetries = -1 }));
    }

    private static SendCommand Command(ICommandTokens tokens, Actor actor) => new(Guid.NewGuid().ToString(),
        tokens.Issue(actor, "chat-handover", "command:1", "test", TimeSpan.FromMinutes(5)), "burst", null);
    private static WriteResult Confirm(Actor actor, SendOperation operation, TimeProvider clock) =>
        new(OperationState.Confirmed, new(operation.Id, actor.Id, actor.Name, operation.Body,
            clock.GetUtcNow(), clock.GetUtcNow()), Status: 201);
    private sealed class Transport(Func<Actor, SendOperation, CancellationToken, Task<WriteResult>> send) : IGraphTransport
    {
        public Task<WriteResult> SendAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            send(actor, operation, ct);
        public Task<WriteResult?> ResolveAsync(Actor actor, Conversation conversation, SendOperation operation, CancellationToken ct) =>
            Task.FromResult<WriteResult?>(null);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(send(request));
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException("Controlled body transport failure."));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            foreach (var timer in timers.ToArray())
                if (timer.Due <= now) timer.Fire();
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
                return true;
            }
            public void Fire() { Due = DateTimeOffset.MaxValue; callback(state); }
            public void Dispose() => Due = DateTimeOffset.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
