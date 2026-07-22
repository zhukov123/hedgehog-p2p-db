using Hedgehog.Admin.Api;
using Hedgehog.Metadata.Core;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

var repository = new AdminRepository();

ClusterStatusShowsOperationalRisk(repository);
ObjectQueriesUseOpaqueIds(repository);
ClusterActionsWriteAudit(repository);
NodeActionsUpdateState(repository);
RepairActionsUseCanonicalStates(repository);
RecoveryGateActionsRequireApprovals(repository);
await AdminEndpointsServeOperationalContractsAsync();

Console.WriteLine("Hedgehog.Admin.Api.Tests passed.");

static void ClusterStatusShowsOperationalRisk(AdminRepository repository)
{
    var status = repository.GetClusterStatus();
    Equal("hedgehog-local", status.ClusterId);
    Equal("normal", status.WriteMode);
    True(status.RepairBacklogCount > 0, "repair backlog should be visible");
    True(status.Signals.Any(signal => signal.Name == "repair"), "repair signal should be visible");
}

static void ObjectQueriesUseOpaqueIds(AdminRepository repository)
{
    var objects = repository.GetObjects("tenant-alpha", "dataset-images", "under_replicated", null);
    Equal(1, objects.Count);
    True(objects[0].ObjectId.StartsWith("obj_", StringComparison.Ordinal), "object id should be opaque in admin samples");
    Equal("under_replicated", objects[0].State);
}

static void ClusterActionsWriteAudit(AdminRepository repository)
{
    var before = repository.GetAuditEvents(null, null, null, null).Count;
    var result = repository.ApplyAction(
        "cluster",
        "cluster",
        "pause-writes",
        new ActionRequestDto("admin-test", "test pause", "test-request"));

    Equal("accepted", result.Result);
    Equal("paused", repository.GetClusterStatus().WriteMode);

    repository.ApplyAction(
        "cluster",
        "cluster",
        "resume-writes",
        new ActionRequestDto("admin-test", "test resume", "test-request-2"));

    Equal("normal", repository.GetClusterStatus().WriteMode);
    True(repository.GetAuditEvents(null, null, null, null).Count >= before + 2, "admin actions should append audit events");
}

static void NodeActionsUpdateState(AdminRepository repository)
{
    repository.ApplyAction("node", "node-a", "drain", new ActionRequestDto("admin-test", "drain test"));
    var drained = repository.GetNode("node-a") ?? throw new InvalidOperationException("node-a missing");
    Equal("draining", drained.State);
    Equal(false, drained.AcceptingWrites);

    repository.ApplyAction("node", "node-a", "cancel-drain", new ActionRequestDto("admin-test", "cancel drain test"));
    var active = repository.GetNode("node-a") ?? throw new InvalidOperationException("node-a missing");
    Equal("active", active.State);
    Equal(true, active.AcceptingWrites);

    repository.ApplyAction("node", "node-a", "revoke", new ActionRequestDto("admin-test", "revoke test"));
    var revoked = repository.GetNode("node-a") ?? throw new InvalidOperationException("node-a missing");
    Equal("revoked", revoked.State);
    Equal(false, revoked.AcceptingWrites);
}

static void RepairActionsUseCanonicalStates(AdminRepository repository)
{
    repository.ApplyAction("repair-job", "repair-30291", "retry", new ActionRequestDto("admin-test", "retry test"));
    var job = repository.GetRepairQueue("pending", "critical").Single(item => item.JobId == "repair-30291");
    Equal("pending", job.State);

    repository.ApplyAction("repair-job", "repair-30291", "cancel-duplicate", new ActionRequestDto("admin-test", "cancel test"));
    var canceled = repository.GetRepairQueue("canceled_superseded", "critical").Single(item => item.JobId == "repair-30291");
    Equal("canceled_superseded", canceled.State);
}

static void RecoveryGateActionsRequireApprovals(AdminRepository repository)
{
    var gate = repository.GetRecoveryGates().Single(item => item.GateId == "gate-capacity-emergency");
    var approvals = gate.Approvals;

    repository.ApplyAction("recovery-gate", gate.GateId, "approve", new ActionRequestDto("admin-test", "approve test"));
    var approved = repository.GetRecoveryGates().Single(item => item.GateId == gate.GateId);
    Equal(approvals + 1, approved.Approvals);

    repository.ApplyAction("recovery-gate", gate.GateId, "acknowledge", new ActionRequestDto("admin-test", "acknowledge test"));
    var acknowledged = repository.GetRecoveryGates().Single(item => item.GateId == gate.GateId);
    Equal("closed", acknowledged.State);
}

