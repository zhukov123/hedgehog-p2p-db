using Hedgehog.Admin.Api;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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

    var objects = await client.GetFromJsonAsync<IReadOnlyList<ObjectVersionDto>>("/admin/v1/objects?state=under_replicated")
        ?? throw new InvalidOperationException("objects endpoint returned no payload");
    Equal(1, objects.Count);
    True(objects[0].ObjectId.StartsWith("obj_", StringComparison.Ordinal), "objects endpoint should expose opaque object ids");

    var actionResponse = await client.PostAsJsonAsync(
        "/admin/v1/cluster/actions/pause-writes",
        new ActionRequestDto("admin-test", "endpoint test", "endpoint-request"));
    Equal(HttpStatusCode.OK, actionResponse.StatusCode);

    var action = await actionResponse.Content.ReadFromJsonAsync<ActionResultDto>()
        ?? throw new InvalidOperationException("cluster action endpoint returned no payload");
    Equal("accepted", action.Result);

    var paused = await client.GetFromJsonAsync<ClusterStatusDto>("/admin/v1/cluster/status")
        ?? throw new InvalidOperationException("paused cluster status endpoint returned no payload");
    Equal("paused", paused.WriteMode);
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
