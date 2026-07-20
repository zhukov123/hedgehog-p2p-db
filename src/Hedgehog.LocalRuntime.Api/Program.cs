using System.Diagnostics;
using System.Text;
using Hedgehog.Client;
using Hedgehog.Head;
using Hedgehog.LocalRuntime;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var runtimeRoot = builder.Configuration["runtime-root"]
    ?? Environment.GetEnvironmentVariable("HEDGEHOG_RUNTIME_ROOT")
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "local-runtime-api");
runtimeRoot = Path.GetFullPath(runtimeRoot);
var resetRuntime = string.Equals(
    builder.Configuration["reset-runtime"] ?? Environment.GetEnvironmentVariable("HEDGEHOG_RUNTIME_RESET"),
    "true",
    StringComparison.OrdinalIgnoreCase);

if (resetRuntime && Directory.Exists(runtimeRoot))
{
    Directory.Delete(runtimeRoot, recursive: true);
}

var cluster = new LocalCluster(CreateClusterOptions(builder.Configuration, runtimeRoot));
await cluster.StartAsync();

builder.Services.AddSingleton<LocalRuntimeMetrics>();
builder.Services.AddSingleton(cluster);
builder.Services.AddSingleton<RuntimeTrafficState>();
if (GetBool(builder.Configuration, "traffic-enabled", "HEDGEHOG_TRAFFIC_ENABLED", defaultValue: false))
{
    builder.Services.AddHostedService<RuntimeTrafficWorker>();
}

var app = builder.Build();
app.UseCors();

