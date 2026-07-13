using Hedgehog.Metadata.Core;
using Hedgehog.Types;
using System.Text.RegularExpressions;

var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

LabelRegistryHasUniqueDomainWirePairs();
LabelsUseStableWireFormat();
FixtureManifestReferencesKnownLabels();
SqliteLabelSeedMatchesRegistry();
CreateWriteIntentCreatesWritingVersion(now);
CompleteReplicaRejectsHashMismatch(now);
CommitRequiresReplicaQuorum(now);
DeleteMarkerBecomesCurrentVersion(now);
RepairLeaseFencesConcurrentLease(now);

Console.WriteLine("Hedgehog.Metadata.Core.Tests passed.");

static void LabelRegistryHasUniqueDomainWirePairs()
{
    var labels = Labels.AllGroups.SelectMany(group => group).ToArray();
    Equal(11, Labels.AllGroups.Count);
    Equal(62, labels.Length);

    var duplicates = labels
        .GroupBy(label => $"{label.Domain}.{label.Wire}", StringComparer.Ordinal)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToArray();

    Equal("", string.Join(", ", duplicates));
}

static void LabelsUseStableWireFormat()
{
    var stableToken = new Regex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    foreach (var label in Labels.AllGroups.SelectMany(group => group))
    {
        True(stableToken.IsMatch(label.Domain), $"Label domain '{label.Domain}' must be lowercase snake_case.");
        True(stableToken.IsMatch(label.Wire), $"Label wire value '{label.Domain}.{label.Wire}' must be lowercase snake_case.");
        True(!string.IsNullOrWhiteSpace(label.Display), $"Label '{label.Domain}.{label.Wire}' must have display text.");
    }
}

static void FixtureManifestReferencesKnownLabels()
{
    var repoRoot = FindRepoRoot();
    var manifestPath = Path.Combine(repoRoot, "fixtures", "scaffold", "manifest.toml");
    var manifest = File.ReadAllText(manifestPath);
    var knownLabels = Labels.AllGroups.SelectMany(group => group)
        .Select(label => $"{label.Domain}.{label.Wire}")
        .ToHashSet(StringComparer.Ordinal);

    var referencedLabels = Regex.Matches(manifest, "\"(?<domain>[a-z_]+)\\.(?<wire>[a-z_]+)\"")
        .Select(match => match.Value.Trim('"'))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    True(referencedLabels.Length > 0, "Fixture manifest should reference canonical labels.");

    var unknownLabels = referencedLabels
        .Where(label => !knownLabels.Contains(label))
        .ToArray();

    Equal("", string.Join(", ", unknownLabels));
}

static void SqliteLabelSeedMatchesRegistry()
{
    var repoRoot = FindRepoRoot();
    var migrationPath = Path.Combine(
        repoRoot,
        "src",
        "Hedgehog.Metadata.Sqlite",
        "Migrations",
        "0006_capacity_reservations.sql");
    var migration = File.ReadAllText(migrationPath);

    var expected = Labels.AllGroups.SelectMany(group => group)
        .Select(label => $"{label.Domain}|{label.Wire}|{label.Display}")
        .Order(StringComparer.Ordinal)
        .ToArray();
    var seeded = Regex.Matches(
            migration,
            "\\('(?<domain>[a-z_]+)',\\s*'(?<wire>[a-z_]+)',\\s*'(?<display>[^']+)'\\)")
        .Select(match => $"{match.Groups["domain"].Value}|{match.Groups["wire"].Value}|{match.Groups["display"].Value}")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    Equal(string.Join("\n", expected), string.Join("\n", seeded));
}

static void CreateWriteIntentCreatesWritingVersion(DateTimeOffset now)
{
    var state = MetadataObjectState.Empty(new ObjectId("object-a"));
    var result = MetadataDecider.CreateWriteIntent(
        state,
        new CreateWriteIntentCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ActorId("actor-a"),
            now,
            12,
            "sha256:abc",
            2,
            TimeSpan.FromMinutes(15)));

    var decision = MustSucceed(result);
    Equal(ObjectLifecycleState.Active, decision.State.State);
    Equal(1, decision.State.Versions.Count);
    Equal(ObjectVersionLifecycleState.Writing, decision.State.Versions[0].State);
    Equal(now.AddMinutes(15), decision.State.Versions[0].WriteIntentExpiresAt);
    IsType<WriteIntentCreated>(decision.Events[0]);
}

static void CompleteReplicaRejectsHashMismatch(DateTimeOffset now)
{
    var state = WithWriteIntent(now, requiredReplicas: 1);
    var result = MetadataDecider.CompleteReplica(
        state,
        new CompleteReplicaCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ReplicaId("r1"),
            new NodeId("node-a"),
            now.AddSeconds(1),
            12,
            "sha256:wrong"));

    MustFail(result, "conflict");
}

