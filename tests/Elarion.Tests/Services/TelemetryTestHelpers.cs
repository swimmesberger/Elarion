using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Tests.Services;

internal sealed class ActivityCollector : IDisposable {
    private readonly HashSet<string> _sourceNames;
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public ActivityCollector(params string[] sourceNames) {
        _sourceNames = new HashSet<string>(sourceNames, StringComparer.Ordinal);
        _listener = new ActivityListener {
            ShouldListenTo = source => _sourceNames.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Activities => _activities.ToArray();

    public void Dispose() => _listener.Dispose();
}

internal sealed class MeterCollector : IDisposable {
    private readonly HashSet<string> _meterNames;
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly MeterListener _listener = new();

    public MeterCollector(params string[] meterNames) {
        _meterNames = new HashSet<string>(meterNames, StringComparer.Ordinal);
        _listener.InstrumentPublished = (instrument, listener) => {
            if (_meterNames.Contains(instrument.Meter.Name)) {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            _measurements.Enqueue(new Measurement(instrument.Name, measurement, CopyTags(tags))));
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            _measurements.Enqueue(new Measurement(instrument.Name, measurement, CopyTags(tags))));
        _listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
            _measurements.Enqueue(new Measurement(instrument.Name, measurement, CopyTags(tags))));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Measurements => _measurements.ToArray();

    public void Dispose() => _listener.Dispose();

    private static IReadOnlyDictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
        var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags) {
            copied[tag.Key] = tag.Value;
        }

        return copied;
    }

    public sealed record Measurement(string InstrumentName, object Value, IReadOnlyDictionary<string, object?> Tags);
}

internal static class TelemetryAssertions {
    public static object? GetTag(this Activity activity, string key) =>
        activity.TagObjects.FirstOrDefault(tag => tag.Key == key).Value;

    public static bool HasTag(this MeterCollector.Measurement measurement, string key, object? value) =>
        measurement.Tags.TryGetValue(key, out var actual) && Equals(actual, value);
}
