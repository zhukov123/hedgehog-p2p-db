using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Hedgehog.LocalRuntime;

internal sealed class LocalRuntimeDemoTraffic : BackgroundService
{
    private readonly LocalCluster runtime;
    private readonly LocalRuntimeMetrics metrics;
    private readonly TimeSpan interval;
    private readonly bool enabled;
    private readonly ConcurrentQueue<DemoTrafficActivityDto> recentActivity = new();
    private long successCount;
    private long failureCount;
    private long runSequence;
    private long running;

    public LocalRuntimeDemoTraffic(
        LocalCluster runtime,
        LocalRuntimeMetrics metrics,
        IConfiguration configuration)
    {
        this.runtime = runtime;
        this.metrics = metrics;
        enabled = !string.Equals(
            configuration["demo-traffic-enabled"] ?? Environment.GetEnvironmentVariable("HEDGEHOG_DEMO_TRAFFIC_ENABLED"),
            "false",
            StringComparison.OrdinalIgnoreCase);

        var configuredSeconds = configuration.GetValue<int?>("demo-traffic-interval-seconds")
            ?? TryReadIntervalSecondsFromEnvironment()
            ?? 30;
        interval = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 5, 3600));
    }

    public DemoTrafficSnapshotDto Snapshot() =>
        new(
            enabled,
            interval.TotalSeconds,
            Interlocked.Read(ref successCount),
            Interlocked.Read(ref failureCount),
            recentActivity.ToArray());

    public async Task<DemoTrafficActivityDto> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) == 1)
        {
            var skipped = new DemoTrafficActivityDto(
                DateTimeOffset.UtcNow,
                "skipped",
                "already_running",
                null,
                null,
                "previous generated-traffic run is still active");
            AddActivity(skipped);
            return skipped;
        }

        var sequence = Interlocked.Increment(ref runSequence);
        var objectName = $"demo/generated-{sequence:000000}.txt";
        var payload = $"hedgehog demo traffic {sequence} at {DateTimeOffset.UtcNow:O}";
        var start = Stopwatch.GetTimestamp();

        try
        {
            var writer = runtime.CreateClient("demo-writer", preferLastHead: sequence % 2 == 0);
            var put = await writer.PutTextAsync(objectName, payload, cancellationToken).ConfigureAwait(false);
            metrics.RecordOperation("demo_put", "ok", Stopwatch.GetElapsedTime(start), Encoding.UTF8.GetByteCount(payload));

            var readerStart = Stopwatch.GetTimestamp();
            var reader = runtime.CreateClient("demo-reader", preferLastHead: sequence % 2 != 0);
            var readBack = await reader.GetTextAsync(objectName, cancellationToken).ConfigureAwait(false);
            metrics.RecordOperation("demo_get", "ok", Stopwatch.GetElapsedTime(readerStart), Encoding.UTF8.GetByteCount(readBack));
            if (!string.Equals(payload, readBack, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated traffic read returned unexpected payload.");
            }

            string action = "write_read";
            if (sequence % 3 == 0)
            {
                var deleteStart = Stopwatch.GetTimestamp();
                await reader.DeleteAsync(objectName, cancellationToken).ConfigureAwait(false);
                metrics.RecordOperation("demo_delete", "ok", Stopwatch.GetElapsedTime(deleteStart));
                action = "write_read_delete";
            }

            Interlocked.Increment(ref successCount);
            var activity = new DemoTrafficActivityDto(
                DateTimeOffset.UtcNow,
                action,
                "ok",
                put.HeadId,
                objectName,
                null);
            AddActivity(activity);
            return activity;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref failureCount);
            metrics.RecordOperation("demo_traffic", "error", Stopwatch.GetElapsedTime(start));
            var activity = new DemoTrafficActivityDto(
                DateTimeOffset.UtcNow,
                "write_read",
                "error",
                null,
                objectName,
                ex.Message);
            AddActivity(activity);
            return activity;
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void AddActivity(DemoTrafficActivityDto activity)
    {
        recentActivity.Enqueue(activity);
        while (recentActivity.Count > 20 && recentActivity.TryDequeue(out _))
        {
        }
    }

    private static int? TryReadIntervalSecondsFromEnvironment() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("HEDGEHOG_DEMO_TRAFFIC_INTERVAL_SECONDS"),
            out var seconds)
            ? seconds
            : null;
}

public sealed record DemoTrafficSnapshotDto(
    bool Enabled,
    double IntervalSeconds,
    long SuccessCount,
    long FailureCount,
    IReadOnlyList<DemoTrafficActivityDto> RecentActivity);

public sealed record DemoTrafficActivityDto(
    DateTimeOffset At,
    string Action,
    string Result,
    string? HeadId,
    string? ObjectName,
    string? Error);
