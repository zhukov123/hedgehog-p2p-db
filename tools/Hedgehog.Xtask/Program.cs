using System.Text.Json;
using System.Text.RegularExpressions;
using Hedgehog.LocalRuntime;
using Hedgehog.Types;

var parsedCommand = CommandLine.Parse(args);
if (parsedCommand is null)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/Hedgehog.Xtask -- <validate-scaffold-contract|run-local-runtime-smoke|run-local-runtime-stress|run-local-restore-drill> [--json] [--allow-missing-scaffold] [--runtime-root <path>] [--tenant-count <n>] [--objects-per-tenant <n>] [--payload-bytes <n>]");
    return 2;
}

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
};

if (parsedCommand.Command == "run-local-runtime-smoke")
{
    var useDefaultRuntimeRoot = parsedCommand.RuntimeRoot is null;
    var runtimeRoot = parsedCommand.RuntimeRoot
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "local-runtime-smoke");
    if (Directory.Exists(runtimeRoot))
    {
        if (!useDefaultRuntimeRoot)
        {
            Console.Error.WriteLine($"custom runtime root already exists and will not be deleted automatically: {runtimeRoot}");
            return 1;
        }

        Directory.Delete(runtimeRoot, recursive: true);
    }

    var result = await LocalRuntimeSmoke.RunAsync(LocalClusterOptions.CreateDefault(runtimeRoot));

    if (parsedCommand.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }
    else
    {
        Console.WriteLine("local runtime smoke passed");
        Console.WriteLine($"runtime_root={result.RuntimeRoot}");
        Console.WriteLine($"heads={result.HeadCount}");
        Console.WriteLine($"storage_nodes={result.StorageNodeCount}");
        Console.WriteLine($"published_objects={result.PublishedObjects}");
        Console.WriteLine($"verified_retrievals={result.VerifiedRetrievals}");
        Console.WriteLine($"delete_verified={result.DeleteVerified}");
        Console.WriteLine($"metadata_object_rows={result.MetadataObjectRows}");
        Console.WriteLine($"healthy_replica_rows={result.HealthyReplicaRows}");
    }

    return 0;
}

if (parsedCommand.Command == "run-local-runtime-stress")
{
    var useDefaultRuntimeRoot = parsedCommand.RuntimeRoot is null;
    var runtimeRoot = parsedCommand.RuntimeRoot
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "local-runtime-stress");
    if (Directory.Exists(runtimeRoot))
    {
        if (!useDefaultRuntimeRoot)
        {
            Console.Error.WriteLine($"custom runtime root already exists and will not be deleted automatically: {runtimeRoot}");
            return 1;
        }

        Directory.Delete(runtimeRoot, recursive: true);
    }

    var result = await LocalRuntimeStress.RunAsync(
        new LocalRuntimeStressOptions(
            runtimeRoot,
            parsedCommand.TenantCount ?? 3,
            parsedCommand.ObjectsPerTenant ?? 12,
            parsedCommand.PayloadBytes ?? 512));

    if (parsedCommand.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }
    else
    {
        Console.WriteLine("local runtime stress passed");
        Console.WriteLine($"runtime_root={result.RuntimeRoot}");
        Console.WriteLine($"tenants={result.TenantCount}");
        Console.WriteLine($"heads={result.HeadCount}");
        Console.WriteLine($"storage_nodes={result.StorageNodeCount}");
        Console.WriteLine($"objects_written={result.ObjectsWritten}");
        Console.WriteLine($"reads_verified={result.ReadsVerified}");
        Console.WriteLine($"deletes_verified={result.DeletesVerified}");
        Console.WriteLine($"metadata_object_rows={result.MetadataObjectRows}");
        Console.WriteLine($"metadata_version_rows={result.MetadataVersionRows}");
        Console.WriteLine($"healthy_replica_rows={result.HealthyReplicaRows}");
        Console.WriteLine($"delete_marker_rows={result.DeleteMarkerRows}");
    }

    return 0;
}

