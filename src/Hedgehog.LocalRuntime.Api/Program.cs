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

builder.Services.AddSingleton(cluster);

var app = builder.Build();
app.UseCors();

app.Lifetime.ApplicationStopping.Register(() =>
{
    cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/", () => Results.Redirect("/runtime/status"));

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

app.MapPost("/runtime/tenants", async (
    CreateTenantRequest request,
    LocalCluster runtime,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.DatasetId))
    {
        return Results.BadRequest(new ErrorDto("tenantId and datasetId are required"));
    }

    var tenant = await runtime.AddTenantAsync(request.TenantId, request.DatasetId, cancellationToken);
    return Results.Ok(new TenantCreatedDto(
        tenant.TenantId,
        tenant.DatasetId,
        tenant.RequiredReplicaCount));
});

app.MapPost("/runtime/tenants/{tenantId}/datasets/{datasetId}/objects", async (
    string tenantId,
    string datasetId,
    PutObjectRequest request,
    LocalCluster runtime,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientId)
        || string.IsNullOrWhiteSpace(request.Name)
        || request.Text is null)
    {
        return Results.BadRequest(new ErrorDto("clientId, name, and text are required"));
    }

    try
    {
        var client = runtime.CreateClientForTenant(tenantId, datasetId, request.ClientId, request.PreferLastHead);
        var result = await client.PutTextAsync(request.Name, request.Text, cancellationToken);
        return Results.Ok(new PutObjectResponse(
            result.ClientId,
            result.HeadId,
            result.ObjectId,
            result.VersionId,
            result.ReplicaCount));
    }
    catch (InvalidOperationException ex)
    {
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
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(clientId))
    {
        return Results.BadRequest(new ErrorDto("name and clientId are required"));
    }

    try
    {
        var client = runtime.CreateClientForTenant(tenantId, datasetId, clientId, preferLastHead == true);
        var result = await client.GetAsync(name, cancellationToken);
        return Results.Ok(new GetObjectResponse(clientId, name, result));
    }
    catch (InvalidOperationException ex)
    {
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
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(clientId))
    {
        return Results.BadRequest(new ErrorDto("name and clientId are required"));
    }

    try
    {
        var client = runtime.CreateClientForTenant(tenantId, datasetId, clientId, preferLastHead == true);
        await client.DeleteAsync(name, cancellationToken);
        return Results.Ok(new DeleteObjectResponse(clientId, tenantId, datasetId, name, Deleted: true));
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new ErrorDto(ex.Message));
    }
});

app.Run();

public sealed record CreateTenantRequest(string TenantId, string DatasetId);

public sealed record PutObjectRequest(string ClientId, string Name, string Text, bool PreferLastHead = false);

public sealed record ErrorDto(string Error);

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