static void CommitRequiresReplicaQuorum(DateTimeOffset now)
{
    var state = WithWriteIntent(now, requiredReplicas: 2);
    state = MustSucceed(MetadataDecider.CompleteReplica(
        state,
        new CompleteReplicaCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ReplicaId("r1"),
            new NodeId("node-a"),
            now.AddSeconds(1),
            12,
            "sha256:abc"))).State;

    MustFail(
        MetadataDecider.CommitVersion(
            state,
            new CommitVersionCommand(
                state.ObjectId,
                new VersionId("v1"),
                new ActorId("actor-a"),
                now.AddSeconds(2))),
        "conflict");

    state = MustSucceed(MetadataDecider.CompleteReplica(
        state,
        new CompleteReplicaCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ReplicaId("r2"),
            new NodeId("node-b"),
            now.AddSeconds(3),
            12,
            "sha256:abc"))).State;

    var committed = MustSucceed(MetadataDecider.CommitVersion(
        state,
        new CommitVersionCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ActorId("actor-a"),
            now.AddSeconds(4))));

    Equal(ObjectVersionLifecycleState.Committed, committed.State.Versions[0].State);
    Equal(new VersionId("v1"), committed.State.CurrentVersionId);
    IsType<VersionCommitted>(committed.Events[0]);
}

static void DeleteMarkerBecomesCurrentVersion(DateTimeOffset now)
{
    var state = MetadataObjectState.Empty(new ObjectId("object-a"));
    var decision = MustSucceed(MetadataDecider.CreateDeleteMarker(
        state,
        new CreateDeleteMarkerCommand(
            state.ObjectId,
            new VersionId("delete-v1"),
            new ActorId("actor-a"),
            now)));

    Equal(ObjectLifecycleState.DeleteMarker, decision.State.State);
    Equal(new VersionId("delete-v1"), decision.State.CurrentVersionId);
    Equal(ObjectVersionLifecycleState.DeleteMarker, decision.State.Versions[0].State);
    IsType<DeleteMarkerCreated>(decision.Events[0]);
}

static void RepairLeaseFencesConcurrentLease(DateTimeOffset now)
{
    var state = WithCommittedVersion(now);
    var leased = MustSucceed(MetadataDecider.AcquireRepairLease(
        state,
        new AcquireRepairLeaseCommand(
            state.ObjectId,
            new VersionId("v1"),
            new RepairLeaseId("lease-1"),
            new NodeId("repair-node-a"),
            now.AddMinutes(1),
            TimeSpan.FromMinutes(10),
            new ReplicaId("r1"))));

    Equal(ObjectVersionLifecycleState.UnderReplicated, leased.State.Versions[0].State);
    Equal(1, leased.State.RepairLeases.Count);
    IsType<RepairLeaseAcquired>(leased.Events[0]);

    MustFail(
        MetadataDecider.AcquireRepairLease(
            leased.State,
            new AcquireRepairLeaseCommand(
                state.ObjectId,
                new VersionId("v1"),
                new RepairLeaseId("lease-2"),
                new NodeId("repair-node-b"),
                now.AddMinutes(2),
                TimeSpan.FromMinutes(10),
                new ReplicaId("r1"))),
        "conflict");
}

static MetadataObjectState WithWriteIntent(DateTimeOffset now, int requiredReplicas)
{
    var state = MetadataObjectState.Empty(new ObjectId("object-a"));
    return MustSucceed(MetadataDecider.CreateWriteIntent(
        state,
        new CreateWriteIntentCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ActorId("actor-a"),
            now,
            12,
            "sha256:abc",
            requiredReplicas,
            TimeSpan.FromMinutes(15)))).State;
}

static MetadataObjectState WithCommittedVersion(DateTimeOffset now)
{
    var state = WithWriteIntent(now, requiredReplicas: 1);
    state = MustSucceed(MetadataDecider.CompleteReplica(
        state,
        new CompleteReplicaCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ReplicaId("r1"),
            new NodeId("node-a"),
            now.AddSeconds(1),
            12,
            "sha256:abc"))).State;

    return MustSucceed(MetadataDecider.CommitVersion(
        state,
        new CommitVersionCommand(
            state.ObjectId,
            new VersionId("v1"),
            new ActorId("actor-a"),
            now.AddSeconds(2)))).State;
}

static MetadataDecision MustSucceed(MetadataResult<MetadataDecision> result)
{
    if (!result.IsSuccess || result.Value is null)
    {
        throw new InvalidOperationException($"Expected success but failed with '{result.Error?.Code}: {result.Error?.Message}'.");
    }

    return result.Value;
}

static void MustFail(MetadataResult<MetadataDecision> result, string code)
{
    if (result.IsSuccess || result.Error is null)
    {
        throw new InvalidOperationException("Expected failure but command succeeded.");
    }

    Equal(code, result.Error.Code);
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

static void IsType<T>(object value)
{
    if (value is not T)
    {
        throw new InvalidOperationException($"Expected event type '{typeof(T).Name}' but got '{value.GetType().Name}'.");
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

    throw new InvalidOperationException("Could not find repository root.");
}
