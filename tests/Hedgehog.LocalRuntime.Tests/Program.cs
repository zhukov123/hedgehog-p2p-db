using Hedgehog.LocalRuntime;
using Hedgehog.LocalRuntime.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using System.Net;
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
    await CapacitySafeFailsClosedAsync(Path.Combine(runtimeRoot, "capacity-safe"));
    await ReadinessTimeoutFailsClosedAsync(Path.Combine(runtimeRoot, "readiness-timeout"));
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
    Equal(LocalRuntimeReadinessEvaluator.SchemaVersion, ready.SchemaVersion);
    Equal("ready", ready.Status);
    Equal(6, ready.Gates.Count);
    Equal(true, ready.Gates.All(gate => gate.Status == LocalRuntimeReadinessGateStatus.Passed));
    Equal(1, ready.TenantCount);
    Equal(2, ready.RunningHeads);
    Equal(2, ready.TotalHeads);
    Equal(3, ready.RunningStorageNodes);
    Equal(3, ready.TotalStorageNodes);

    var cluster = await client.GetFromJsonAsync<HealthClusterDto>("/health/cluster")
        ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
    Equal(ready.TotalHeads, cluster.TotalHeads);
    Equal(ready.TotalStorageNodes, cluster.TotalStorageNodes);
    Equal(true, cluster.Ready);

    var metrics = await client.GetStringAsync("/metrics");
    True(metrics.Contains("hedgehog_runtime_readiness_gate_status{label=\"schema_current\",status=\"passed\"} 1", StringComparison.Ordinal), "metrics should expose readiness gate status from evaluator output");

    await ExpiredOutboxClaimFailsReadinessAsync(Path.Combine(runtimeRoot, "expired-claim"));
    await StalePendingOutboxFailsReadinessAsync(Path.Combine(runtimeRoot, "stale-outbox"));
    await ActiveOutboxLeaseDoesNotFailReadinessAsync(Path.Combine(runtimeRoot, "active-outbox-lease"));
    await MetadataIntegrityFailureFailsReadinessAsync(Path.Combine(runtimeRoot, "metadata-integrity"));
    await AuditWritableFailureIsUnknownAsync(Path.Combine(runtimeRoot, "audit-writable"));
    await HttpReadinessTimeoutFailsClosedAsync(Path.Combine(runtimeRoot, "http-timeout"));
    await MissingReplicaBlobFailsReadinessAsync(Path.Combine(runtimeRoot, "missing-replica"));
}

static async Task CapacitySafeFailsClosedAsync(string runtimeRoot)
{
    await using var cluster = new LocalCluster(LocalClusterOptions.CreateDefault(runtimeRoot));
    await cluster.StartAsync();
    var writer = cluster.CreateClient("capacity-writer");
    await writer.PutTextAsync("capacity-pressure.txt", "pressure");
    var evaluator = new LocalRuntimeReadinessEvaluator(new LocalRuntimeReadinessOptions(
        OutboxMaxAvailableAge: TimeSpan.FromMinutes(5),
        GateTimeout: TimeSpan.FromSeconds(2),
        EmergencyFreeBytesRatio: 0.99d));

    var result = await evaluator.EvaluateAsync(cluster);

    Equal(false, result.Ready);
    var gate = result.Gates.Single(gate => gate.Label == "capacity_safe");
    Equal(LocalRuntimeReadinessGateStatus.Failed, gate.Status);
    Equal("3", gate.Diagnostics["emergency_pressure_count"]);
}

static async Task ReadinessTimeoutFailsClosedAsync(string runtimeRoot)
{
    await using var cluster = new LocalCluster(LocalClusterOptions.CreateDefault(runtimeRoot));
    await cluster.StartAsync();
    var evaluator = new LocalRuntimeReadinessEvaluator(new LocalRuntimeReadinessOptions(
        OutboxMaxAvailableAge: TimeSpan.FromMinutes(5),
        GateTimeout: TimeSpan.Zero,
        EmergencyFreeBytesRatio: 0.05d));

    var result = await evaluator.EvaluateAsync(cluster);

    Equal(false, result.Ready);
    Equal(true, result.Gates.All(gate => gate.Status == LocalRuntimeReadinessGateStatus.Unknown));
    Equal(true, result.Gates.All(gate => gate.Diagnostics["reason"] == "timeout"));
}