app.Lifetime.ApplicationStopping.Register(() =>
{
    cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/", () => Results.Redirect("/runtime/status"));

app.MapGet("/demo", () => Results.Content(RuntimeDemoPage.Html, "text/html; charset=utf-8"));

app.MapGet("/health/live", () => Results.Ok(new HealthLiveDto(
    "Hedgehog.LocalRuntime.Api",
    "live",
    DateTimeOffset.UtcNow)));

app.MapGet("/health/ready", async (LocalCluster runtime, CancellationToken cancellationToken) =>
{
    var health = await LoadClusterHealthAsync(runtime, cancellationToken);
    return health.Ready ? Results.Ok(health) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/health/cluster", async (LocalCluster runtime, CancellationToken cancellationToken) =>
    Results.Ok(await LoadClusterHealthAsync(runtime, cancellationToken)));

app.MapGet("/runtime/status", async (LocalCluster runtime, CancellationToken cancellationToken) =>
{
    var snapshot = await runtime.SnapshotAsync(cancellationToken);
    var metadataCounts = await LoadMetadataCountsAsync(runtime, cancellationToken);
    return Results.Ok(new RuntimeStatusDto(
        snapshot.RuntimeRoot,
        snapshot.MetadataPath,
        metadataCounts,
        snapshot.Tenants,
        snapshot.Heads,
        snapshot.StorageNodes.Select(node => new StorageNodeStatusDto(
            node.NodeId,
            node.IsRunning,
            node.CapacityBytes,
            node.UsedBytes,
            node.FreeBytes,
            node.Replicas.Count)).ToArray()));
});

app.MapGet("/runtime/traffic/status", (RuntimeTrafficState traffic) =>
    Results.Ok(traffic.Snapshot()));

app.MapPost("/runtime/traffic/tick", async (
    RuntimeTrafficState traffic,
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    await RuntimeTrafficWorker.RunOneTickAsync(runtime, metrics, traffic, cancellationToken);
    return Results.Ok(traffic.Snapshot());
});

app.MapGet("/metrics", async (
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var snapshot = await runtime.SnapshotAsync(cancellationToken);
    var metadataCounts = await LoadMetadataCountsAsync(runtime, cancellationToken);
    return Results.Text(
        metrics.RenderPrometheus(snapshot, metadataCounts),
        "text/plain; version=0.0.4; charset=utf-8");
});

app.MapPost("/runtime/tenants", async (
    CreateTenantRequest request,
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var start = Stopwatch.GetTimestamp();
    if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.DatasetId))
    {
        metrics.RecordOperation("create_tenant", "bad_request", Stopwatch.GetElapsedTime(start));
        return Results.BadRequest(new ErrorDto("tenantId and datasetId are required"));
    }

    try
    {
        var tenant = await runtime.AddTenantAsync(request.TenantId, request.DatasetId, cancellationToken);
        metrics.RecordOperation("create_tenant", "ok", Stopwatch.GetElapsedTime(start));
        return Results.Ok(new TenantCreatedDto(
            tenant.TenantId,
            tenant.DatasetId,
            tenant.RequiredReplicaCount));
    }
    catch (InvalidOperationException ex)
    {
        metrics.RecordOperation("create_tenant", "error", Stopwatch.GetElapsedTime(start));
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/runtime/tenants/{tenantId}/datasets/{datasetId}/objects", async (
    string tenantId,
    string datasetId,
    PutObjectRequest request,
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var start = Stopwatch.GetTimestamp();
    if (string.IsNullOrWhiteSpace(request.ClientId)
        || string.IsNullOrWhiteSpace(request.Name)
        || request.Text is null)
    {
        metrics.RecordOperation("put", "bad_request", Stopwatch.GetElapsedTime(start));
        return Results.BadRequest(new ErrorDto("clientId, name, and text are required"));
    }

    try
    {
        var client = runtime.CreateClientForTenant(tenantId, datasetId, request.ClientId, request.PreferLastHead);
        var result = await client.PutTextAsync(request.Name, request.Text, cancellationToken);
        metrics.RecordOperation("put", "ok", Stopwatch.GetElapsedTime(start), System.Text.Encoding.UTF8.GetByteCount(request.Text));
        return Results.Ok(new PutObjectResponse(
            result.ClientId,
            result.HeadId,
            result.ObjectId,
            result.VersionId,
            result.ReplicaCount));
    }
    catch (InvalidOperationException ex)
    {
        metrics.RecordOperation("put", "not_found", Stopwatch.GetElapsedTime(start));
        return Results.NotFound(new ErrorDto(ex.Message));
    }
});

app.MapGet("/runtime/tenants/{tenantId}/datasets/{datasetId}/objects", async (
    string tenantId,
    string datasetId,
    string name,
    string clientId,
    bool? preferLastHead,
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var start = Stopwatch.GetTimestamp();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(clientId))
    {
        metrics.RecordOperation("get", "bad_request", Stopwatch.GetElapsedTime(start));
        return Results.BadRequest(new ErrorDto("name and clientId are required"));
    }

    try
    {
        var client = runtime.CreateClientForTenant(tenantId, datasetId, clientId, preferLastHead == true);
        var result = await client.GetAsync(name, cancellationToken);
        metrics.RecordOperation("get", "ok", Stopwatch.GetElapsedTime(start), result.Plaintext.LongLength);
        return Results.Ok(new GetObjectResponse(clientId, name, result));
    }
    catch (InvalidOperationException ex)
    {
        metrics.RecordOperation("get", "not_found", Stopwatch.GetElapsedTime(start));
        return Results.NotFound(new ErrorDto(ex.Message));
    }
});

app.MapDelete("/runtime/tenants/{tenantId}/datasets/{datasetId}/objects", async (
    string tenantId,
    string datasetId,
    string name,
    string clientId,
    bool? preferLastHead,
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var start = Stopwatch.GetTimestamp();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(clientId))
    {
        metrics.RecordOperation("delete", "bad_request", Stopwatch.GetElapsedTime(start));
        return Results.BadRequest(new ErrorDto("name and clientId are required"));
    }

    try
    {
        var client = runtime.CreateClientForTenant(tenantId, datasetId, clientId, preferLastHead == true);
        await client.DeleteAsync(name, cancellationToken);
        metrics.RecordOperation("delete", "ok", Stopwatch.GetElapsedTime(start));
        return Results.Ok(new DeleteObjectResponse(clientId, tenantId, datasetId, name, Deleted: true));
    }
    catch (InvalidOperationException ex)
    {
        metrics.RecordOperation("delete", "not_found", Stopwatch.GetElapsedTime(start));
        return Results.NotFound(new ErrorDto(ex.Message));
    }
});

app.Run();

