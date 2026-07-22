using Hedgehog.LocalRuntime;
using Hedgehog.LocalRuntime.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;

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
    await RuntimeApiStatusDoesNotExposeRuntimePathsAsync(Path.Combine(runtimeRoot, "api-status"));
    await RuntimeApiHealthFailsClosedForUnknownGatesAsync(Path.Combine(runtimeRoot, "api-unknown"));
    await RuntimeApiHealthFailsClosedForFailedGateAsync(Path.Combine(runtimeRoot, "api-failed"));
    await RuntimeApiHealthFailsClosedForProbeExceptionAsync(Path.Combine(runtimeRoot, "api-exception"));
    await RuntimeApiHealthFailsClosedForProbeTimeoutAsync(Path.Combine(runtimeRoot, "api-timeout"));

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
    Equal(7, result.BackupManifestEntries);
    Equal(true, result.MissingReplicaBlobRejected);
    Equal(true, result.CorruptReplicaBlobRejected);
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
    await using var app = CreateApi(runtimeRoot, new StaticRecoveryReadinessProbe(AllPassedGates()));
    using var client = app.CreateClient();

    var live = await client.GetFromJsonAsync<HealthLiveDto>("/health/live")
        ?? throw new InvalidOperationException("live health endpoint returned no payload");
    Equal("Hedgehog.LocalRuntime.Api", live.Service);
    Equal("live", live.Status);

    var readyResponse = await client.GetAsync("/health/ready");
    Equal(System.Net.HttpStatusCode.OK, readyResponse.StatusCode);
    var ready = await readyResponse.Content.ReadFromJsonAsync<HealthClusterDto>()
        ?? throw new InvalidOperationException("ready health endpoint returned no payload");
    Equal(true, ready.Ready);
    Equal("ready", ready.Status);
    Equal(1, ready.TenantCount);
    Equal(2, ready.RunningHeads);
    Equal(2, ready.TotalHeads);
    Equal(3, ready.RunningStorageNodes);
    Equal(3, ready.TotalStorageNodes);
    Equal(true, ready.MetadataAvailable);
    AssertCanonicalGates(ready.Recovery);
    True(ready.Recovery.Gates.All(gate => gate.Status == RecoveryReadinessEvaluator.Passed), "all-passed probe should keep every gate passed");

    var cluster = await client.GetFromJsonAsync<HealthClusterDto>("/health/cluster")
        ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
    Equal(true, cluster.Ready);
    Equal(ready.TotalHeads, cluster.TotalHeads);
    Equal(ready.TotalStorageNodes, cluster.TotalStorageNodes);
    AssertCanonicalGates(cluster.Recovery);

    var clusterPayload = await client.GetStringAsync("/health/cluster");
    False(clusterPayload.Contains(runtimeRoot, StringComparison.Ordinal), "cluster health should not expose runtime root");
    False(clusterPayload.Contains("metadata", StringComparison.OrdinalIgnoreCase) && clusterPayload.Contains(".sqlite", StringComparison.OrdinalIgnoreCase), "cluster health should not expose metadata path");

    var metrics = await client.GetStringAsync("/metrics");
    True(metrics.Contains("hedgehog_runtime_recovery_ready 1", StringComparison.Ordinal), "metrics should render the same ready decision");
}

