using Hedgehog.LocalRuntime;
using Hedgehog.LocalRuntime.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;

var runtimeRoot = Path.Combine(Path.GetTempPath(), $"hedgehog-local-runtime-test-{Guid.NewGuid():N}");
try
{
    var result = await LocalRuntimeSmoke.RunAsync(LocalClusterOptions.CreateDefault(Path.Combine(runtimeRoot, "smoke")));

    Equal(2, result.HeadCount);
    Equal(3, result.StorageNodeCount);
    Equal(2, result.PublishedObjects);
    Equal(2, result.VerifiedRetrievals);
    Equal(true, result.DeleteVerified);
    Equal(2, result.MetadataObjectRows);
    Equal(6, result.HealthyReplicaRows);

    await MultiTenantIsolationAndDeleteAsync(Path.Combine(runtimeRoot, "isolation"));
    await StressScenarioAsync(Path.Combine(runtimeRoot, "stress"));
    await RestoreDrillAsync(Path.Combine(runtimeRoot, "restore"));
    await RuntimeApiHealthEndpointsAsync(Path.Combine(runtimeRoot, "api-health"));

    Console.WriteLine("Hedgehog.LocalRuntime.Tests passed.");
}

finally
{
    if (Directory.Exists(runtimeRoot))
    {
        Directory.Delete(runtimeRoot, recursive: true);
    }
}

static async Task RestoreDrillAsync(string runtimeRoot)
{
    var result = await LocalRuntimeRestoreDrill.RunAsync(LocalClusterOptions.CreateDefault(runtimeRoot));

    Equal(2, result.HeadCountAfterRestore);
    Equal(3, result.StorageNodeCountAfterRestore);
    Equal(1, result.ReadsVerifiedAfterRestore);
    Equal(true, result.DeleteMarkerRecovered);
    Equal(2, result.MetadataObjectRows);
    Equal(3, result.MetadataVersionRows);
    Equal(6, result.HealthyReplicaRows);
    Equal(6, result.HealthyReplicasVerified);
    Equal(6, result.CommittedReservationRows);
    Equal(1, result.PendingOutboxRows);
    Equal(1, result.PendingRepairJobRows);
    True(result.AuditRows >= 7, "restore drill should preserve workflow audit rows");
}

static async Task MultiTenantIsolationAndDeleteAsync(string runtimeRoot)
{
    await using var cluster = new LocalCluster(LocalClusterOptions.CreateDefault(runtimeRoot));
    await cluster.StartAsync();
    await cluster.AddTenantAsync("tenant-alpha", "dataset-docs");
    await cluster.AddTenantAsync("tenant-beta", "dataset-docs");

    var alphaWriter = cluster.CreateClientForTenant("tenant-alpha", "dataset-docs", "alpha-writer");
    var betaWriter = cluster.CreateClientForTenant("tenant-beta", "dataset-docs", "beta-writer", preferLastHead: true);
    await alphaWriter.PutTextAsync("shared-name.txt", "alpha private value");
    await betaWriter.PutTextAsync("shared-name.txt", "beta private value");

    var alphaReader = cluster.CreateClientForTenant("tenant-alpha", "dataset-docs", "alpha-reader", preferLastHead: true);
    var betaReader = cluster.CreateClientForTenant("tenant-beta", "dataset-docs", "beta-reader");
    Equal("alpha private value", await alphaReader.GetTextAsync("shared-name.txt"));
    Equal("beta private value", await betaReader.GetTextAsync("shared-name.txt"));

    await alphaReader.DeleteAsync("shared-name.txt");
    Equal(true, await ThrowsInvalidOperationAsync(() => alphaReader.GetTextAsync("shared-name.txt")));
    Equal("beta private value", await betaReader.GetTextAsync("shared-name.txt"));

    Equal(2, await cluster.ScalarLongAsync("SELECT COUNT(*) FROM objects WHERE dataset_id = 'dataset-docs';"));
    Equal(1, await cluster.ScalarLongAsync("SELECT COUNT(*) FROM object_versions WHERE state = 'delete_marker';"));
    Equal(0, await cluster.ScalarLongAsync("SELECT COUNT(*) FROM objects WHERE object_id LIKE '%shared-name%';"));
}

static async Task StressScenarioAsync(string runtimeRoot)
{
    var result = await LocalRuntimeStress.RunAsync(
        new LocalRuntimeStressOptions(
            runtimeRoot,
            TenantCount: 3,
            ObjectsPerTenant: 12,
            PayloadBytes: 512));

    Equal(3, result.TenantCount);
    Equal(3, result.StorageNodeCount);
    Equal(8, result.HeadCount);
    Equal(36, result.ObjectsWritten);
    Equal(63, result.ReadsVerified);
    Equal(9, result.DeletesVerified);
    Equal(36, result.MetadataObjectRows);
    Equal(45, result.MetadataVersionRows);
    Equal(108, result.HealthyReplicaRows);
    Equal(9, result.DeleteMarkerRows);
}

static async Task RuntimeApiHealthEndpointsAsync(string runtimeRoot)
{
    var contentRoot = Path.Combine(FindRepoRoot(), "src", "Hedgehog.LocalRuntime.Api");
    await using var app = new WebApplicationFactory<LocalRuntimeApiAssemblyMarker>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("runtime-root", runtimeRoot);
            builder.UseSetting("reset-runtime", "true");
        });
    using var client = app.CreateClient();

    var live = await client.GetFromJsonAsync<HealthLiveDto>("/health/live")
        ?? throw new InvalidOperationException("live health endpoint returned no payload");
    Equal("Hedgehog.LocalRuntime.Api", live.Service);
    Equal("live", live.Status);

    var ready = await client.GetFromJsonAsync<HealthClusterDto>("/health/ready")
        ?? throw new InvalidOperationException("ready health endpoint returned no payload");
    Equal(true, ready.Ready);
    Equal("ready", ready.Status);
    Equal(1, ready.TenantCount);
    Equal(2, ready.RunningHeads);
    Equal(2, ready.TotalHeads);
    Equal(3, ready.RunningStorageNodes);
    Equal(3, ready.TotalStorageNodes);
    Equal(true, ready.MetadataAvailable);

    var cluster = await client.GetFromJsonAsync<HealthClusterDto>("/health/cluster")
        ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
    Equal(ready.TotalHeads, cluster.TotalHeads);
    Equal(ready.TotalStorageNodes, cluster.TotalStorageNodes);
    True(cluster.RuntimeRoot.EndsWith("api-health", StringComparison.Ordinal), "cluster health should expose runtime root");
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

static async Task<bool> ThrowsInvalidOperationAsync(Func<Task> action)
{
    try
    {
        await action();
        return false;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
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
