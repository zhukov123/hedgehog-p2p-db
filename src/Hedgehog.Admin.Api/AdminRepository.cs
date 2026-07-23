using Hedgehog.LocalRuntime.Api;

namespace Hedgehog.Admin.Api;

public sealed class AdminRepository
{
    public const string RecoveryGateActionRejectedReason = "recovery gates are projection-only; canonical evaluator only";

    private readonly Lock _gate = new();
    private readonly List<NodeDto> _nodes;
    private readonly List<CapacityScopeDto> _capacity;
    private readonly List<ObjectVersionDto> _objects;
    private readonly List<RepairJobDto> _repairJobs;
    private readonly List<AuditEventDto> _auditEvents;
    private string _writeMode = "normal";

    public AdminRepository()
    {
        var now = DateTimeOffset.UtcNow;
        _nodes =
        [
            new("node-a", "us-east-1", "active", now.AddSeconds(-18), 18, true, "none", Tib(12), Tib(10), Tib(6), Gib(500), 18203, 4, 31),
            new("node-b", "us-east-1", "draining", now.AddMinutes(-4), 240, false, "draining", Tib(12), Tib(10), Tib(9), Gib(700), 17042, 87, 218),
            new("node-c", "us-west-2", "active", now.AddSeconds(-22), 22, true, "none", Tib(16), Tib(13), Tib(7), Gib(620), 19012, 2, 49),
            new("node-d", "eu-central-1", "quarantined", now.AddMinutes(-16), 960, false, "quarantined", Tib(8), Tib(6), Tib(5), Gib(400), 0, 604, 0),
        ];
        _capacity =
        [
            new("global", "cluster", "pressure", Tib(48), Tib(39), Tib(27), Tib(3), Tib(9), Tib(4), false, now.AddSeconds(-12)),
            new("tenant", "tenant-alpha", "normal", Tib(18), Tib(14), Tib(8), Gib(900), Tib(5), Tib(1), false, now.AddSeconds(-42)),
            new("tenant", "tenant-beta", "critical", Tib(10), Tib(7), Tib(6), Gib(700), Gib(220), Gib(600), true, now.AddSeconds(-37)),
            new("node", "node-b", "critical", Tib(12), Tib(10), Tib(9), Gib(700), Gib(315), Gib(500), true, now.AddSeconds(-27)),
        ];
        _objects =
        [
            new("tenant-alpha", "dataset-images", "obj_8f4c92d1b3aa", "8f4c92d1b3aa", "ver-100817", true, "committed", false, 3, 3, 0, 2291, null, false, now.AddMinutes(-7)),
            new("tenant-alpha", "dataset-images", "obj_7a18c43ff912", "7a18c43ff912", "ver-100829", true, "under_replicated", false, 2, 3, 1, 2293, null, false, now.AddMinutes(-11)),
            new("tenant-beta", "dataset-ledger", "obj_19d23a090bc4", "19d23a090bc4", "ver-99142", true, "quarantined", false, 1, 3, 2, 2264, null, true, now.AddMinutes(-32)),
            new("tenant-beta", "dataset-ledger", "obj_3be6f07a4511", "3be6f07a4511", "ver-97002", false, "delete_marker", true, 0, 3, 0, 2018, 2095, false, now.AddHours(-4)),
        ];
        _repairJobs =
        [
            new("repair-30291", "replication", "critical", "pending", "healthy_replicas_below_minimum", "tenant-beta", "dataset-ledger", "19d23a090bc4", "ver-99142", Gib(28), now.AddMinutes(-34), 2, "node-d unavailable"),
            new("repair-30294", "verification", "high", "running", "suspect_replica", "tenant-alpha", "dataset-images", "7a18c43ff912", "ver-100829", Gib(3), now.AddMinutes(-14), 1, null),
            new("repair-30299", "placement", "normal", "pending", "capacity_rebalance", "tenant-alpha", "dataset-images", "8f4c92d1b3aa", "ver-100817", Gib(9), now.AddMinutes(-5), 0, null),
        ];
        _auditEvents =
        [
            Audit("system", "bootstrap", "cluster.status.sampled", "cluster", "cluster", "succeeded", "initial admin seed", now.AddMinutes(-20)),
            Audit("admin", "ops@example.invalid", "node.drain.started", "node", "node-b", "succeeded", "capacity pressure on node-b", now.AddMinutes(-12)),
            Audit("admin", "ops@example.invalid", "tenant.writes.frozen", "tenant", "tenant-beta", "succeeded", "effective free below reserve", now.AddMinutes(-8)),
        ];
    }

    public ClusterStatusDto GetClusterStatus()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var unavailable = _nodes.Count(node => node.State is "quarantined" or "revoked");
            var repairBytes = _repairJobs
                .Where(job => job.State is "pending" or "running")
                .Sum(job => job.BytesPending);
            var pressure = _capacity
                .OrderByDescending(scope => PressureRank(scope.Pressure))
                .First().Pressure;