static async Task ExpiredOutboxClaimFailsReadinessAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(runtimeRoot, async client =>
    {
        await AssertReadyAsync(client);
        await SeedOutboxAsync(
            runtimeRoot,
            "expired-claim-outbox",
            availableAtMs: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds(),
            claimedBy: "readiness-test",
            claimedUntilMs: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());

        await AssertNotReadyAsync(client, "outbox_reconciled", ("expired_claim_count", "1"));
    });
}

static async Task StalePendingOutboxFailsReadinessAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(runtimeRoot, async client =>
    {
        await AssertReadyAsync(client);
        await SeedOutboxAsync(
            runtimeRoot,
            "stale-pending-outbox",
            availableAtMs: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds(),
            claimedBy: null,
            claimedUntilMs: null);

        await AssertNotReadyAsync(client, "outbox_reconciled", ("pending_count", "1"));
    });
}

static async Task ActiveOutboxLeaseDoesNotFailReadinessAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(runtimeRoot, async client =>
    {
        await AssertReadyAsync(client);
        await SeedOutboxAsync(
            runtimeRoot,
            "active-lease-outbox",
            availableAtMs: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds(),
            claimedBy: "readiness-test",
            claimedUntilMs: DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());

        var cluster = await client.GetFromJsonAsync<HealthClusterDto>("/health/cluster")
            ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
        Equal(true, cluster.Ready);
        var gate = cluster.Gates.Single(gate => gate.Label == "outbox_reconciled");
        Equal(LocalRuntimeReadinessGateStatus.Passed, gate.Status);
        Equal("0", gate.Diagnostics["oldest_available_age_ms"]);
    });
}

