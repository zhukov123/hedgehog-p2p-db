using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

var runtimeRoot = Path.Combine(Path.GetTempPath(), $"hedgehog-local-runtime-api-test-{Guid.NewGuid():N}");

try
{
    var contentRoot = Path.Combine(FindRepoRoot(), "src", "Hedgehog.LocalRuntime.Api");
    await using var app = new WebApplicationFactory<LocalRuntimeApiAssemblyMarker>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["runtime-root"] = runtimeRoot,
                    ["reset-runtime"] = "true",
                });
            });
        });

    using var client = app.CreateClient();

    var live = await client.GetFromJsonAsync<TestHealthStatusDto>("/health/live")
        ?? throw new InvalidOperationException("live health endpoint returned no payload");
    Equal("live", live.Status);

    var readyResponse = await client.GetAsync("/health/ready");
    Equal(HttpStatusCode.OK, readyResponse.StatusCode);
    var ready = await readyResponse.Content.ReadFromJsonAsync<TestHealthStatusDto>()
        ?? throw new InvalidOperationException("ready health endpoint returned no payload");
    Equal("ready", ready.Status);
    Equal("2/2", ready.Details["heads"]);
    Equal("3/3", ready.Details["storage_nodes"]);
    Equal("1", ready.Details["tenants"]);

    var cluster = await client.GetFromJsonAsync<TestClusterHealthDto>("/health/cluster")
        ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
    Equal("ready", cluster.Status);
    Equal(1, cluster.Tenants.Count);
    Equal(2, cluster.Heads.Count);
    Equal(3, cluster.StorageNodes.Count);
    True(cluster.MetadataCounts["tenants"] >= 1, "cluster health should expose metadata counts");
    True(cluster.MetadataCounts["datasets"] >= 1, "cluster health should expose dataset counts");

    Console.WriteLine("Hedgehog.LocalRuntime.Api.Tests passed.");
}
finally
{
    if (Directory.Exists(runtimeRoot))
    {
        Directory.Delete(runtimeRoot, recursive: true);
    }
}

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Hedgehog.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not find repository root containing Hedgehog.sln.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public sealed record TestHealthStatusDto(
    string Status,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyDictionary<string, string> Details);

public sealed record TestClusterHealthDto(
    string Status,
    DateTimeOffset CheckedAtUtc,
    string RuntimeRoot,
    string MetadataPath,
    IReadOnlyList<TestLocalTenantSnapshotDto> Tenants,
    IReadOnlyList<TestHeadNodeSnapshotDto> Heads,
    IReadOnlyList<TestStorageNodeStatusDto> StorageNodes,
    IReadOnlyDictionary<string, long> MetadataCounts);

public sealed record TestLocalTenantSnapshotDto(
    string TenantId,
    string DatasetId,
    int HeadCount,
    int RequiredReplicaCount);

public sealed record TestHeadNodeSnapshotDto(
    string HeadId,
    bool IsRunning,
    string TenantId,
    string DatasetId,
    int RequiredReplicaCount);

public sealed record TestStorageNodeStatusDto(
    string NodeId,
    bool IsRunning,
    long CapacityBytes,
    long UsedBytes,
    long FreeBytes,
    int ReplicaCount);
