using System.Diagnostics;
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

var cluster = new LocalCluster(LocalClusterOptions.CreateDefault(runtimeRoot));
await cluster.StartAsync();

builder.Services.AddSingleton<LocalRuntimeMetrics>();
builder.Services.AddSingleton(cluster);
builder.Services.AddSingleton<DemoTrafficRunner>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DemoTrafficRunner>());

var app = builder.Build();
app.UseCors();

app.Lifetime.ApplicationStopping.Register(() =>
{
    cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/", () => Results.Redirect("/demo"));

app.MapGet("/demo", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Hedgehog Demo Runtime</title>
      <style>
        :root { color-scheme: light; font-family: ui-sans-serif, system-ui, -apple-system, Segoe UI, sans-serif; background: #f7f8fb; color: #1c2634; }
        body { margin: 0; }
        main { max-width: 1120px; margin: 0 auto; padding: 24px; }
        h1 { font-size: 28px; margin: 0 0 4px; letter-spacing: 0; }
        h2 { font-size: 16px; margin: 0 0 12px; }
        .top { display: flex; justify-content: space-between; gap: 16px; align-items: end; margin-bottom: 20px; }
        .muted { color: #5c6878; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
        .card { background: #fff; border: 1px solid #dbe1ea; border-radius: 8px; padding: 14px; box-shadow: 0 1px 2px rgba(30, 42, 56, .06); }
        .value { font-size: 24px; font-weight: 700; margin-top: 4px; }
        .status { display: inline-flex; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 999px; background: #e9f7ef; color: #116233; font-weight: 650; }
        .status.off { background: #fff6de; color: #7a5200; }
        table { width: 100%; border-collapse: collapse; font-size: 14px; }
        th, td { text-align: left; padding: 9px 8px; border-bottom: 1px solid #e5eaf1; vertical-align: top; }
        th { color: #536071; font-size: 12px; text-transform: uppercase; }
        code { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 12px; }
        .section { margin-top: 18px; }
        .error { color: #9b2226; }
      </style>
    </head>
    <body>
      <main>
        <div class="top">
          <div>
            <h1>Hedgehog Demo Runtime</h1>
            <div class="muted">Live local cluster state and synthetic write/read/delete traffic.</div>
          </div>
          <div id="runner-state" class="status off">loading</div>
        </div>
        <section class="grid" id="cards"></section>
        <section class="section card">
          <h2>Recent Activity</h2>
          <table><thead><tr><th>Tick</th><th>Object</th><th>Head</th><th>Replicas</th><th>Completed</th></tr></thead><tbody id="activity"></tbody></table>
        </section>
        <section class="section card">
          <h2>Recent Failures</h2>
          <table><thead><tr><th>Time</th><th>Object</th><th>Error</th></tr></thead><tbody id="failures"></tbody></table>
        </section>
      </main>
      <script>
        const fmt = value => value == null ? "n/a" : new Date(value).toLocaleString();
        const num = value => Number(value).toLocaleString();
        const text = value => String(value ?? "").replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[ch]));
        async function refresh() {
          const response = await fetch("/runtime/demo/status", { cache: "no-store" });
          const data = await response.json();
          const state = document.querySelector("#runner-state");
          state.textContent = data.enabled ? `traffic on, every ${data.interval}` : "traffic off";
          state.className = data.enabled ? "status" : "status off";
          document.querySelector("#cards").innerHTML = [
            ["Heads", `${num(data.runningHeads)} / ${num(data.headCount)}`],
            ["Storage Nodes", `${num(data.runningStorageNodes)} / ${num(data.storageNodeCount)}`],
            ["Replica Files", num(data.replicaFiles)],
            ["Healthy Replicas", num(data.metadata.healthyReplicas)],
            ["Objects", num(data.metadata.objects)],
            ["Delete Markers", num(data.metadata.deleteMarkers)],
            ["Writes", num(data.writesSucceeded)],
            ["Reads", num(data.readsSucceeded)],
            ["Deletes", num(data.deletesSucceeded)],
            ["Failures", num(data.ticksFailed)]
          ].map(([label, value]) => `<div class="card"><div class="muted">${label}</div><div class="value">${value}</div></div>`).join("");
          document.querySelector("#activity").innerHTML = data.recentActivity.length
            ? data.recentActivity.slice().reverse().map(item => `<tr><td>${item.tick}</td><td><code>${text(item.objectName)}</code></td><td><code>${text(item.headId)}</code></td><td>${item.replicaCount}</td><td>${fmt(item.completedAt)}</td></tr>`).join("")
            : `<tr><td colspan="5" class="muted">No synthetic traffic yet.</td></tr>`;
          document.querySelector("#failures").innerHTML = data.recentFailures.length
            ? data.recentFailures.slice().reverse().map(item => `<tr><td>${fmt(item.failedAt)}</td><td><code>${text(item.objectName)}</code></td><td class="error">${text(item.errorType)}: ${text(item.message)}</td></tr>`).join("")
            : `<tr><td colspan="3" class="muted">No recent failures.</td></tr>`;
        }
        refresh();
        setInterval(refresh, 5000);
      </script>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));

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
    return Results.Ok(new RuntimeStatusDto(
        snapshot.RuntimeRoot,
        snapshot.MetadataPath,
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

app.MapGet("/runtime/demo/status", async (DemoTrafficRunner runner, CancellationToken cancellationToken) =>
    Results.Ok(await runner.SnapshotAsync(cancellationToken)));

app.MapPost("/runtime/demo/tick", async (DemoTrafficRunner runner, CancellationToken cancellationToken) =>
    Results.Ok(await runner.RunOnceAsync(cancellationToken)));

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

public partial class Program;