if (parsedCommand.Command == "run-local-restore-drill")
{
    var useDefaultRuntimeRoot = parsedCommand.RuntimeRoot is null;
    var runtimeRoot = parsedCommand.RuntimeRoot
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "local-restore-drill");
    if (Directory.Exists(runtimeRoot))
    {
        if (!useDefaultRuntimeRoot)
        {
            Console.Error.WriteLine($"custom runtime root already exists and will not be deleted automatically: {runtimeRoot}");
            return 1;
        }

        Directory.Delete(runtimeRoot, recursive: true);
    }

    var result = await LocalRuntimeRestoreDrill.RunAsync(LocalClusterOptions.CreateDefault(runtimeRoot));

    if (parsedCommand.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }
    else
    {
        Console.WriteLine("local restore drill passed");
        Console.WriteLine($"runtime_root={result.RuntimeRoot}");
        Console.WriteLine($"heads_after_restore={result.HeadCountAfterRestore}");
        Console.WriteLine($"storage_nodes_after_restore={result.StorageNodeCountAfterRestore}");
        Console.WriteLine($"objects_recovered={result.ObjectsRecovered}");
        Console.WriteLine($"reads_verified_after_restore={result.ReadsVerifiedAfterRestore}");
        Console.WriteLine($"delete_marker_recovered={result.DeleteMarkerRecovered}");
        Console.WriteLine($"metadata_object_rows={result.MetadataObjectRows}");
        Console.WriteLine($"metadata_version_rows={result.MetadataVersionRows}");
        Console.WriteLine($"healthy_replica_rows={result.HealthyReplicaRows}");
        Console.WriteLine($"healthy_replicas_verified={result.HealthyReplicasVerified}");
        Console.WriteLine($"committed_reservation_rows={result.CommittedReservationRows}");
        Console.WriteLine($"pending_outbox_rows={result.PendingOutboxRows}");
        Console.WriteLine($"pending_repair_job_rows={result.PendingRepairJobRows}");
        Console.WriteLine($"audit_rows={result.AuditRows}");
        Console.WriteLine($"backup_manifest_entries={result.BackupManifestEntries}");
        Console.WriteLine($"missing_replica_blob_rejected={result.MissingReplicaBlobRejected}");
        Console.WriteLine($"corrupt_replica_blob_rejected={result.CorruptReplicaBlobRejected}");
    }

    return 0;
}

var validator = new ScaffoldContractValidator(Directory.GetCurrentDirectory(), parsedCommand.AllowMissingScaffold);
var failures = validator.Validate();

if (parsedCommand.Json)
{
    var payload = new
    {
        passed = failures.Count == 0,
        failures = failures.Select(failure => new
        {
            check = failure.CheckId,
            message = failure.Message,
            path = failure.Path,
        }),
    };

    Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
}
else if (failures.Count == 0)
{
    Console.WriteLine("scaffold contract validation passed");
}
else
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure.ToDisplayString());
    }
}

return failures.Count == 0 ? 0 : 1;

internal sealed record CommandLine(
    string Command,
    bool Json,
    bool AllowMissingScaffold,
    string? RuntimeRoot,
    int? TenantCount,
    int? ObjectsPerTenant,
    int? PayloadBytes)
{
    public static CommandLine? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("validate-scaffold-contract" or "run-local-runtime-smoke" or "run-local-runtime-stress" or "run-local-restore-drill"))
        {
            return null;
        }

        var command = args[0];
        var json = false;
        var allowMissingScaffold = false;
        string? runtimeRoot = null;
        int? tenantCount = null;
        int? objectsPerTenant = null;
        int? payloadBytes = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--allow-missing-scaffold":
                    allowMissingScaffold = true;
                    break;
                case "--runtime-root":
                    if (i + 1 >= args.Length)
                    {
                        return null;
                    }

                    runtimeRoot = args[++i];
                    break;
                case "--tenant-count":
                    if (i + 1 >= args.Length || !TryPositiveInt(args[++i], out tenantCount))
                    {
                        return null;
                    }

                    break;
                case "--objects-per-tenant":
                    if (i + 1 >= args.Length || !TryPositiveInt(args[++i], out objectsPerTenant))
                    {
                        return null;
                    }

                    break;
                case "--payload-bytes":
                    if (i + 1 >= args.Length || !TryPositiveInt(args[++i], out payloadBytes))
                    {
                        return null;
                    }

                    break;
                default:
                    return null;
            }
        }

        return new CommandLine(command, json, allowMissingScaffold, runtimeRoot, tenantCount, objectsPerTenant, payloadBytes);
    }

    private static bool TryPositiveInt(string value, out int? parsed)
    {
        if (int.TryParse(value, out var number) && number > 0)
        {
            parsed = number;
            return true;
        }

        parsed = null;
        return false;
    }
}

