using System.Collections.Concurrent;
using System.Diagnostics;
using Hedgehog.LocalRuntime;

internal sealed class DemoTrafficRunner(
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    IConfiguration configuration,
    ILogger<DemoTrafficRunner> logger) : BackgroundService
{
    private const int MaxRecentActivity = 20;
    private const int MaxRecentFailures = 10;

    private readonly ConcurrentQueue<DemoTrafficEventDto> recentActivity = new();
    private readonly ConcurrentQueue<DemoTrafficFailureDto> recentFailures = new();
    private long ticksStarted;
    private long ticksSucceeded;
    private long ticksFailed;
    private long writesSucceeded;
    private long readsSucceeded;
    private long deletesSucceeded;
    private long failureSequence;
    private DateTimeOffset? lastTickAt;
    private DateTimeOffset? lastSuccessAt;
    private DateTimeOffset? lastFailureAt;

    public bool Enabled { get; } = ReadBoolean(configuration, "demo-traffic-enabled", "HEDGEHOG_DEMO_TRAFFIC_ENABLED", defaultValue: false);

    public TimeSpan Interval { get; } = TimeSpan.FromSeconds(Math.Max(
        5,
        ReadInt(configuration, "demo-traffic-interval-seconds", "HEDGEHOG_DEMO_TRAFFIC_INTERVAL_SECONDS", defaultValue: 30)));

    public async Task<DemoTrafficSnapshotDto> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await runtime.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var metadata = new DemoTrafficMetadataDto(
            await runtime.ScalarLongAsync("SELECT COUNT(*) FROM objects;", cancellationToken).ConfigureAwait(false),
            await runtime.ScalarLongAsync("SELECT COUNT(*) FROM object_versions;", cancellationToken).ConfigureAwait(false),
            await runtime.ScalarLongAsync("SELECT COUNT(*) FROM replicas WHERE state = 'healthy';", cancellationToken).ConfigureAwait(false),
            await runtime.ScalarLongAsync("SELECT COUNT(*) FROM object_versions WHERE state = 'delete_marker';", cancellationToken).ConfigureAwait(false),
            await runtime.ScalarLongAsync("SELECT COUNT(*) FROM repair_jobs;", cancellationToken).ConfigureAwait(false));

        return new DemoTrafficSnapshotDto(
            Enabled,
            Interval,
            DateTimeOffset.UtcNow,
            lastTickAt,
            lastSuccessAt,
            lastFailureAt,
            Interlocked.Read(ref ticksStarted),
            Interlocked.Read(ref ticksSucceeded),
            Interlocked.Read(ref ticksFailed),
            Interlocked.Read(ref writesSucceeded),
            Interlocked.Read(ref readsSucceeded),
            Interlocked.Read(ref deletesSucceeded),
            snapshot.Tenants.Count,
            snapshot.Heads.Count,
            snapshot.Heads.Count(head => head.IsRunning),
            snapshot.StorageNodes.Count,
            snapshot.StorageNodes.Count(node => node.IsRunning),
            snapshot.StorageNodes.Sum(node => node.CapacityBytes),
            snapshot.StorageNodes.Sum(node => node.UsedBytes),
            snapshot.StorageNodes.Sum(node => node.FreeBytes),
            snapshot.StorageNodes.Sum(node => node.Replicas.Count),
            metadata,
            recentActivity.ToArray(),
            recentFailures.ToArray());
    }

    public async Task<DemoTrafficEventDto> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var tick = Interlocked.Increment(ref ticksStarted);
        var startedAt = DateTimeOffset.UtcNow;
        lastTickAt = startedAt;
        var stopwatch = Stopwatch.StartNew();
        var objectName = $"demo-traffic-{startedAt:yyyyMMddHHmmss}-{tick}.txt";
        var clientId = $"demo-client-{tick % 2}";
        var tenantId = "tenant-local";
        var datasetId = "dataset-local";
        var payload = $"hedgehog demo traffic tick {tick} at {startedAt:O}";

        try
        {
            var writer = runtime.CreateClientForTenant(tenantId, datasetId, clientId, preferLastHead: tick % 2 == 0);
            var put = await writer.PutTextAsync(objectName, payload, cancellationToken).ConfigureAwait(false);
            metrics.RecordOperation("demo_put", "ok", stopwatch.Elapsed, payload.Length);
            Interlocked.Increment(ref writesSucceeded);

            var reader = runtime.CreateClientForTenant(tenantId, datasetId, $"{clientId}-reader", preferLastHead: tick % 2 != 0);
            var read = await reader.GetTextAsync(objectName, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(payload, read, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Demo traffic read returned unexpected payload.");
            }

            metrics.RecordOperation("demo_get", "ok", stopwatch.Elapsed, payload.Length);
            Interlocked.Increment(ref readsSucceeded);

            await reader.DeleteAsync(objectName, cancellationToken).ConfigureAwait(false);
            metrics.RecordOperation("demo_delete", "ok", stopwatch.Elapsed);
            Interlocked.Increment(ref deletesSucceeded);

            Interlocked.Increment(ref ticksSucceeded);
            lastSuccessAt = DateTimeOffset.UtcNow;
            var item = new DemoTrafficEventDto(
                tick,
                lastSuccessAt.Value,
                "ok",
                tenantId,
                datasetId,
                objectName,
                put.HeadId,
                put.ReplicaCount,
                stopwatch.Elapsed);
            EnqueueBounded(recentActivity, item, MaxRecentActivity);
            return item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref ticksFailed);
            lastFailureAt = DateTimeOffset.UtcNow;
            metrics.RecordOperation("demo_tick", "error", stopwatch.Elapsed);
            var failure = new DemoTrafficFailureDto(
                Interlocked.Increment(ref failureSequence),
                lastFailureAt.Value,
                objectName,
                ex.GetType().Name,
                ex.Message);
            EnqueueBounded(recentFailures, failure, MaxRecentFailures);
            logger.LogWarning(ex, "Demo traffic tick failed for {ObjectName}", objectName);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        await RunLoopTickAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunLoopTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunLoopTickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Failure details are retained in recentFailures and exposed through the demo status endpoint.
        }
    }

    private static void EnqueueBounded<T>(ConcurrentQueue<T> queue, T item, int maxItems)
    {
        queue.Enqueue(item);
        while (queue.Count > maxItems && queue.TryDequeue(out _))
        {
        }
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        string environmentVariable,
        bool defaultValue)
    {
        var value = configuration[key] ?? Environment.GetEnvironmentVariable(environmentVariable);
        return value is null
            ? defaultValue
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        string environmentVariable,
        int defaultValue)
    {
        var value = configuration[key] ?? Environment.GetEnvironmentVariable(environmentVariable);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}

