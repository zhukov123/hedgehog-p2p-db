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

app.MapGet("/health/live", () => Results.Ok(HealthStatusDto.Live()));

app.MapGet("/health/ready", async (LocalCluster runtime, CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await runtime.SnapshotAsync(cancellationToken);
        var metadataRows = await runtime.ScalarLongAsync("SELECT COUNT(*) FROM __hedgehog_schema_migrations;", cancellationToken);
        var ready = ClusterIsReady(snapshot) && metadataRows > 0;
        var response = HealthStatusDto.Ready(
            ready,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["heads"] = RunningCount(snapshot.Heads.Select(head => head.IsRunning), snapshot.Heads.Count),
                ["storage_nodes"] = RunningCount(snapshot.StorageNodes.Select(node => node.IsRunning), snapshot.StorageNodes.Count),
                ["tenants"] = snapshot.Tenants.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schema_migrations"] = metadataRows.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        return ready
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
    {
        return Results.Json(
            HealthStatusDto.Unready(ex.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/health/cluster", async (LocalCluster runtime, CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await runtime.SnapshotAsync(cancellationToken);
        var metadataCounts = await LoadMetadataCountsAsync(runtime, cancellationToken);
        var ready = ClusterIsReady(snapshot);
        var response = new ClusterHealthDto(
            ready ? "ready" : "unready",
            DateTimeOffset.UtcNow,
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
                node.Replicas.Count)).ToArray(),
            metadataCounts);

        return ready
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
    {
        return Results.Json(
            HealthStatusDto.Unready(ex.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

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

static bool ClusterIsReady(LocalClusterSnapshot snapshot) =>
    snapshot.Heads.Count > 0
    && snapshot.StorageNodes.Count > 0
    && snapshot.Tenants.Count > 0
    && snapshot.Heads.All(head => head.IsRunning)
    && snapshot.StorageNodes.All(node => node.IsRunning);

static string RunningCount(IEnumerable<bool> values, int total) =>
    $"{values.Count(value => value)}/{total}";

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

public sealed record CreateTenantRequest(string TenantId, string DatasetId);

public sealed record PutObjectRequest(string ClientId, string Name, string Text, bool PreferLastHead = false);

public sealed record ErrorDto(string Error);

public sealed record HealthStatusDto(
    string Status,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyDictionary<string, string> Details)
{
    public static HealthStatusDto Live() =>
        new("live", DateTimeOffset.UtcNow, new Dictionary<string, string>(StringComparer.Ordinal));

    public static HealthStatusDto Ready(bool ready, IReadOnlyDictionary<string, string> details) =>
        new(ready ? "ready" : "unready", DateTimeOffset.UtcNow, details);

    public static HealthStatusDto Unready(string reason) =>
        new(
            "unready",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
            });
}

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

public sealed record ClusterHealthDto(
    string Status,
    DateTimeOffset CheckedAtUtc,
    string RuntimeRoot,
    string MetadataPath,
    IReadOnlyList<LocalTenantSnapshot> Tenants,
    IReadOnlyList<HeadNodeSnapshot> Heads,
    IReadOnlyList<StorageNodeStatusDto> StorageNodes,
    IReadOnlyDictionary<string, long> MetadataCounts);

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