internal sealed class ScaffoldContractValidator(string root, bool allowMissingScaffold)
{
    private const string ManifestPath = "fixtures/scaffold/manifest.toml";

    private static readonly string[] RequiredDocs =
    [
        "README.md",
        "p2p-object-store-guide.md",
        "p2p-object-store-key-model.md",
        "p2p-object-store-sqlite-schema-plan.md",
        "p2p-nosql-implementation-contract.md",
        "p2p-nosql-scaffold-contract.md",
        "docs/project-layout-v1.md",
    ];

    private static readonly (string Path, string Phrase)[] RequiredPhrases =
    [
        ("p2p-object-store-key-model.md", "object_lookup_hash = HMAC-SHA256(dataset_lookup_key, normalized_object_name)"),
        ("p2p-object-store-sqlite-schema-plan.md", "object_lookup_hash blob not null"),
        ("p2p-nosql-implementation-contract.md", "Use `Microsoft.Data.Sqlite` with SQLite for v1-alpha."),
        ("p2p-nosql-scaffold-contract-part-01.md", "Hedgehog.Metadata.Sqlite"),
        ("docs/project-layout-v1.md", "V1 projects are allowed only under `src/`, `tests/`, and `tools/`."),
    ];

    private static readonly string[] QuarantineScanFiles =
    [
        "p2p-object-store-sqlite-schema-plan.md",
        "p2p-nosql-replication-repair-state-machine.md",
    ];

    private static readonly string[] QuarantinedTokens =
    [
        "COMMIT_PENDING",
        "AVAILABLE",
        "TRANSFER_ASSIGNED",
        "UPLOADING",
        "UNDER_REPLICATED",
        "REPAIRING",
    ];

    private static readonly RequiredFixture[] RequiredFixtures =
    [
        new("head_crash_after_one_fsynced_replica", "head crash after one fsynced replica"),
        new("late_ack_after_reservation_expiry", "late ACK after reservation expiry"),
        new("late_ack_after_delete_epoch_bump", "late ACK after delete epoch bump"),
        new("revoked_node_final_result", "revoked-node final result"),
        new("interrupted_repair_conversion", "interrupted repair conversion"),
        new("metadata_pause_and_recover", "metadata pause and recover"),
        new("restore_with_outbox_lag", "restore with outbox lag"),
        new("temp_disk_full_during_upload", "temp disk full during upload"),
        new("repair_reserve_exhausted", "repair reserve exhausted"),
        new("orphan_cleanup_under_critical_capacity", "orphan cleanup under critical capacity"),
        new("agent_local_sqlite_manifest_replay_after_crash", "agent-local SQLite manifest replay after crash"),
        new("task_cancellation_after_fsync_before_final_result_publication", "task cancellation after fsync and before final result publication"),
        new("lock_held_across_await_check", "lock-held-across-await check in service code"),
        new("bounded_queue_overflow_under_repair_pressure", "bounded queue overflow under repair pressure"),
        new("test_controlled_clock_skew_for_leases_and_envelope_expiry", "test-controlled clock skew for leases and envelope expiry"),
    ];

    private static readonly RequiredTask[] RequiredTasks =
    [
        new("apply_sqlite_migrations", "Apply SQLite migrations"),
        new("seed_local_authority", "Seed local authority"),
        new("start_head_runtime", "Start head runtime"),
        new("start_storage_agents", "Start storage agents"),
        new("start_admin_api", "Start admin API"),
        new("start_admin_ui", "Start admin UI"),
        new("verify_readiness_gates", "Verify readiness gates"),
    ];

