namespace Hedgehog.Metadata.Core;

public static class MetadataDecider
{
    public static MetadataResult<MetadataDecision> CreateWriteIntent(
        MetadataObjectState current,
        CreateWriteIntentCommand command)
    {
        var validation = ValidateObjectCommand(current, command.ObjectId)
            ?? RequireId(command.VersionId.Value, nameof(command.VersionId))
            ?? RequireId(command.WriterActorId.Value, nameof(command.WriterActorId))
            ?? RequireNonNegative(command.ContentLength, nameof(command.ContentLength))
            ?? RequireText(command.ContentHash, nameof(command.ContentHash))
            ?? RequirePositive(command.RequiredReplicaCount, nameof(command.RequiredReplicaCount))
            ?? RequirePositive(command.IntentTtl, nameof(command.IntentTtl));

        if (validation is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(validation);
        }

        if (FindVersion(current, command.VersionId) is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' already exists."));
        }

        var expiresAt = command.RequestedAt.Add(command.IntentTtl);
        var version = new ObjectVersionState(
            command.VersionId,
            ObjectVersionLifecycleState.Writing,
            command.WriterActorId,
            command.RequestedAt,
            command.ContentLength,
            command.ContentHash,
            command.RequiredReplicaCount,
            expiresAt,
            []);

        var next = current with
        {
            State = current.State is ObjectLifecycleState.Missing or ObjectLifecycleState.Deleted
                ? ObjectLifecycleState.Active
                : current.State,
            Versions = Append(current.Versions, version),
        };

        var evt = new WriteIntentCreated(
            command.ObjectId,
            command.VersionId,
            command.WriterActorId,
            command.RequestedAt,
            command.ContentLength,
            command.ContentHash,
            command.RequiredReplicaCount,
            expiresAt);

        return MetadataResult<MetadataDecision>.Ok(new MetadataDecision(next, [evt]));
    }