static LocalClusterOptions CreateClusterOptions(IConfiguration configuration, string runtimeRoot)
{
    var storageNodeCount = GetInt(configuration, "storage-node-count", "HEDGEHOG_STORAGE_NODE_COUNT", 3);
    var requiredReplicaCount = GetInt(configuration, "required-replica-count", "HEDGEHOG_REQUIRED_REPLICA_COUNT", 3);
    var capacityMiB = GetInt(configuration, "storage-node-capacity-mib", "HEDGEHOG_STORAGE_NODE_CAPACITY_MIB", 64);
    var defaults = LocalClusterOptions.CreateDefault(runtimeRoot);

    return defaults with
    {
        HeadCount = GetInt(configuration, "head-count", "HEDGEHOG_HEAD_COUNT", 2),
        StorageNodeCount = storageNodeCount,
        RequiredReplicaCount = requiredReplicaCount,
        StorageNodeCapacityBytes = capacityMiB * 1024L * 1024L,
        TenantId = configuration["tenant-id"] ?? Environment.GetEnvironmentVariable("HEDGEHOG_TENANT_ID") ?? defaults.TenantId,
        DatasetId = configuration["dataset-id"] ?? Environment.GetEnvironmentVariable("HEDGEHOG_DATASET_ID") ?? defaults.DatasetId,
    };
}

static int GetInt(IConfiguration configuration, string key, string environmentKey, int defaultValue)
{
    var value = configuration[key] ?? Environment.GetEnvironmentVariable(environmentKey);
    return int.TryParse(value, out var parsed) ? parsed : defaultValue;
}

static bool GetBool(IConfiguration configuration, string key, string environmentKey, bool defaultValue)
{
    var value = configuration[key] ?? Environment.GetEnvironmentVariable(environmentKey);
    return value is null
        ? defaultValue
        : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}