    private static readonly string[] RequiredMigrations =
    [
        "src/Hedgehog.Metadata.Sqlite/Migrations/0001_security_roots_tenants_datasets.sql",
        "src/Hedgehog.Metadata.Sqlite/Migrations/0002_nodes_keys_capacity.sql",
        "src/Hedgehog.Metadata.Sqlite/Migrations/0003_objects_versions_replicas.sql",
        "src/Hedgehog.Metadata.Sqlite/Migrations/0004_leases_repair_jobs_tombstones.sql",
        "src/Hedgehog.Metadata.Sqlite/Migrations/0005_idempotency_outbox_audit.sql",
        "src/Hedgehog.Metadata.Sqlite/Migrations/0006_capacity_reservations.sql",
    ];

    private static readonly RequiredSurface[] AdminSurfaces =
    [
        new(
            "admin-api",
            [
                "src/Hedgehog.Admin.Api/Hedgehog.Admin.Api.csproj",
                "src/Hedgehog.Admin-api/Hedgehog.Admin-api.csproj",
                "admin/admin-api",
            ]),
        new(
            "admin-ui",
            [
                "src/Hedgehog.Admin.Ui/Hedgehog.Admin.Ui.csproj",
                "src/Hedgehog.Admin-ui/Hedgehog.Admin-ui.csproj",
                "admin/admin-ui",
            ]),
    ];

    private static readonly RequiredProject[] ProjectLayout =
    [
        new("Hedgehog.Types", "src/Hedgehog.Types/Hedgehog.Types.csproj", true),
        new("Hedgehog.Crypto", "src/Hedgehog.Crypto/Hedgehog.Crypto.csproj", true),
        new("Hedgehog.Metadata.Core", "src/Hedgehog.Metadata.Core/Hedgehog.Metadata.Core.csproj", true),
        new("Hedgehog.Metadata.Sqlite", "src/Hedgehog.Metadata.Sqlite/Hedgehog.Metadata.Sqlite.csproj", true),
        new("Hedgehog.Admin.Api", "src/Hedgehog.Admin.Api/Hedgehog.Admin.Api.csproj", true),
        new("Hedgehog.Admin.Ui", "src/Hedgehog.Admin.Ui/Hedgehog.Admin.Ui.csproj", true),
        new("Hedgehog.Head", "src/Hedgehog.Head/Hedgehog.Head.csproj", true),
        new("Hedgehog.Agent.Core", "src/Hedgehog.Agent.Core/Hedgehog.Agent.Core.csproj", true),
        new("Hedgehog.Agent.Store", "src/Hedgehog.Agent.Store/Hedgehog.Agent.Store.csproj", true),
        new("Hedgehog.StorageAgent", "src/Hedgehog.StorageAgent/Hedgehog.StorageAgent.csproj", true),
        new("Hedgehog.Repair", "src/Hedgehog.Repair/Hedgehog.Repair.csproj", false),
        new("Hedgehog.Client", "src/Hedgehog.Client/Hedgehog.Client.csproj", true),
        new("Hedgehog.LocalRuntime", "src/Hedgehog.LocalRuntime/Hedgehog.LocalRuntime.csproj", true),
        new("Hedgehog.LocalRuntime.Api", "src/Hedgehog.LocalRuntime.Api/Hedgehog.LocalRuntime.Api.csproj", true),
        new("Hedgehog.Metadata.Core.Tests", "tests/Hedgehog.Metadata.Core.Tests/Hedgehog.Metadata.Core.Tests.csproj", true),
        new("Hedgehog.Metadata.Sqlite.Tests", "tests/Hedgehog.Metadata.Sqlite.Tests/Hedgehog.Metadata.Sqlite.Tests.csproj", true),
        new("Hedgehog.Admin.Api.Tests", "tests/Hedgehog.Admin.Api.Tests/Hedgehog.Admin.Api.Tests.csproj", true),
        new("Hedgehog.Head.Tests", "tests/Hedgehog.Head.Tests/Hedgehog.Head.Tests.csproj", false),
        new("Hedgehog.StorageAgent.Tests", "tests/Hedgehog.StorageAgent.Tests/Hedgehog.StorageAgent.Tests.csproj", false),
        new("Hedgehog.Repair.Tests", "tests/Hedgehog.Repair.Tests/Hedgehog.Repair.Tests.csproj", false),
        new("Hedgehog.Client.Tests", "tests/Hedgehog.Client.Tests/Hedgehog.Client.Tests.csproj", false),
        new("Hedgehog.LocalRuntime.Tests", "tests/Hedgehog.LocalRuntime.Tests/Hedgehog.LocalRuntime.Tests.csproj", true),
        new("Hedgehog.Xtask", "tools/Hedgehog.Xtask/Hedgehog.Xtask.csproj", true),
    ];