static async Task MetadataIntegrityFailureFailsReadinessAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(runtimeRoot, async client =>
    {
        await AssertReadyAsync(client);
        await ExecuteSqlAsync(
            runtimeRoot,
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO replicas (
                replica_id,
                version_id,
                node_id,
                state,
                placement_epoch,
                fencing_token,
                created_at_ms,
                updated_at_ms
            )
            VALUES (
                'readiness-orphan-replica',
                'missing-version',
                'node-1',
                'healthy',
                1,
                0,
                @now_ms,
                @now_ms
            );
            """,
            ("@now_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        await AssertNotReadyAsync(client, "metadata_integrity", ("violation_count", "1"));
    });
}

static async Task AuditWritableFailureIsUnknownAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(runtimeRoot, async client =>
    {
        await AssertReadyAsync(client);
        await ExecuteSqlAsync(runtimeRoot, "DROP TABLE audit_events;");

        await AssertNotReadyAsync(
            client,
            "audit_writable",
            LocalRuntimeReadinessGateStatus.Unknown,
            ("reason", "probe_error"));
    });
}

static async Task HttpReadinessTimeoutFailsClosedAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(
        runtimeRoot,
        async client =>
        {
            var readyResponse = await client.GetAsync("/health/ready");
            Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);

            var cluster = await client.GetFromJsonAsync<HealthClusterDto>("/health/cluster")
                ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
            Equal(false, cluster.Ready);
            Equal(true, cluster.Gates.All(gate => gate.Status == LocalRuntimeReadinessGateStatus.Unknown));
            Equal(true, cluster.Gates.All(gate => gate.Diagnostics["reason"] == "timeout"));
        },
        ("readiness:gate-timeout-ms", "0"));
}

static async Task MissingReplicaBlobFailsReadinessAsync(string runtimeRoot)
{
    await WithRuntimeApiAsync(runtimeRoot, async client =>
    {
        await AssertReadyAsync(client);
        var put = await client.PostAsJsonAsync(
            "/runtime/tenants/tenant-local/datasets/dataset-local/objects",
            new PutObjectRequest("readiness-writer", "readiness-storage.txt", "replica body"));
        put.EnsureSuccessStatusCode();
        var response = await put.Content.ReadFromJsonAsync<PutObjectResponse>()
            ?? throw new InvalidOperationException("put endpoint returned no payload");
        var replica = await FirstReplicaAsync(runtimeRoot, response.VersionId);
        var replicaPath = Path.Combine(
            runtimeRoot,
            "storage",
            replica.NodeId,
            "replicas",
            response.VersionId,
            $"{replica.ReplicaId}.bin");
        File.Delete(replicaPath);

        await AssertNotReadyAsync(client, "storage_consistent", ("missing_blob_count", "1"));
    });
}

static async Task WithRuntimeApiAsync(
    string runtimeRoot,
    Func<HttpClient, Task> action,
    params (string Key, string Value)[] settings)
{
    var contentRoot = Path.Combine(FindRepoRoot(), "src", "Hedgehog.LocalRuntime.Api");
    await using var app = new WebApplicationFactory<LocalRuntimeApiAssemblyMarker>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("runtime-root", runtimeRoot);
            builder.UseSetting("reset-runtime", "true");
            builder.UseSetting("readiness:outbox-max-age-ms", "300000");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    using var client = app.CreateClient();
    await action(client);
}

static async Task AssertReadyAsync(HttpClient client)
{
    var response = await client.GetAsync("/health/ready");
    Equal(HttpStatusCode.OK, response.StatusCode);
    var ready = await response.Content.ReadFromJsonAsync<HealthClusterDto>()
        ?? throw new InvalidOperationException("ready health endpoint returned no payload");
    Equal(true, ready.Ready);
    Equal(true, ready.Gates.All(gate => gate.Status == LocalRuntimeReadinessGateStatus.Passed));
}

static async Task AssertNotReadyAsync(
    HttpClient client,
    string failedGateLabel,
    (string Key, string Value) expectedDiagnostic) =>
    await AssertNotReadyAsync(
        client,
        failedGateLabel,
        LocalRuntimeReadinessGateStatus.Failed,
        expectedDiagnostic);

static async Task AssertNotReadyAsync(
    HttpClient client,
    string gateLabel,
    string expectedStatus,
    (string Key, string Value) expectedDiagnostic)
{
    var readyResponse = await client.GetAsync("/health/ready");
    Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);

    var cluster = await client.GetFromJsonAsync<HealthClusterDto>("/health/cluster")
        ?? throw new InvalidOperationException("cluster health endpoint returned no payload");
    Equal(false, cluster.Ready);
    var gate = cluster.Gates.Single(gate => gate.Label == gateLabel);
    Equal(expectedStatus, gate.Status);
    Equal(expectedDiagnostic.Value, gate.Diagnostics[expectedDiagnostic.Key]);
    True(cluster.Gates.Count >= 6, "cluster health should include individual gate diagnostics when not ready");
}

static async Task SeedOutboxAsync(
    string runtimeRoot,
    string outboxId,
    long availableAtMs,
    string? claimedBy,
    long? claimedUntilMs)
{
    await ExecuteSqlAsync(
        runtimeRoot,
        """
        INSERT INTO outbox_events (
            outbox_id,
            workflow,
            destination_node_id,
            topic,
            payload,
            idempotency_key,
            available_at_ms,
            claimed_by,
            claimed_until_ms,
            delivered_at_ms,
            created_at_ms
        )
        VALUES (
            @outbox_id,
            'claim_outbox',
            NULL,
            'readiness.test',
            X'00',
            @idempotency_key,
            @available_at_ms,
            @claimed_by,
            @claimed_until_ms,
            NULL,
            @available_at_ms
        );
        """,
        ("@outbox_id", outboxId),
        ("@idempotency_key", $"idem-{outboxId}"),
        ("@available_at_ms", availableAtMs),
        ("@claimed_by", claimedBy),
        ("@claimed_until_ms", claimedUntilMs));
}

static async Task<(string NodeId, string ReplicaId)> FirstReplicaAsync(string runtimeRoot, string versionId)
{
    await using var connection = await OpenSqliteAsync(runtimeRoot);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT node_id, replica_id
        FROM replicas
        WHERE version_id = @version_id AND state = 'healthy'
        ORDER BY node_id, replica_id
        LIMIT 1;
        """;
    command.Parameters.AddWithValue("@version_id", versionId);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("Expected put object to create at least one healthy replica.");
    }

    return (reader.GetString(0), reader.GetString(1));
}

static async Task ExecuteSqlAsync(
    string runtimeRoot,
    string sql,
    params (string Name, object? Value)[] parameters)
{
    await using var connection = await OpenSqliteAsync(runtimeRoot);
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    foreach (var (name, value) in parameters)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    await command.ExecuteNonQueryAsync();
}

static async Task<SqliteConnection> OpenSqliteAsync(string runtimeRoot)
{
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(runtimeRoot, "metadata", "hedgehog.sqlite"),
        Cache = SqliteCacheMode.Shared,
    }.ToString();
    var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    return connection;
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