static async Task<IReadOnlyDictionary<string, long>> LoadMetadataCountsAsync(
    LocalCluster runtime,
    CancellationToken cancellationToken)
{
    var counts = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["tenants"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM tenants;", cancellationToken),
        ["datasets"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM datasets;", cancellationToken),
        ["objects"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM objects;", cancellationToken),
        ["object_versions"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM object_versions;", cancellationToken),
        ["healthy_replicas"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM replicas WHERE state = 'healthy';", cancellationToken),
        ["delete_markers"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM object_versions WHERE state = 'delete_marker';", cancellationToken),
        ["audit_events"] = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM audit_events;", cancellationToken),
    };

    return counts;
}

static async Task<HealthClusterDto> LoadClusterHealthAsync(
    LocalCluster runtime,
    CancellationToken cancellationToken)
{
    var snapshot = await runtime.SnapshotAsync(cancellationToken);
    var metadataAvailable = await runtime.ScalarLongAsync(
        "SELECT COUNT(*) FROM tenants;",
        cancellationToken) >= 0;
    var runningHeads = snapshot.Heads.Count(head => head.IsRunning);
    var runningStorageNodes = snapshot.StorageNodes.Count(node => node.IsRunning);
    var ready = metadataAvailable
        && snapshot.Tenants.Count > 0
        && runningHeads == snapshot.Heads.Count
        && runningStorageNodes == snapshot.StorageNodes.Count
        && snapshot.StorageNodes.All(node => node.FreeBytes >= 0);

    return new HealthClusterDto(
        ready ? "ready" : "not_ready",
        ready,
        DateTimeOffset.UtcNow,
        snapshot.RuntimeRoot,
        snapshot.MetadataPath,
        metadataAvailable,
        snapshot.Tenants.Count,
        runningHeads,
        snapshot.Heads.Count,
        runningStorageNodes,
        snapshot.StorageNodes.Count);
}

public sealed record CreateTenantRequest(string TenantId, string DatasetId);

public sealed record PutObjectRequest(string ClientId, string Name, string Text, bool PreferLastHead = false);

public sealed record ErrorDto(string Error);

public sealed record HealthLiveDto(
    string Service,
    string Status,
    DateTimeOffset CheckedAt);

public sealed record HealthClusterDto(
    string Status,
    bool Ready,
    DateTimeOffset CheckedAt,
    string RuntimeRoot,
    string MetadataPath,
    bool MetadataAvailable,
    int TenantCount,
    int RunningHeads,
    int TotalHeads,
    int RunningStorageNodes,
    int TotalStorageNodes);

public sealed record TenantCreatedDto(string TenantId, string DatasetId, int RequiredReplicaCount);

public sealed record RuntimeStatusDto(
    string RuntimeRoot,
    string MetadataPath,
    IReadOnlyDictionary<string, long> MetadataCounts,
    IReadOnlyList<LocalTenantSnapshot> Tenants,
    IReadOnlyList<HeadNodeSnapshot> Heads,
    IReadOnlyList<StorageNodeStatusDto> StorageNodes);

public sealed record StorageNodeStatusDto(
    string NodeId,
    bool IsRunning,
    long CapacityBytes,
    long UsedBytes,
    long FreeBytes,
    int ReplicaCount);

public sealed record PutObjectResponse(
    string ClientId,
    string HeadId,
    string ObjectId,
    string VersionId,
    int ReplicaCount);

public sealed record GetObjectResponse(
    string ClientId,
    string Name,
    string HeadId,
    string ObjectId,
    string VersionId,
    string Text)
{
    public GetObjectResponse(string clientId, string name, GetObjectResult result)
        : this(clientId, name, result.HeadId, result.ObjectId, result.VersionId, System.Text.Encoding.UTF8.GetString(result.Plaintext))
    {
    }
}

public sealed record DeleteObjectResponse(
    string ClientId,
    string TenantId,
    string DatasetId,
    string Name,
    bool Deleted);

public sealed record RuntimeTrafficSnapshot(
    bool Enabled,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastTickAt,
    long Ticks,
    long Writes,
    long Reads,
    long Updates,
    long Deletes,
    long Failures,
    IReadOnlyList<string> LiveObjectNames,
    IReadOnlyList<string> RecentFailures);

public sealed class RuntimeTrafficState
{
    private readonly Lock gate = new();
    private readonly Queue<string> recentFailures = new();
    private readonly List<string> liveObjectNames = [];
    private long ticks;
    private long writes;
    private long reads;
    private long updates;
    private long deletes;
    private long failures;
    private DateTimeOffset? lastTickAt;

    public RuntimeTrafficState()
    {
        StartedAt = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset StartedAt { get; }

    public RuntimeTrafficSnapshot Snapshot()
    {
        lock (gate)
        {
            return new RuntimeTrafficSnapshot(
                Enabled: true,
                StartedAt,
                lastTickAt,
                ticks,
                writes,
                reads,
                updates,
                deletes,
                failures,
                liveObjectNames.ToArray(),
                recentFailures.ToArray());
        }
    }

    public string? PickLiveName(Random random)
    {
        lock (gate)
        {
            return liveObjectNames.Count == 0
                ? null
                : liveObjectNames[random.Next(liveObjectNames.Count)];
        }
    }

    public void RecordTick()
    {
        lock (gate)
        {
            ticks++;
            lastTickAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordWrite(string name)
    {
        lock (gate)
        {
            writes++;
            if (!liveObjectNames.Contains(name, StringComparer.Ordinal))
            {
                liveObjectNames.Add(name);
            }
        }
    }

    public void RecordRead()
    {
        lock (gate)
        {
            reads++;
        }
    }

    public void RecordUpdate()
    {
        lock (gate)
        {
            updates++;
        }
    }

    public void RecordDelete(string name)
    {
        lock (gate)
        {
            deletes++;
            liveObjectNames.Remove(name);
        }
    }

    public void RecordFailure(Exception exception)
    {
        lock (gate)
        {
            failures++;
            recentFailures.Enqueue($"{DateTimeOffset.UtcNow:o} {exception.GetType().Name}: {exception.Message}");
            while (recentFailures.Count > 20)
            {
                recentFailures.Dequeue();
            }
        }
    }
}

internal sealed class RuntimeTrafficWorker : BackgroundService
{
    private const string TenantId = "tenant-live";
    private const string DatasetId = "dataset-live";
    private readonly LocalCluster runtime;
    private readonly LocalRuntimeMetrics metrics;
    private readonly RuntimeTrafficState state;
    private readonly ILogger<RuntimeTrafficWorker> logger;
    private readonly TimeSpan interval;

    public RuntimeTrafficWorker(
        LocalCluster runtime,
        LocalRuntimeMetrics metrics,
        RuntimeTrafficState state,
        IConfiguration configuration,
        ILogger<RuntimeTrafficWorker> logger)
    {
        this.runtime = runtime;
        this.metrics = metrics;
        this.state = state;
        this.logger = logger;
        interval = TimeSpan.FromSeconds(Math.Max(1, GetTrafficIntervalSeconds(configuration)));
    }

    public static async Task RunOneTickAsync(
        LocalCluster runtime,
        LocalRuntimeMetrics metrics,
        RuntimeTrafficState state,
        CancellationToken cancellationToken)
    {
        var random = Random.Shared;
        state.RecordTick();
        await runtime.AddTenantAsync(TenantId, DatasetId, cancellationToken).ConfigureAwait(false);

        var operation = random.Next(100);
        if (operation < 45)
        {
            await WriteAsync(runtime, metrics, state, random, updateExisting: false, cancellationToken).ConfigureAwait(false);
        }
        else if (operation < 70)
        {
            await ReadAsync(runtime, metrics, state, random, cancellationToken).ConfigureAwait(false);
        }
        else if (operation < 90)
        {
            await WriteAsync(runtime, metrics, state, random, updateExisting: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DeleteAsync(runtime, metrics, state, random, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Hedgehog runtime traffic worker started with interval {Interval}.", interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneTickAsync(runtime, metrics, state, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.RecordFailure(ex);
                logger.LogWarning(ex, "Hedgehog runtime traffic tick failed.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(
        LocalCluster runtime,
        LocalRuntimeMetrics metrics,
        RuntimeTrafficState state,
        Random random,
        bool updateExisting,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var name = updateExisting ? state.PickLiveName(random) : null;
        name ??= $"live-object-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{random.Next(1000, 9999)}.txt";
        var client = runtime.CreateClientForTenant(TenantId, DatasetId, $"traffic-writer-{random.Next(1, 5)}", preferLastHead: random.Next(2) == 0);
        var payload = $"hedgehog traffic {DateTimeOffset.UtcNow:o} seq={Guid.NewGuid():N}";
        await client.PutTextAsync(name, payload, cancellationToken).ConfigureAwait(false);
        metrics.RecordOperation(updateExisting ? "traffic_update" : "traffic_write", "ok", Stopwatch.GetElapsedTime(start), Encoding.UTF8.GetByteCount(payload));
        if (updateExisting)
        {
            state.RecordUpdate();
        }
        else
        {
            state.RecordWrite(name);
        }
    }

    private static async Task ReadAsync(
        LocalCluster runtime,
        LocalRuntimeMetrics metrics,
        RuntimeTrafficState state,
        Random random,
        CancellationToken cancellationToken)
    {
        var name = state.PickLiveName(random);
        if (name is null)
        {
            await WriteAsync(runtime, metrics, state, random, updateExisting: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var client = runtime.CreateClientForTenant(TenantId, DatasetId, $"traffic-reader-{random.Next(1, 5)}", preferLastHead: random.Next(2) == 0);
        var text = await client.GetTextAsync(name, cancellationToken).ConfigureAwait(false);
        metrics.RecordOperation("traffic_read", "ok", Stopwatch.GetElapsedTime(start), Encoding.UTF8.GetByteCount(text));
        state.RecordRead();
    }

    private static async Task DeleteAsync(
        LocalCluster runtime,
        LocalRuntimeMetrics metrics,
        RuntimeTrafficState state,
        Random random,
        CancellationToken cancellationToken)
    {
        var name = state.PickLiveName(random);
        if (name is null)
        {
            await WriteAsync(runtime, metrics, state, random, updateExisting: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var client = runtime.CreateClientForTenant(TenantId, DatasetId, $"traffic-deleter-{random.Next(1, 5)}", preferLastHead: random.Next(2) == 0);
        await client.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        metrics.RecordOperation("traffic_delete", "ok", Stopwatch.GetElapsedTime(start));
        state.RecordDelete(name);
    }

    private static int GetTrafficIntervalSeconds(IConfiguration configuration)
    {
        var value = configuration["traffic-interval-seconds"] ?? Environment.GetEnvironmentVariable("HEDGEHOG_TRAFFIC_INTERVAL_SECONDS");
        return int.TryParse(value, out var parsed) ? parsed : 10;
    }
}

public static class RuntimeDemoPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Hedgehog Live Runtime</title>
  <style>
    body { margin: 0; font: 14px/1.45 system-ui, sans-serif; color: #17202a; background: #f6f7f9; }
    header { padding: 18px 24px; background: #101820; color: white; }
    main { padding: 20px 24px; display: grid; gap: 16px; }
    section { background: white; border: 1px solid #d9dee5; border-radius: 6px; padding: 16px; }
    h1, h2 { margin: 0 0 10px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 10px; }
    .metric { border: 1px solid #e0e4ea; padding: 10px; border-radius: 4px; }
    .metric b { display: block; font-size: 22px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { border-bottom: 1px solid #e6e9ee; padding: 8px; text-align: left; }
    code { background: #eef1f5; padding: 2px 4px; border-radius: 3px; }
  </style>
</head>
<body>
  <header>
    <h1>Hedgehog Live Runtime</h1>
    <div id="line">Loading</div>
  </header>
  <main>
    <section>
      <h2>Traffic</h2>
      <div id="traffic" class="grid"></div>
    </section>
    <section>
      <h2>Cluster</h2>
      <div id="cluster" class="grid"></div>
    </section>
    <section>
      <h2>Storage Nodes</h2>
      <table><thead><tr><th>Node</th><th>Running</th><th>Used</th><th>Free</th><th>Replicas</th></tr></thead><tbody id="nodes"></tbody></table>
    </section>
  </main>
  <script>
    const fmt = new Intl.NumberFormat();
    const mib = value => `${Math.round(value / 1024 / 1024)} MiB`;
    async function json(path) {
      const res = await fetch(path);
      if (!res.ok) throw new Error(`${path}: ${res.status}`);
      return await res.json();
    }
    function metric(label, value) {
      return `<div class="metric"><span>${label}</span><b>${value}</b></div>`;
    }
    async function load() {
      const [status, traffic, health] = await Promise.all([
        json('/runtime/status'),
        json('/runtime/traffic/status'),
        json('/health/cluster')
      ]);
      document.querySelector('#line').textContent = `${health.status} | heads ${health.runningHeads}/${health.totalHeads} | storage ${health.runningStorageNodes}/${health.totalStorageNodes}`;
      document.querySelector('#traffic').innerHTML = [
        metric('ticks', fmt.format(traffic.ticks)),
        metric('writes', fmt.format(traffic.writes)),
        metric('reads', fmt.format(traffic.reads)),
        metric('updates', fmt.format(traffic.updates)),
        metric('deletes', fmt.format(traffic.deletes)),
        metric('live objects', fmt.format(traffic.liveObjectNames.length)),
        metric('failures', fmt.format(traffic.failures))
      ].join('');
      document.querySelector('#cluster').innerHTML = [
        metric('tenants', fmt.format(status.tenants.length)),
        metric('heads', fmt.format(status.heads.length)),
        metric('storage nodes', fmt.format(status.storageNodes.length)),
        metric('objects', fmt.format(status.metadataCounts.objects || 0)),
        metric('versions', fmt.format(status.metadataCounts.object_versions || 0)),
        metric('healthy replicas', fmt.format(status.metadataCounts.healthy_replicas || 0))
      ].join('');
      document.querySelector('#nodes').innerHTML = status.storageNodes.map(node => `
        <tr><td><code>${node.nodeId}</code></td><td>${node.isRunning}</td><td>${mib(node.usedBytes)}</td><td>${mib(node.freeBytes)}</td><td>${fmt.format(node.replicaCount)}</td></tr>
      `).join('');
    }
    load().catch(err => document.querySelector('#line').textContent = err.message);
    setInterval(() => load().catch(console.error), 5000);
  </script>
</body>
</html>
""";
}

public partial class Program;
