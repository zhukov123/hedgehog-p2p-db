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

var app = builder.Build();
app.UseCors();

app.Lifetime.ApplicationStopping.Register(() =>
{
    cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/", () => Results.Redirect("/runtime/status"));

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

app.MapGet("/metrics", async (
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var snapshot = await runtime.SnapshotAsync(cancellationToken);
    var metadataCounts = await LoadMetadataCountsAsync(runtime, cancellationToken);
    var outbox = await runtime.OutboxSnapshotAsync(cancellationToken);
    return Results.Text(
        metrics.RenderPrometheus(snapshot, metadataCounts, outbox),
        "text/plain; version=0.0.4; charset=utf-8");
});

app.MapPost("/runtime/outbox/dispatch", async (
    DispatchOutboxRequest request,
    LocalCluster runtime,
    LocalRuntimeMetrics metrics,
    CancellationToken cancellationToken) =>
{
    var result = await runtime.DispatchOutboxAsync(
        request.MaxItems <= 0 ? 25 : request.MaxItems,
        request.LeaseSeconds <= 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(request.LeaseSeconds),
        string.IsNullOrWhiteSpace(request.Topic) ? null : request.Topic,
        cancellationToken);
    metrics.RecordOutboxDispatch(result);
    var state = await runtime.OutboxSnapshotAsync(cancellationToken);
    return Results.Ok(new DispatchOutboxResponse(
        result.Claimed,
        result.Delivered,
        result.Failed,
        state.PendingRows,
        state.LeasedRows,
        state.FailedRows,
        state.DeliveredRows,
        state.OldestPendingAgeSeconds));
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

public sealed record DispatchOutboxRequest(int MaxItems = 25, int LeaseSeconds = 30, string? Topic = null);

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

public sealed record DispatchOutboxResponse(
    int Claimed,
    int Delivered,
    int Failed,
    long PendingRows,
    long LeasedRows,
    long FailedRows,
    long DeliveredRows,
    long OldestPendingAgeSeconds);

public partial class Program;