    public static MetadataResult<MetadataDecision> CompleteReplica(
        MetadataObjectState current,
        CompleteReplicaCommand command)
    {
        var validation = ValidateObjectCommand(current, command.ObjectId)
            ?? RequireId(command.VersionId.Value, nameof(command.VersionId))
            ?? RequireId(command.ReplicaId.Value, nameof(command.ReplicaId))
            ?? RequireId(command.NodeId.Value, nameof(command.NodeId))
            ?? RequireNonNegative(command.StoredBytes, nameof(command.StoredBytes))
            ?? RequireText(command.ContentHash, nameof(command.ContentHash));

        if (validation is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(validation);
        }

        var version = FindVersion(current, command.VersionId);
        if (version is null)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.NotFound($"Version '{command.VersionId}' was not found."));
        }

        if (version.State is ObjectVersionLifecycleState.DeleteMarker
            or ObjectVersionLifecycleState.GcEligible
            or ObjectVersionLifecycleState.GarbageCollected)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' cannot accept replicas in state '{version.State}'."));
        }

        if (version.ContentLength is { } expectedLength && expectedLength != command.StoredBytes)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Replica bytes '{command.StoredBytes}' do not match version length '{expectedLength}'."));
        }

        if (!string.Equals(version.ContentHash, command.ContentHash, StringComparison.Ordinal))
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict("Replica content hash does not match the write intent."));
        }

        if (version.Replicas.Any(replica => replica.ReplicaId == command.ReplicaId))
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Replica '{command.ReplicaId}' already exists."));
        }

        if (version.Replicas.Any(replica => replica.NodeId == command.NodeId && replica.State == ReplicaLifecycleState.Healthy))
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Node '{command.NodeId}' already has a healthy replica for version '{command.VersionId}'."));
        }

        var replicaState = new ReplicaPlacementState(
            command.ReplicaId,
            command.NodeId,
            ReplicaLifecycleState.Healthy,
            command.StoredBytes,
            command.ContentHash,
            command.CompletedAt);

        var updatedVersion = version with
        {
            Replicas = Append(version.Replicas, replicaState),
        };

        var next = ReplaceVersion(current, updatedVersion);
        var evt = new ReplicaCompleted(
            command.ObjectId,
            command.VersionId,
            command.ReplicaId,
            command.NodeId,
            command.CompletedAt,
            command.StoredBytes,
            command.ContentHash);

        return MetadataResult<MetadataDecision>.Ok(new MetadataDecision(next, [evt]));
    }

    public static MetadataResult<MetadataDecision> CommitVersion(
        MetadataObjectState current,
        CommitVersionCommand command)
    {
        var validation = ValidateObjectCommand(current, command.ObjectId)
            ?? RequireId(command.VersionId.Value, nameof(command.VersionId))
            ?? RequireId(command.CommitterActorId.Value, nameof(command.CommitterActorId));

        if (validation is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(validation);
        }

        var version = FindVersion(current, command.VersionId);
        if (version is null)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.NotFound($"Version '{command.VersionId}' was not found."));
        }

        if (version.State != ObjectVersionLifecycleState.Writing)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' cannot be committed from state '{version.State}'."));
        }

        var healthyReplicaCount = version.Replicas.Count(replica => replica.State == ReplicaLifecycleState.Healthy);
        if (healthyReplicaCount < version.RequiredReplicaCount)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict(
                    $"Version '{command.VersionId}' requires {version.RequiredReplicaCount} healthy replicas but has {healthyReplicaCount}."));
        }

        var updatedVersion = version with
        {
            State = ObjectVersionLifecycleState.Committed,
            WriteIntentExpiresAt = null,
        };

        var next = ReplaceVersion(current, updatedVersion) with
        {
            State = ObjectLifecycleState.Active,
            CurrentVersionId = command.VersionId,
        };

        var evt = new VersionCommitted(
            command.ObjectId,
            command.VersionId,
            command.CommitterActorId,
            command.CommittedAt);

        return MetadataResult<MetadataDecision>.Ok(new MetadataDecision(next, [evt]));
    }

    public static MetadataResult<MetadataDecision> CreateDeleteMarker(
        MetadataObjectState current,
        CreateDeleteMarkerCommand command)
    {
        var validation = ValidateObjectCommand(current, command.ObjectId)
            ?? RequireId(command.DeleteMarkerVersionId.Value, nameof(command.DeleteMarkerVersionId))
            ?? RequireId(command.ActorId.Value, nameof(command.ActorId));

        if (validation is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(validation);
        }

        if (FindVersion(current, command.DeleteMarkerVersionId) is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.DeleteMarkerVersionId}' already exists."));
        }

        var markerVersion = new ObjectVersionState(
            command.DeleteMarkerVersionId,
            ObjectVersionLifecycleState.DeleteMarker,
            command.ActorId,
            command.CreatedAt,
            null,
            null,
            0,
            null,
            []);

        var next = current with
        {
            State = ObjectLifecycleState.DeleteMarker,
            CurrentVersionId = command.DeleteMarkerVersionId,
            Versions = Append(current.Versions, markerVersion),
        };

        var evt = new DeleteMarkerCreated(
            command.ObjectId,
            command.DeleteMarkerVersionId,
            command.ActorId,
            command.CreatedAt);

        return MetadataResult<MetadataDecision>.Ok(new MetadataDecision(next, [evt]));
    }

    public static MetadataResult<MetadataDecision> AcquireRepairLease(
        MetadataObjectState current,
        AcquireRepairLeaseCommand command)
    {
        var validation = ValidateObjectCommand(current, command.ObjectId)
            ?? RequireId(command.VersionId.Value, nameof(command.VersionId))
            ?? RequireId(command.LeaseId.Value, nameof(command.LeaseId))
            ?? RequireId(command.HolderNodeId.Value, nameof(command.HolderNodeId))
            ?? RequireOptionalId(command.ReplicaId, nameof(command.ReplicaId))
            ?? RequirePositive(command.LeaseDuration, nameof(command.LeaseDuration));

        if (validation is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(validation);
        }

        if (current.RepairLeases.Any(lease => lease.LeaseId == command.LeaseId))
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Repair lease '{command.LeaseId}' already exists."));
        }

        var version = FindVersion(current, command.VersionId);
        if (version is null)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.NotFound($"Version '{command.VersionId}' was not found."));
        }

        if (version.State is ObjectVersionLifecycleState.Writing
            or ObjectVersionLifecycleState.DeleteMarker
            or ObjectVersionLifecycleState.GcEligible
            or ObjectVersionLifecycleState.GarbageCollected)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' cannot be repaired from state '{version.State}'."));
        }

        if (command.ReplicaId is { } replicaId
            && !version.Replicas.Any(replica => replica.ReplicaId == replicaId))
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.NotFound($"Replica '{replicaId}' was not found on version '{command.VersionId}'."));
        }

        var conflictingLease = current.RepairLeases.Any(lease =>
            lease.VersionId == command.VersionId
            && lease.ReplicaId == command.ReplicaId
            && lease.State == RepairLeaseLifecycleState.Issued
            && lease.ExpiresAt > command.LeasedAt);

        if (conflictingLease)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' already has an active repair lease."));
        }

        var expiresAt = command.LeasedAt.Add(command.LeaseDuration);
        var leaseState = new RepairLeaseState(
            command.LeaseId,
            command.VersionId,
            command.ReplicaId,
            command.HolderNodeId,
            RepairLeaseLifecycleState.Issued,
            command.LeasedAt,
            expiresAt);

        var nextVersion = version.State == ObjectVersionLifecycleState.Committed
            ? version with { State = ObjectVersionLifecycleState.UnderReplicated }
            : version;

        var next = ReplaceVersion(current, nextVersion) with
        {
            RepairLeases = Append(current.RepairLeases, leaseState),
        };

        var evt = new RepairLeaseAcquired(
            command.ObjectId,
            command.VersionId,
            command.LeaseId,
            command.HolderNodeId,
            command.LeasedAt,
            expiresAt,
            command.ReplicaId);

        return MetadataResult<MetadataDecision>.Ok(new MetadataDecision(next, [evt]));
    }

    public static MetadataResult<MetadataDecision> ExpireReservation(
        MetadataObjectState current,
        ExpireReservationCommand command)
    {
        var validation = ValidateObjectCommand(current, command.ObjectId)
            ?? RequireId(command.VersionId.Value, nameof(command.VersionId));

        if (validation is not null)
        {
            return MetadataResult<MetadataDecision>.Fail(validation);
        }

        var version = FindVersion(current, command.VersionId);
        if (version is null)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.NotFound($"Version '{command.VersionId}' was not found."));
        }

        if (version.State != ObjectVersionLifecycleState.Writing)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' cannot expire a reservation from state '{version.State}'."));
        }

        if (version.WriteIntentExpiresAt is not { } expiresAt)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' has no write intent expiry."));
        }

        if (command.ExpiredAt < expiresAt)
        {
            return MetadataResult<MetadataDecision>.Fail(
                MetadataError.Conflict($"Version '{command.VersionId}' cannot expire before '{expiresAt:O}'."));
        }

        var updatedVersion = version with
        {
            State = ObjectVersionLifecycleState.GcEligible,
            WriteIntentExpiresAt = null,
        };

        var next = ReplaceVersion(current, updatedVersion);
        var evt = new ReservationExpired(
            command.ObjectId,
            command.VersionId,
            command.ExpiredAt,
            expiresAt);

        return MetadataResult<MetadataDecision>.Ok(new MetadataDecision(next, [evt]));
    }

    private static MetadataError? ValidateObjectCommand(MetadataObjectState current, ObjectId objectId)
    {
        var validation = RequireId(objectId.Value, nameof(objectId));
        if (validation is not null)
        {
            return validation;
        }

        return current.ObjectId == objectId
            ? null
            : MetadataError.Conflict($"Command object '{objectId}' does not match state object '{current.ObjectId}'.");
    }

    private static ObjectVersionState? FindVersion(MetadataObjectState current, VersionId versionId) =>
        current.Versions.FirstOrDefault(version => version.VersionId == versionId);

    private static MetadataObjectState ReplaceVersion(MetadataObjectState current, ObjectVersionState updatedVersion) =>
        current with
        {
            Versions = current.Versions
                .Select(version => version.VersionId == updatedVersion.VersionId ? updatedVersion : version)
                .ToArray(),
        };

    private static IReadOnlyList<T> Append<T>(IReadOnlyList<T> items, T item) =>
        [.. items, item];

    private static MetadataError? RequireId(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? MetadataError.Validation($"{name} is required.")
            : null;

    private static MetadataError? RequireOptionalId(ReplicaId? value, string name) =>
        value is { } id
            ? RequireId(id.Value, name)
            : null;

    private static MetadataError? RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? MetadataError.Validation($"{name} is required.")
            : null;

    private static MetadataError? RequirePositive(int value, string name) =>
        value <= 0
            ? MetadataError.Validation($"{name} must be greater than zero.")
            : null;

    private static MetadataError? RequireNonNegative(long value, string name) =>
        value < 0
            ? MetadataError.Validation($"{name} must be zero or greater.")
            : null;

    private static MetadataError? RequirePositive(TimeSpan value, string name) =>
        value <= TimeSpan.Zero
            ? MetadataError.Validation($"{name} must be greater than zero.")
            : null;
}
