namespace Teams.Core;

public sealed class GraphRetryOptions
{
    public int MaxRetries { get; set; } = 3;
    public int OperationTimeoutSeconds { get; set; } = 120;
    public int AttemptTimeoutSeconds { get; set; } = 20;
    public int BaseDelayMilliseconds { get; set; } = 1000;
    public int MaxDelayMilliseconds { get; set; } = 30000;

    public void Validate()
    {
        if (MaxRetries < 0 || OperationTimeoutSeconds <= 0 || OperationTimeoutSeconds > (uint.MaxValue - 1L) / 1000 ||
            AttemptTimeoutSeconds <= 0 ||
            AttemptTimeoutSeconds > OperationTimeoutSeconds || BaseDelayMilliseconds <= 0 ||
            MaxDelayMilliseconds < BaseDelayMilliseconds)
            throw new InvalidOperationException("Graph retry settings require nonnegative retries and positive, bounded timeouts/backoff.");
    }
}

public sealed class GraphRetry
{
    public GraphRetryOptions Options { get; }
    public TimeProvider Clock { get; }
    public GraphRetry(GraphRetryOptions? options = null, TimeProvider? clock = null)
    {
        Options = options ?? new();
        Options.Validate();
        Clock = clock ?? TimeProvider.System;
    }
    public DateTimeOffset Deadline() => Clock.GetUtcNow().AddSeconds(Options.OperationTimeoutSeconds);
    public TimeSpan Delay(int retry, TimeSpan? retryAfter)
    {
        if (retryAfter is { } supplied) return supplied > TimeSpan.Zero ? supplied : TimeSpan.Zero;
        var maximum = Math.Min(Options.MaxDelayMilliseconds,
            Options.BaseDelayMilliseconds * Math.Pow(2, Math.Min(retry - 1, 30)));
        return TimeSpan.FromMilliseconds(maximum * (0.5 + Random.Shared.NextDouble() * 0.5));
    }
    public bool CanRetry(int attempts, TimeSpan delay, DateTimeOffset deadline) =>
        attempts <= Options.MaxRetries && delay < deadline - Clock.GetUtcNow();
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, Clock, ct);
}