static async Task RuntimeApiStatusDoesNotExposeRuntimePathsAsync(string runtimeRoot)
{
    await using var app = CreateApi(runtimeRoot, new StaticRecoveryReadinessProbe(AllPassedGates()));
    using var client = app.CreateClient();

    var payload = await client.GetStringAsync("/runtime/status");
    False(payload.Contains(runtimeRoot, StringComparison.Ordinal), "runtime status should not expose runtime root");
    False(payload.Contains("metadata", StringComparison.OrdinalIgnoreCase) && payload.Contains(".sqlite", StringComparison.OrdinalIgnoreCase), "runtime status should not expose metadata path");

    using var document = JsonDocument.Parse(payload);
    var root = document.RootElement;
    Equal(RuntimeStatusDto.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
    Equal("running", root.GetProperty("status").GetString());
    Equal(false, root.TryGetProperty("runtimeRoot", out _));
    Equal(false, root.TryGetProperty("metadataPath", out _));
    Equal(2, root.GetProperty("heads").GetArrayLength());
    Equal(3, root.GetProperty("storageNodes").GetArrayLength());
}

static async Task RuntimeApiHealthFailsClosedForUnknownGatesAsync(string runtimeRoot)
{
    await using var app = CreateApi(runtimeRoot);
    using var client = app.CreateClient();

    var response = await client.GetAsync("/health/ready");
    Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    var ready = await response.Content.ReadFromJsonAsync<HealthClusterDto>()
        ?? throw new InvalidOperationException("closed ready health endpoint returned no payload");
    Equal(false, ready.Ready);
    Equal("not_ready", ready.Status);
    AssertCanonicalGates(ready.Recovery);
    True(ready.Recovery.Gates.Any(gate => gate.Name == "manifest_reconciliation" && gate.Status == RecoveryReadinessEvaluator.Unknown), "manifest reconciliation should remain unknown until implemented");
    True(ready.Recovery.Gates.Any(gate => gate.Name == "reservation_reconciliation" && gate.Status == RecoveryReadinessEvaluator.Unknown), "reservation reconciliation should remain unknown until implemented");
    True(ready.Recovery.Gates.Any(gate => gate.Name == "repair_deficit" && gate.Status == RecoveryReadinessEvaluator.Unknown), "repair deficit should remain unknown until implemented");
    True(ready.Recovery.Gates.Any(gate => gate.Name == "fresh_capacity_reports" && gate.Status == RecoveryReadinessEvaluator.Unknown), "fresh capacity reports should remain unknown until implemented");
}

static async Task RuntimeApiHealthFailsClosedForFailedGateAsync(string runtimeRoot)
{
    var gates = AllPassedGates()
        .Select(gate => gate.Name == "audit_continuity"
            ? new RecoveryGateProbeResult(gate.Name, RecoveryReadinessEvaluator.Failed, "audit_gap")
            : gate)
        .ToArray();
    await using var app = CreateApi(runtimeRoot, new StaticRecoveryReadinessProbe(gates));
    using var client = app.CreateClient();

    var response = await client.GetAsync("/health/ready");
    Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    var ready = await response.Content.ReadFromJsonAsync<HealthClusterDto>()
        ?? throw new InvalidOperationException("failed ready health endpoint returned no payload");
    Equal(false, ready.Ready);
    var failed = ready.Recovery.Gates.Single(gate => gate.Status == RecoveryReadinessEvaluator.Failed);
    Equal("audit_continuity", failed.Name);
    Equal("audit_gap", failed.Reason);

    var metrics = await client.GetStringAsync("/metrics");
    True(metrics.Contains("hedgehog_runtime_recovery_ready 0", StringComparison.Ordinal), "metrics should render the same not-ready decision");
}

static async Task RuntimeApiHealthFailsClosedForProbeExceptionAsync(string runtimeRoot)
{
    await using var app = CreateApi(runtimeRoot, new ThrowingRecoveryReadinessProbe());
    using var client = app.CreateClient();

    var response = await client.GetAsync("/health/ready");
    Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    var payload = await response.Content.ReadAsStringAsync();
    False(payload.Contains(runtimeRoot, StringComparison.Ordinal), "exception payload should not expose runtime root");
    False(payload.Contains("boom", StringComparison.OrdinalIgnoreCase), "exception payload should not expose exception detail");

    var ready = await response.Content.ReadFromJsonAsync<HealthClusterDto>()
        ?? throw new InvalidOperationException("exception ready health endpoint returned no payload");
    Equal(false, ready.Ready);
    AssertCanonicalGates(ready.Recovery);
    True(ready.Recovery.Gates.All(gate => gate.Status == RecoveryReadinessEvaluator.Unknown), "probe exceptions should make every gate unknown");
}

static async Task RuntimeApiHealthFailsClosedForProbeTimeoutAsync(string runtimeRoot)
{
    await using var app = CreateApi(
        runtimeRoot,
        new SlowRecoveryReadinessProbe(),
        new RecoveryReadinessOptions { EvaluationTimeout = TimeSpan.FromMilliseconds(25) });
    using var client = app.CreateClient();

    var response = await client.GetAsync("/health/ready");
    Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    var ready = await response.Content.ReadFromJsonAsync<HealthClusterDto>()
        ?? throw new InvalidOperationException("timeout ready health endpoint returned no payload");
    Equal(false, ready.Ready);
    Equal(false, ready.MetadataAvailable);
    AssertCanonicalGates(ready.Recovery);
    True(ready.Recovery.Gates.All(gate => gate.Status == RecoveryReadinessEvaluator.Unknown), "probe timeouts should make every gate unknown");
    True(ready.Recovery.Gates.All(gate => gate.Reason == "probe_timeout"), "probe timeouts should use a bounded reason");
}

static WebApplicationFactory<LocalRuntimeApiAssemblyMarker> CreateApi(
    string runtimeRoot,
    IRecoveryReadinessProbe? probe = null,
    RecoveryReadinessOptions? options = null)
{
    var contentRoot = Path.Combine(FindRepoRoot(), "src", "Hedgehog.LocalRuntime.Api");
    return new WebApplicationFactory<LocalRuntimeApiAssemblyMarker>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("runtime-root", runtimeRoot);
            builder.UseSetting("reset-runtime", "true");
            builder.ConfigureTestServices(services =>
            {
                if (probe is not null)
                {
                    services.AddSingleton(probe);
                }

                if (options is not null)
                {
                    services.AddSingleton(options);
                }
            });
        });
}

static IReadOnlyList<RecoveryGateProbeResult> AllPassedGates() =>
    RecoveryReadinessEvaluator.CanonicalGateNames
        .Select(name => new RecoveryGateProbeResult(name, RecoveryReadinessEvaluator.Passed, "test_passed"))
        .ToArray();

static void AssertCanonicalGates(RecoveryReadinessDto recovery)
{
    Equal(RecoveryReadinessEvaluator.SchemaVersion, recovery.SchemaVersion);
    Equal(
        string.Join(",", RecoveryReadinessEvaluator.CanonicalGateNames),
        string.Join(",", recovery.Gates.Select(gate => gate.Name)));
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

static void False(bool condition, string message)
{
    if (condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class StaticRecoveryReadinessProbe(IReadOnlyList<RecoveryGateProbeResult> gates) : IRecoveryReadinessProbe
{
    public Task<RecoveryReadinessProbeSnapshot> EvaluateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RecoveryReadinessProbeSnapshot(
            new RecoveryOperationalSummaryDto(
                MetadataAvailable: true,
                TenantCount: 1,
                RunningHeads: 2,
                TotalHeads: 2,
                RunningStorageNodes: 3,
                TotalStorageNodes: 3),
            gates));
}

internal sealed class ThrowingRecoveryReadinessProbe : IRecoveryReadinessProbe
{
    public Task<RecoveryReadinessProbeSnapshot> EvaluateAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom C:\\secret\\metadata\\hedgehog.sqlite");
}

internal sealed class SlowRecoveryReadinessProbe : IRecoveryReadinessProbe
{
    public async Task<RecoveryReadinessProbeSnapshot> EvaluateAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}