public sealed record DemoTrafficSnapshotDto(
    bool Enabled,
    TimeSpan Interval,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    long TicksStarted,
    long TicksSucceeded,
    long TicksFailed,
    long WritesSucceeded,
    long ReadsSucceeded,
    long DeletesSucceeded,
    int TenantCount,
    int HeadCount,
    int RunningHeads,
    int StorageNodeCount,
    int RunningStorageNodes,
    long CapacityBytes,
    long UsedBytes,
    long FreeBytes,
    int ReplicaFiles,
    DemoTrafficMetadataDto Metadata,
    IReadOnlyList<DemoTrafficEventDto> RecentActivity,
    IReadOnlyList<DemoTrafficFailureDto> RecentFailures);

public sealed record DemoTrafficMetadataDto(
    long Objects,
    long ObjectVersions,
    long HealthyReplicas,
    long DeleteMarkers,
    long RepairJobs);

public sealed record DemoTrafficEventDto(
    long Tick,
    DateTimeOffset CompletedAt,
    string Result,
    string TenantId,
    string DatasetId,
    string ObjectName,
    string HeadId,
    int ReplicaCount,
    TimeSpan Elapsed);

public sealed record DemoTrafficFailureDto(
    long Sequence,
    DateTimeOffset FailedAt,
    string ObjectName,
    string ErrorType,
    string Message);