            return new ClusterStatusDto(
                "hedgehog-local",
                now,
                "healthy",
                "healthy",
                _writeMode,
                pressure,
                unavailable,
                _repairJobs.Count(job => job.State is "pending" or "running"),
                repairBytes,
                72,
                [
                    new("head", "ok", "3 voters", "admin head accepts reads and recovery actions"),
                    new("metadata", "ok", "p95 18ms", "SQLite authority is responsive"),
                    new("outbox", "warning", "72s oldest", "capacity and repair delivery lag is elevated"),
                    new("repair", "critical", $"{_repairJobs.Count} active", "oldest critical repair is above the target age"),
                    new("capacity", pressure, FormatBytes(_capacity[0].EffectiveFreeBytes), "global effective free includes emergency reserve"),
                ]);
        }
    }

    public IReadOnlyList<NodeDto> GetNodes()
    {
        lock (_gate)
        {
            return _nodes.ToArray();
        }
    }

    public NodeDto? GetNode(string nodeId)
    {
        lock (_gate)
        {
            return _nodes.FirstOrDefault(node => node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<CapacityScopeDto> GetCapacity()
    {
        lock (_gate)
        {
            return _capacity.ToArray();
        }
    }

    public IReadOnlyList<ObjectVersionDto> GetObjects(string? tenantId, string? datasetId, string? state, string? q)
    {
        lock (_gate)
        {
            return _objects
                .Where(item => IsMatch(item.TenantId, tenantId))
                .Where(item => IsMatch(item.DatasetId, datasetId))
                .Where(item => IsMatch(item.State, state))
                .Where(item => string.IsNullOrWhiteSpace(q)
                    || item.ObjectId.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || item.ObjectLookupHashPrefix.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || item.VersionId.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public ObjectVersionDto? GetObject(string objectIdOrVersionId)
    {
        lock (_gate)
        {
            return _objects.FirstOrDefault(item =>
                item.ObjectId.Equals(objectIdOrVersionId, StringComparison.OrdinalIgnoreCase)
                || item.VersionId.Equals(objectIdOrVersionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<RepairJobDto> GetRepairQueue(string? state, string? priority)
    {
        lock (_gate)
        {
            return _repairJobs
                .Where(job => IsMatch(job.State, state))
                .Where(job => IsMatch(job.Priority, priority))
                .OrderBy(job => PriorityRank(job.Priority))
                .ThenBy(job => job.EnqueuedAt)
                .ToArray();
        }
    }

    public IReadOnlyList<AuditEventDto> GetAuditEvents(string? actorId, string? action, string? targetType, string? result)
    {
        lock (_gate)
        {
            return _auditEvents
                .Where(item => IsMatch(item.ActorId, actorId))
                .Where(item => IsMatch(item.Action, action))
                .Where(item => IsMatch(item.TargetType, targetType))
                .Where(item => IsMatch(item.Result, result))
                .OrderByDescending(item => item.OccurredAt)
                .Take(200)
                .ToArray();
        }
    }

    public IReadOnlyList<RecoveryGateDto> GetRecoveryGates()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return RecoveryReadinessEvaluator.CanonicalGateNames
                .Select(name => new RecoveryGateDto(
                    name,
                    name,
                    RecoveryReadinessEvaluator.Unknown,
                    "warning",
                    "canonical evaluator projection pending",
                    now,
                    0,
                    0,
                    ["runtime admission"],
                    ["projection-only"]))
                .ToArray();
        }
    }

    public ActionResultDto ApplyAction(string targetType, string targetId, string action, ActionRequestDto request)
    {
        lock (_gate)
        {
            var normalizedAction = action.ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var result = "accepted";
            var reason = request.Reason;

            switch (targetType, normalizedAction)
            {
                case ("cluster", "pause-writes"):
                    _writeMode = "paused";
                    break;
                case ("cluster", "resume-writes"):
                    _writeMode = "normal";
                    break;
                case ("cluster", "read-only"):
                    _writeMode = "read_only";
                    break;
                case ("node", "drain"):
                    UpdateNode(targetId, node => node with { AcceptingWrites = false, DrainState = "draining", State = node.State is "active" ? "draining" : node.State });
                    break;
                case ("node", "cancel-drain"):
                    UpdateNode(targetId, node => node with { AcceptingWrites = true, DrainState = "none", State = node.State is "draining" ? "active" : node.State });
                    break;
                case ("node", "quarantine"):
                    UpdateNode(targetId, node => node with { AcceptingWrites = false, DrainState = "quarantined", State = "quarantined" });
                    break;
                case ("node", "revoke"):
                    UpdateNode(targetId, node => node with { AcceptingWrites = false, DrainState = "revoked", State = "revoked" });
                    break;
                case ("object", "block-gc"):
                    UpdateObject(targetId, item => item with { GcBlocked = true, UpdatedAt = now });
                    break;
                case ("object", "unblock-gc"):
                    UpdateObject(targetId, item => item with { GcBlocked = false, UpdatedAt = now });
                    break;
                case ("object", "mark-suspect"):
                    UpdateObject(targetId, item => item with { State = "quarantined", SuspectReplicas = Math.Max(1, item.SuspectReplicas), UpdatedAt = now });
                    break;
                case ("repair-job", "boost-priority"):
                    UpdateRepairJob(targetId, job => job with { Priority = "critical" });
                    break;
                case ("repair-job", "retry"):
                    UpdateRepairJob(targetId, job => job with { State = "pending", AttemptCount = job.AttemptCount + 1, LastFailureReason = null });
                    break;
                case ("repair-job", "cancel-duplicate"):
                    UpdateRepairJob(targetId, job => job with { State = "canceled_superseded", LastFailureReason = "operator cancelled safe duplicate" });
                    break;
                case ("recovery-gate", "approve"):
                case ("recovery-gate", "acknowledge"):
                case ("recovery-gate", "close"):
                    result = "rejected";
                    reason = RecoveryGateActionRejectedReason;
                    break;
                case ("capacity", "freeze-writes"):
                    UpdateCapacity(targetId, scope => scope with { WritesFrozen = true, UpdatedAt = now });
                    break;
                case ("capacity", "resume-writes"):
                    UpdateCapacity(targetId, scope => scope with { WritesFrozen = false, UpdatedAt = now });
                    break;
                default:
                    result = "recorded";
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "action is defined for API compatibility but has no backing authority in this skeleton"
                        : reason;
                    break;
            }

            var audit = Audit("admin", request.ActorId, $"{targetType}.{normalizedAction}", targetType, targetId, result, reason, now, request.RequestId, request.IdempotencyKey);
            _auditEvents.Add(audit);
            return new ActionResultDto(targetType, targetId, normalizedAction, result, reason, audit.EventId, audit.OccurredAt);
        }
    }

    private void UpdateNode(string nodeId, Func<NodeDto, NodeDto> update)
    {
        var index = _nodes.FindIndex(node => node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _nodes[index] = update(_nodes[index]);
        }
    }

    private void UpdateCapacity(string scopeId, Func<CapacityScopeDto, CapacityScopeDto> update)
    {
        var index = _capacity.FindIndex(scope => scope.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _capacity[index] = update(_capacity[index]);
        }
    }

    private void UpdateObject(string objectIdOrVersionId, Func<ObjectVersionDto, ObjectVersionDto> update)
    {
        var index = _objects.FindIndex(item =>
            item.ObjectId.Equals(objectIdOrVersionId, StringComparison.OrdinalIgnoreCase)
            || item.VersionId.Equals(objectIdOrVersionId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _objects[index] = update(_objects[index]);
        }
    }

    private void UpdateRepairJob(string jobId, Func<RepairJobDto, RepairJobDto> update)
    {
        var index = _repairJobs.FindIndex(job => job.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _repairJobs[index] = update(_repairJobs[index]);
        }
    }

    private static AuditEventDto Audit(
        string actorType,
        string actorId,
        string action,
        string targetType,
        string targetId,
        string result,
        string reason,
        DateTimeOffset occurredAt,
        string? requestId = null,
        string? idempotencyKey = null)
    {
        return new AuditEventDto(
            $"audit-{Guid.NewGuid():N}"[..18],
            occurredAt,
            actorType,
            actorId,
            "authority-local-dev",
            requestId ?? $"req-{Guid.NewGuid():N}"[..16],
            idempotencyKey,
            action,
            targetType,
            targetId,
            result,
            reason,
            "head-a",
            new Dictionary<string, string>
            {
                ["source"] = "admin-skeleton",
                ["privacy"] = "object names are placeholders",
            });
    }

    private static bool IsMatch(string value, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter) || value.Equals(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static int PressureRank(string pressure)
    {
        return pressure switch
        {
            "emergency" => 4,
            "critical" => 3,
            "constrained" => 2,
            "normal" => 1,
            _ => 0,
        };
    }

    private static int PriorityRank(string priority)
    {
        return priority switch
        {
            "critical" => 0,
            "high" => 1,
            "normal" => 2,
            "low" => 3,
            _ => 4,
        };
    }

    private static long Gib(long value)
    {
        return value * 1024L * 1024L * 1024L;
    }

    private static long Tib(long value)
    {
        return value * 1024L * 1024L * 1024L * 1024L;
    }

    private static string FormatBytes(long bytes)
    {
        return bytes >= 1024L * 1024L * 1024L * 1024L
            ? $"{bytes / 1024L / 1024L / 1024L / 1024L} TiB"
            : $"{bytes / 1024L / 1024L / 1024L} GiB";
    }
}