    private static readonly Regex SlugPattern = new("^[a-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<ValidationFailure> Validate()
    {
        var failures = new List<ValidationFailure>();

        ValidateDocs(failures);
        ValidateLabels(failures);
        ValidateQuarantine(failures);

        var manifest = ValidateManifestShape(failures);
        if (manifest is not null)
        {
            ValidateRequiredTasks(manifest, failures);
            ValidateRequiredFixtures(manifest, failures);
            ValidateManifestLabels(manifest, failures);
        }

        ValidateAdminSurfaces(failures);
        ValidateMigrations(failures);
        ValidateProjectLayout(failures);

        return failures
            .OrderBy(failure => failure.CheckId, StringComparer.Ordinal)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private void ValidateDocs(List<ValidationFailure> failures)
    {
        foreach (var path in RequiredDocs)
        {
            if (!File.Exists(FullPath(path)))
            {
                failures.Add(new("docs.required", $"missing required doc: {path}", path));
            }
        }

        foreach (var (path, phrase) in RequiredPhrases)
        {
            var fullPath = FullPath(path);
            if (!File.Exists(fullPath))
            {
                failures.Add(new("docs.required_phrase", $"missing required phrase source: {path}", path));
                continue;
            }

            var text = File.ReadAllText(fullPath);
            if (!text.Contains(phrase, StringComparison.Ordinal))
            {
                failures.Add(new("docs.required_phrase", $"{path} missing required phrase: {phrase}", path));
            }
        }
    }

    private static void ValidateLabels(List<ValidationFailure> failures)
    {
        foreach (var group in Labels.AllGroups)
        {
            foreach (var label in group)
            {
                if (!IsLowercaseStable(label.Wire))
                {
                    failures.Add(new("labels.canonical", $"{label.Domain} label is not lowercase wire format: {label.Wire}"));
                }
            }
        }
    }

    private void ValidateQuarantine(List<ValidationFailure> failures)
    {
        foreach (var path in QuarantineScanFiles.Concat(EnumerateOptionalScanFiles("fixtures", "*.toml")))
        {
            var fullPath = FullPath(path);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var text = File.ReadAllText(fullPath);
            foreach (var token in QuarantinedTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    failures.Add(new("labels.uppercase_quarantine", $"{path} contains quarantined token: {token}", path));
                }
            }
        }
    }

    private ManifestDocument? ValidateManifestShape(List<ValidationFailure> failures)
    {
        var fullPath = FullPath(ManifestPath);
        if (!File.Exists(fullPath))
        {
            if (allowMissingScaffold)
            {
                return null;
            }

            failures.Add(new("fixtures.manifest", $"missing fixture manifest: {ManifestPath}", ManifestPath));
            return null;
        }

        var manifest = ManifestParser.Parse(ManifestPath, File.ReadAllLines(fullPath));
        foreach (var error in manifest.Errors)
        {
            failures.Add(new("fixtures.manifest", error, ManifestPath));
        }

        if (manifest.Version != 1)
        {
            failures.Add(new("fixtures.manifest", $"{ManifestPath} must set version = 1", ManifestPath));
        }

        ValidateEntryShape("task", manifest.Tasks, failures);
        ValidateEntryShape("fixture", manifest.Fixtures, failures);

        return manifest;
    }

    private static void ValidateEntryShape(string section, IReadOnlyList<ManifestEntry> entries, List<ValidationFailure> failures)
    {
        foreach (var entry in entries)
        {
            var slug = entry.GetString("slug");
            if (string.IsNullOrWhiteSpace(slug))
            {
                failures.Add(new("fixtures.manifest", $"{section} at line {entry.Line} is missing slug", ManifestPath));
            }
            else if (!SlugPattern.IsMatch(slug))
            {
                failures.Add(new("fixtures.manifest", $"{section} \"{slug}\" has non-stable slug; use lowercase letters, digits, and underscores only", ManifestPath));
            }

            RequireString(section, entry, "name", failures);
            RequireString(section, entry, "owner", failures);

            if (section == "fixture")
            {
                RequireString(section, entry, "scenario", failures);
                if (entry.GetBoolean("beta_blocker") != true)
                {
                    failures.Add(new("fixtures.manifest", $"fixture \"{slug ?? $"line {entry.Line}"}\" must set beta_blocker = true", ManifestPath));
                }
            }
            else if (section == "task")
            {
                RequireString(section, entry, "status", failures);
                if (entry.GetBoolean("v1_required") != true)
                {
                    failures.Add(new("runtime.tasks", $"task \"{slug ?? $"line {entry.Line}"}\" must set v1_required = true", ManifestPath));
                }
            }
        }
    }

    private static void RequireString(string section, ManifestEntry entry, string key, List<ValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(entry.GetString(key)))
        {
            failures.Add(new("fixtures.manifest", $"{section} \"{entry.GetString("slug") ?? $"line {entry.Line}"}\" is missing {key}", ManifestPath));
        }
    }

