using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Teams.Core;

namespace Teams.Tests;

public sealed class LiveTelemetryTests
{
    [Fact]
    public void MetricLabelsAreBoundedOperationalCategoriesNotActorsOrContent()
    {
        ConcurrentBag<(string Name, string[] Keys)> measurements = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == LiveTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().Select(t => t.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().Select(t => t.Key).ToArray())));
        listener.Start();
        LiveTelemetry.Failure("test");
        using (LiveTelemetry.Measure("test")) { }
        Assert.Contains(measurements, m => m.Name == "teamsref_worker_failures");
        Assert.Contains(measurements, m => m.Name == "teamsref_dependency_duration_ms");
        Assert.All(measurements.SelectMany(m => m.Keys), key => Assert.Contains(key, new[] { "mode", "component", "operation", "state" }));
    }
}