static async Task AdminEndpointsServeOperationalContractsAsync()
{
    var contentRoot = Path.Combine(FindRepoRoot(), "src", "Hedgehog.Admin.Api");
    await using var app = new WebApplicationFactory<AdminApiAssemblyMarker>()
        .WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
    using var client = app.CreateClient();

    var status = await client.GetFromJsonAsync<ClusterStatusDto>("/admin/v1/cluster/status")
        ?? throw new InvalidOperationException("cluster status endpoint returned no payload");
    Equal("hedgehog-local", status.ClusterId);
    True(status.Signals.Any(signal => signal.Name == "repair"), "status endpoint should expose repair signal");

    var canonicalStatus = await client.GetFromJsonAsync<ClusterStatusDto>("/admin/v1/status")
        ?? throw new InvalidOperationException("canonical status endpoint returned no payload");
    Equal(status.ClusterId, canonicalStatus.ClusterId);

    var compatibilityStatus = await client.GetFromJsonAsync<ClusterStatusDto>("/admin/status")
        ?? throw new InvalidOperationException("compatibility status endpoint returned no payload");
    Equal(status.ClusterId, compatibilityStatus.ClusterId);

    var nodes = await client.GetFromJsonAsync<IReadOnlyList<NodeDto>>("/admin/v1/nodes")
        ?? throw new InvalidOperationException("nodes endpoint returned no payload");
    True(nodes.Count > 0, "nodes endpoint should expose storage agents");

    var capacity = await client.GetFromJsonAsync<IReadOnlyList<CapacityScopeDto>>("/admin/v1/capacity")
        ?? throw new InvalidOperationException("capacity endpoint returned no payload");
    True(capacity.Count > 0, "capacity endpoint should expose capacity scopes");

    var objects = await client.GetFromJsonAsync<IReadOnlyList<ObjectVersionDto>>("/admin/v1/objects?state=under_replicated")
        ?? throw new InvalidOperationException("objects endpoint returned no payload");
    Equal(1, objects.Count);
    True(objects[0].ObjectId.StartsWith("obj_", StringComparison.Ordinal), "objects endpoint should expose opaque object ids");

    var objectDetail = await client.GetFromJsonAsync<ObjectVersionDto>($"/admin/v1/objects/{objects[0].ObjectId}")
        ?? throw new InvalidOperationException("object detail endpoint returned no payload");
    Equal(objects[0].ObjectId, objectDetail.ObjectId);

    var repairJobs = await client.GetFromJsonAsync<IReadOnlyList<RepairJobDto>>("/admin/v1/repair/jobs")
        ?? throw new InvalidOperationException("repair jobs endpoint returned no payload");
    True(repairJobs.Count > 0, "repair jobs endpoint should expose repair queue");

    var audit = await client.GetFromJsonAsync<IReadOnlyList<AuditEventDto>>("/admin/v1/audit")
        ?? throw new InvalidOperationException("audit endpoint returned no payload");
    True(audit.Count > 0, "audit endpoint should expose audit events");

    var recovery = await client.GetFromJsonAsync<RecoveryReadinessDto>("/admin/v1/recovery/gates")
        ?? throw new InvalidOperationException("recovery gates endpoint returned no payload");
    AssertCanonicalGates(recovery);
    Equal(false, recovery.Ready);
    True(recovery.Gates.Any(gate => gate.Status == RecoveryReadinessEvaluator.Failed), "admin recovery should fail closed while sample risks remain open");

    await AdminRecoveryEndpointUsesSharedEvaluatorContractAsync(contentRoot);

    var actionResponse = await client.PostAsJsonAsync(
        "/admin/v1/cluster/actions/pause-writes",
        new ActionRequestDto("admin-test", "endpoint test", "endpoint-request"));
    Equal(HttpStatusCode.OK, actionResponse.StatusCode);

    var action = await actionResponse.Content.ReadFromJsonAsync<ActionResultDto>()
        ?? throw new InvalidOperationException("cluster action endpoint returned no payload");
    Equal("accepted", action.Result);

    var badAction = await client.PostAsJsonAsync(
        "/admin/v1/nodes/node-a/actions/drain",
        new ActionRequestDto("", ""));
    Equal(HttpStatusCode.BadRequest, badAction.StatusCode);

    var quarantineResponse = await client.PostAsJsonAsync(
        "/admin/v1/nodes/node-a/actions/quarantine",
        new ActionRequestDto("admin-test", "endpoint quarantine", "endpoint-quarantine"));
    Equal(HttpStatusCode.OK, quarantineResponse.StatusCode);

    var revokeResponse = await client.PostAsJsonAsync(
        "/admin/v1/nodes/node-b/actions/revoke",
        new ActionRequestDto("admin-test", "endpoint revoke", "endpoint-revoke"));
    Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

    var retryResponse = await client.PostAsJsonAsync(
        "/admin/v1/repair/jobs/repair-30291/actions/retry",
        new ActionRequestDto("admin-test", "endpoint retry", "endpoint-retry"));
    Equal(HttpStatusCode.OK, retryResponse.StatusCode);

    var acknowledgeResponse = await client.PostAsJsonAsync(
        "/admin/v1/recovery/gates/gate-capacity-emergency/actions/acknowledge",
        new ActionRequestDto("admin-test", "endpoint acknowledge", "endpoint-acknowledge"));
    Equal(HttpStatusCode.OK, acknowledgeResponse.StatusCode);

    var paused = await client.GetFromJsonAsync<ClusterStatusDto>("/admin/v1/cluster/status")
        ?? throw new InvalidOperationException("paused cluster status endpoint returned no payload");
    Equal("paused", paused.WriteMode);
}

static async Task AdminRecoveryEndpointUsesSharedEvaluatorContractAsync(string contentRoot)
{
    await using var app = new WebApplicationFactory<AdminApiAssemblyMarker>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRecoveryReadinessProbe>(new StaticRecoveryReadinessProbe(AllPassedGates()));
            });
        });
    using var client = app.CreateClient();

    var recovery = await client.GetFromJsonAsync<RecoveryReadinessDto>("/admin/v1/recovery/gates")
        ?? throw new InvalidOperationException("override recovery gates endpoint returned no payload");
    AssertCanonicalGates(recovery);
    Equal(true, recovery.Ready);
    True(recovery.Gates.All(gate => gate.Status == RecoveryReadinessEvaluator.Passed), "admin endpoint should use the same evaluator contract as health and metrics");

    var payload = await client.GetStringAsync("/admin/v1/recovery/gates");
    False(payload.Contains("C:\\", StringComparison.Ordinal), "admin recovery should not expose Windows paths");
    False(payload.Contains(".sqlite", StringComparison.OrdinalIgnoreCase), "admin recovery should not expose database paths");
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

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
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
                RunningHeads: 1,
                TotalHeads: 1,
                RunningStorageNodes: 3,
                TotalStorageNodes: 3),
            gates));
}