    private static void ValidateRequiredTasks(ManifestDocument manifest, List<ValidationFailure> failures)
    {
        var bySlug = IndexEntriesBySlug("task", manifest.Tasks, failures);

        foreach (var task in RequiredTasks)
        {
            if (!bySlug.TryGetValue(task.Slug, out var entry))
            {
                failures.Add(new("runtime.tasks", $"missing local runtime task \"{task.Name}\" (slug {task.Slug})", ManifestPath));
                continue;
            }

            if (entry.GetBoolean("v1_required") != true)
            {
                failures.Add(new("runtime.tasks", $"local runtime task \"{task.Slug}\" must be v1_required", ManifestPath));
            }
        }
    }

    private static void ValidateRequiredFixtures(ManifestDocument manifest, List<ValidationFailure> failures)
    {
        var bySlug = IndexEntriesBySlug("fixture", manifest.Fixtures, failures);

        foreach (var fixture in RequiredFixtures)
        {
            if (!bySlug.TryGetValue(fixture.Slug, out var entry))
            {
                failures.Add(new("fixtures.present", $"missing beta fixture \"{fixture.Name}\" (slug {fixture.Slug})", ManifestPath));
                continue;
            }

            if (!string.Equals(entry.GetString("scenario"), fixture.Name, StringComparison.Ordinal))
            {
                failures.Add(new("fixtures.present", $"fixture \"{fixture.Slug}\" must use scenario \"{fixture.Name}\"", ManifestPath));
            }

            if (entry.GetBoolean("beta_blocker") != true)
            {
                failures.Add(new("fixtures.present", $"fixture \"{fixture.Slug}\" must be marked beta_blocker", ManifestPath));
            }
        }
    }

    private static Dictionary<string, ManifestEntry> IndexEntriesBySlug(
        string section,
        IReadOnlyList<ManifestEntry> entries,
        List<ValidationFailure> failures)
    {
        var bySlug = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var slug = entry.GetString("slug");
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            if (!bySlug.TryAdd(slug, entry))
            {
                failures.Add(new("fixtures.manifest", $"duplicate {section} slug \"{slug}\"", ManifestPath));
            }
        }

