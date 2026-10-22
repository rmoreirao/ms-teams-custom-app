using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Teams.Core;

public static class LiveTelemetry
{
    public const string MeterName = "TeamsReference.Live";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    public static readonly Counter<long> GraphAttempts = Meter.CreateCounter<long>("teamsref_graph_attempts");
    public static readonly Counter<long> GraphResponses = Meter.CreateCounter<long>("teamsref_graph_responses");
    public static readonly Counter<long> GraphRetries = Meter.CreateCounter<long>("teamsref_graph_retries");
    public static readonly Counter<long> GraphRetryExhausted = Meter.CreateCounter<long>("teamsref_graph_retry_exhausted");
    public static readonly Histogram<double> GraphRetryWait = Meter.CreateHistogram<double>("teamsref_graph_retry_wait_ms", "ms");
    public static readonly Counter<long> GraphDeferred = Meter.CreateCounter<long>("teamsref_graph_deferred");
    public static readonly Counter<long> AcceptedNotifications = Meter.CreateCounter<long>("teamsref_notification_accepted");
    public static readonly Counter<long> FilteredNotifications = Meter.CreateCounter<long>("teamsref_notification_filtered");
    public static readonly Counter<long> ProjectionChanges = Meter.CreateCounter<long>("teamsref_projection_changes");
    public static readonly Counter<long> DeliveryPages = Meter.CreateCounter<long>("teamsref_delivery_pages");
    public static readonly Counter<long> DeliveryRecipients = Meter.CreateCounter<long>("teamsref_delivery_recipients");
    public static readonly Counter<long> WriteOutcomes = Meter.CreateCounter<long>("teamsref_write_outcomes");
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>("teamsref_worker_failures");
    public static readonly Counter<long> ReadRefreshRequests = Meter.CreateCounter<long>("teamsref_read_refresh_requests");
    public static readonly Counter<long> ReadRefreshCoalesced = Meter.CreateCounter<long>("teamsref_read_refresh_coalesced");
    public static readonly Counter<long> ReadRefreshFailures = Meter.CreateCounter<long>("teamsref_read_refresh_failures");
    public static readonly Counter<long> ReadRefreshProgress = Meter.CreateCounter<long>("teamsref_read_refresh_progress");
    public static readonly Histogram<double> ReadRefreshAge = Meter.CreateHistogram<double>("teamsref_read_refresh_age_ms", "ms");
    public static readonly Histogram<long> RepairStalled = Meter.CreateHistogram<long>("teamsref_repair_stalled");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("teamsref_dependency_duration_ms", "ms");
    public static void Failure(string component) =>
        Failures.Add(1, new KeyValuePair<string, object?>("component", component));
    public static IDisposable Measure(string operation) => new Measurement(operation);
    private sealed class Measurement(string operation) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose() => Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
    }
}