        return bySlug;
    }

    private static void ValidateManifestLabels(ManifestDocument manifest, List<ValidationFailure> failures)
    {
        var labelsByDomain = Labels.AllGroups
            .SelectMany(group => group)
            .GroupBy(label => label.Domain, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(label => label.Wire).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        foreach (var entry in manifest.Fixtures)
        {
            var slug = entry.GetString("slug") ?? $"line {entry.Line}";
            foreach (var labelRef in entry.GetStringArray("labels"))
            {
                var dotIndex = labelRef.IndexOf('.', StringComparison.Ordinal);
                if (dotIndex <= 0 || dotIndex == labelRef.Length - 1)
                {
                    failures.Add(new("labels.canonical", $"fixture \"{slug}\" label \"{labelRef}\" must use domain.wire format", ManifestPath));
                    continue;
                }

                var domain = labelRef[..dotIndex];
                var wire = labelRef[(dotIndex + 1)..];
                if (!labelsByDomain.TryGetValue(domain, out var validLabels) || !validLabels.Contains(wire))
                {
                    failures.Add(new("labels.canonical", $"fixture \"{slug}\" references unknown label \"{labelRef}\"", ManifestPath));
                }
            }
        }
    }

    private void ValidateAdminSurfaces(List<ValidationFailure> failures)
    {
        foreach (var surface in AdminSurfaces)
        {
            if (surface.PathCandidates.Any(path => File.Exists(FullPath(path)) || Directory.Exists(FullPath(path))))
            {
                continue;
            }

            failures.Add(new("admin.interface", $"missing {surface.Name} surface; expected one of: {string.Join(", ", surface.PathCandidates)}"));
        }
    }

    private void ValidateMigrations(List<ValidationFailure> failures)
    {
        foreach (var path in RequiredMigrations)
        {
            if (!File.Exists(FullPath(path)))
            {
                failures.Add(new("migrations.present", $"missing required SQLite migration: {path}", path));
            }
        }
    }

    private void ValidateProjectLayout(List<ValidationFailure> failures)
    {
        var contractPath = "docs/project-layout-v1.md";
        var contractFullPath = FullPath(contractPath);
        var contractText = File.Exists(contractFullPath) ? File.ReadAllText(contractFullPath) : string.Empty;
        var knownProjectPaths = ProjectLayout.Select(project => project.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var project in ProjectLayout)
        {
            if (!contractText.Contains(project.Name, StringComparison.Ordinal)
                || !contractText.Contains(project.Path, StringComparison.Ordinal))
            {
                failures.Add(new("projects.layout", $"layout contract must declare {project.Name} at {project.Path}", contractPath));
            }

            if (project.MustExist && !File.Exists(FullPath(project.Path)))
            {
                failures.Add(new("projects.layout", $"required project is missing: {project.Path}", project.Path));
            }
        }

        foreach (var projectFile in EnumerateProjectFiles())
        {
            if (!knownProjectPaths.Contains(projectFile))
            {
                failures.Add(new("projects.layout", $"project file is outside the v1 layout contract: {projectFile}", projectFile));
            }
        }
    }

    private IEnumerable<string> EnumerateProjectFiles()
    {
        foreach (var directory in new[] { "src", "tests", "tools" })
        {
            var fullDirectory = FullPath(directory);
            if (!Directory.Exists(fullDirectory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(fullDirectory, "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                yield return Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
            }
        }
    }

    private IEnumerable<string> EnumerateOptionalScanFiles(string directory, string pattern)
    {
        var fullDirectory = FullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(fullDirectory, pattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            yield return Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private string FullPath(string path) => Path.Combine(root, path);

    private static bool IsLowercaseStable(string value) =>
        value.Length > 0 && value.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_');
}

internal static class ManifestParser
{
    public static ManifestDocument Parse(string path, IReadOnlyList<string> lines)
    {
        int? version = null;
        var tasks = new List<ManifestEntry>();
        var fixtures = new List<ManifestEntry>();
        var errors = new List<string>();
        ManifestEntry? currentEntry = null;
        string? currentSection = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var lineNumber = i + 1;
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                currentSection = line[2..^2].Trim();
                currentEntry = new ManifestEntry(lineNumber);

                switch (currentSection)
                {
                    case "task":
                        tasks.Add(currentEntry);
                        break;
                    case "fixture":
                        fixtures.Add(currentEntry);
                        break;
                    default:
                        errors.Add($"{path}:{lineNumber}: unknown section [[{currentSection}]]");
                        currentEntry = null;
                        break;
                }

                continue;
            }

            var equalsIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex <= 0)
            {
                errors.Add($"{path}:{lineNumber}: expected key = value");
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var rawValue = line[(equalsIndex + 1)..].Trim();
            if (!IsKey(key))
            {
                errors.Add($"{path}:{lineNumber}: invalid key \"{key}\"");
                continue;
            }

            var value = ParseValue(path, lineNumber, rawValue, errors);
            if (currentEntry is null)
            {
                if (!string.Equals(key, "version", StringComparison.Ordinal))
                {
                    errors.Add($"{path}:{lineNumber}: top-level key \"{key}\" is not supported");
                    continue;
                }

                if (value is int parsedVersion)
                {
                    version = parsedVersion;
                }
                else
                {
                    errors.Add($"{path}:{lineNumber}: version must be an integer");
                }

                continue;
            }

            if (currentSection is not ("task" or "fixture"))
            {
                errors.Add($"{path}:{lineNumber}: key outside supported section");
                continue;
            }

            currentEntry.Set(key, value);
        }

        return new ManifestDocument(version, tasks, fixtures, errors);
    }

    private static object? ParseValue(string path, int lineNumber, string rawValue, List<string> errors)
    {
        if (rawValue.StartsWith("\"", StringComparison.Ordinal) && rawValue.EndsWith("\"", StringComparison.Ordinal) && rawValue.Length >= 2)
        {
            return rawValue[1..^1];
        }

        if (rawValue is "true" or "false")
        {
            return string.Equals(rawValue, "true", StringComparison.Ordinal);
        }

        if (rawValue.StartsWith("[", StringComparison.Ordinal) && rawValue.EndsWith("]", StringComparison.Ordinal))
        {
            return ParseStringArray(path, lineNumber, rawValue, errors);
        }

        if (int.TryParse(rawValue, out var integer))
        {
            return integer;
        }

        errors.Add($"{path}:{lineNumber}: unsupported value \"{rawValue}\"");
        return null;
    }

    private static IReadOnlyList<string> ParseStringArray(string path, int lineNumber, string rawValue, List<string> errors)
    {
        var inner = rawValue[1..^1].Trim();
        if (inner.Length == 0)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var part in inner.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("\"", StringComparison.Ordinal) || !part.EndsWith("\"", StringComparison.Ordinal) || part.Length < 2)
            {
                errors.Add($"{path}:{lineNumber}: arrays must contain quoted strings");
                continue;
            }

            values.Add(part[1..^1]);
        }

        return values;
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inString = !inString;
            }
            else if (line[i] == '#' && !inString)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static bool IsKey(string key) =>
        key.Length > 0 && key.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_');
}

internal sealed record ManifestDocument(
    int? Version,
    IReadOnlyList<ManifestEntry> Tasks,
    IReadOnlyList<ManifestEntry> Fixtures,
    IReadOnlyList<string> Errors);

internal sealed class ManifestEntry(int line)
{
    private readonly Dictionary<string, object?> fields = new(StringComparer.Ordinal);

    public int Line { get; } = line;

    public void Set(string key, object? value) => fields[key] = value;

    public string? GetString(string key) => fields.TryGetValue(key, out var value) ? value as string : null;

    public bool? GetBoolean(string key) => fields.TryGetValue(key, out var value) ? value as bool? : null;

    public IReadOnlyList<string> GetStringArray(string key) =>
        fields.TryGetValue(key, out var value) && value is IReadOnlyList<string> strings ? strings : [];
}

internal sealed record RequiredFixture(string Slug, string Name);

internal sealed record RequiredTask(string Slug, string Name);

internal sealed record RequiredSurface(string Name, IReadOnlyList<string> PathCandidates);

internal sealed record RequiredProject(string Name, string Path, bool MustExist);

internal sealed record ValidationFailure(string CheckId, string Message, string? Path = null)
{
    public string ToDisplayString() => $"{CheckId}: {Message}";
}
