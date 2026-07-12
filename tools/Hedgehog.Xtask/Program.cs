using System.Text.Json;
using System.Text.RegularExpressions;
using Hedgehog.LocalRuntime;
using Hedgehog.Types;

var parsedCommand = CommandLine.Parse(args);
if (parsedCommand is null)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/Hedgehog.Xtask -- <validate-scaffold-contract|run-local-runtime-smoke|run-local-runtime-stress|run-local-restore-drill> [--json] [--allow-missing-scaffold] [--runtime-root <path>] [--restore-runtime-root <path>] [--tenant-count <n>] [--objects-per-tenant <n>] [--payload-bytes <n>]");
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
    var useDefaultRuntimeRoots = parsedCommand.RuntimeRoot is null && parsedCommand.RestoreRuntimeRoot is null;
    var sourceRuntimeRoot = parsedCommand.RuntimeRoot
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "local-restore-drill-source");
    var restoredRuntimeRoot = parsedCommand.RestoreRuntimeRoot
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "local-restore-drill-restored");

    foreach (var runtimeRoot in new[] { sourceRuntimeRoot, restoredRuntimeRoot })
    {
        if (!Directory.Exists(runtimeRoot))
        {
            continue;
        }

        if (!useDefaultRuntimeRoots)
        {
            Console.Error.WriteLine($"custom runtime root already exists and will not be deleted automatically: {runtimeRoot}");
            return 1;
        }

        Directory.Delete(runtimeRoot, recursive: true);
    }

    var result = await LocalRuntimeRestoreDrill.RunAsync(
        LocalClusterOptions.CreateDefault(sourceRuntimeRoot),
        restoredRuntimeRoot);

    if (parsedCommand.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }
    else
    {
        Console.WriteLine("local restore drill passed");
        Console.WriteLine($"source_runtime_root={result.SourceRuntimeRoot}");
        Console.WriteLine($"restored_runtime_root={result.RestoredRuntimeRoot}");
        Console.WriteLine($"objects_written_before_backup={result.ObjectsWrittenBeforeBackup}");
        Console.WriteLine($"reads_verified_after_restore={result.ReadsVerifiedAfterRestore}");
        Console.WriteLine($"delete_verified_after_restore={result.DeleteVerifiedAfterRestore}");
        Console.WriteLine($"objects_written_after_restore={result.ObjectsWrittenAfterRestore}");
        Console.WriteLine($"metadata_object_rows={result.MetadataObjectRows}");
        Console.WriteLine($"metadata_version_rows={result.MetadataVersionRows}");
        Console.WriteLine($"healthy_replica_rows={result.HealthyReplicaRows}");
        Console.WriteLine($"delete_marker_rows={result.DeleteMarkerRows}");
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
    string? RestoreRuntimeRoot,
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
        string? restoreRuntimeRoot = null;
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
                case "--restore-runtime-root":
                    if (i + 1 >= args.Length)
                    {
                        return null;
                    }

                    restoreRuntimeRoot = args[++i];
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

        return new CommandLine(command, json, allowMissingScaffold, runtimeRoot, restoreRuntimeRoot, tenantCount, objectsPerTenant, payloadBytes);
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
        new("Hedgehog.Head", "src/Hedgehog.Head/Hedgehog.Head.csproj", tÛ^x¶‰žËkºwµçh€€€€€€€€€€€¥˜€¡Í•Ñ¥½¸€ôô€‰™¥áÑÕÉ”ˆ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€I•ÅÕ¥É•MÑÉ¥¹œ¡Í•Ñ¥½¸°•¹ÑÉä°€‰Í•¹…É¥¼ˆ°™…¥±ÕÉ•Ì¤ì(€€€€€€€€€€€€€€€¥˜€¡•¹ÑÉä¹•Ñ	½½±•…¸ ‰‰•Ñ…}‰±½­•Èˆ¤€„ôÑÉÕ”¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰™¥áÑÕÉ•Ì¹µ…¹¥™•ÍÐˆ°€‰™¥áÑÕÉ”p‰íÍ±Õœ€üü€‰±¥¹”í•¹ÑÉä¹1¥¹•ô‰õpˆµÕÍÐÍ•Ð‰•Ñ…}‰±½­•È€ôÑÉÕ”ˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô(€€€€€€€€€€€•±Í”¥˜€¡Í•Ñ¥½¸€ôô€‰Ñ…Í¬ˆ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€I•ÅÕ¥É•MÑÉ¥¹œ¡Í•Ñ¥½¸°•¹ÑÉä°€‰ÍÑ…ÑÕÌˆ°™…¥±ÕÉ•Ì¤ì(€€€€€€€€€€€€€€€¥˜€¡•¹ÑÉä¹•Ñ	½½±•…¸ ‰ØÅ}É•ÅÕ¥É•ˆ¤€„ôÑÉÕ”¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰ÉÕ¹Ñ¥µ”¹Ñ…Í­Ìˆ°€‰Ñ…Í¬p‰íÍ±Õœ€üü€‰±¥¹”í•¹ÑÉä¹1¥¹•ô‰õpˆµÕÍÐÍ•ÐØÅ}É•ÅÕ¥É•€ôÑÉÕ”ˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥I•ÅÕ¥É•MÑÉ¥¹œ¡ÍÑÉ¥¹œÍ•Ñ¥½¸°5…¹¥™•ÍÑ¹ÑÉä•¹ÑÉä°ÍÑÉ¥¹œ­•ä°1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡•¹ÑÉä¹•ÑMÑÉ¥¹œ¡­•ä¤¤¤(€€€€€€€ì(€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰™¥áÑÕÉ•Ì¹µ…¹¥™•ÍÐˆ°€‰íÍ•Ñ¥½¹ôp‰í•¹ÑÉä¹•ÑMÑÉ¥¹œ ‰Í±Õœˆ¤€üü€‰±¥¹”í•¹ÑÉä¹1¥¹•ô‰õpˆ¥Ìµ¥ÍÍ¥¹œí­•åôˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥Y…±¥‘…Ñ•I•ÅÕ¥É•‘Q…Í­Ì¡5…¹¥™•ÍÑ½Õµ•¹Ðµ…¹¥™•ÍÐ°1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€Ù…È‰åM±Õœ€ô%¹‘•á¹ÑÉ¥•Í	åM±Õœ ‰Ñ…Í¬ˆ°µ…¹¥™•ÍÐ¹Q…Í­Ì°™…¥±ÕÉ•Ì¤ì((€€€€€€€™½É•… €¡Ù…ÈÑ…Í¬¥¸I•ÅÕ¥É•‘Q…Í­Ì¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …‰åM±Õœ¹QÉå•ÑY…±Õ”¡Ñ…Í¬¹M±Õœ°½ÕÐÙ…È•¹ÑÉä¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰ÉÕ¹Ñ¥µ”¹Ñ…Í­Ìˆ°€‰µ¥ÍÍ¥¹œ±½…°ÉÕ¹Ñ¥µ”Ñ…Í¬p‰íÑ…Í¬¹9…µ•õpˆ€¡Í±ÕœíÑ…Í¬¹M±Õô¤ˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡•¹ÑÉä¹•Ñ	½½±•…¸ ‰ØÅ}É•ÅÕ¥É•ˆ¤€„ôÑÉÕ”¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰ÉÕ¹Ñ¥µ”¹Ñ…Í­Ìˆ°€‰±½…°ÉÕ¹Ñ¥µ”Ñ…Í¬p‰íÑ…Í¬¹M±ÕõpˆµÕÍÐ‰”ØÅ}É•ÅÕ¥É•ˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥Y…±¥‘…Ñ•I•ÅÕ¥É•‘¥áÑÕÉ•Ì¡5…¹¥™•ÍÑ½Õµ•¹Ðµ…¹¥™•ÍÐ°1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€Ù…È‰åM±Õœ€ô%¹‘•á¹ÑÉ¥•Í	åM±Õœ ‰™¥áÑÕÉ”ˆ°µ…¹¥™•ÍÐ¹¥áÑÕÉ•Ì°™…¥±ÕÉ•Ì¤ì((€€€€€€€™½É•… €¡Ù…È™¥áÑÕÉ”¥¸I•ÅÕ¥É•‘¥áÑÕÉ•Ì¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …‰åM±Õœ¹QÉå•ÑY…±Õ”¡™¥áÑÕÉ”¹M±Õœ°½ÕÐÙ…È•¹ÑÉä¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰™¥áÑÕÉ•Ì¹ÁÉ•Í•¹Ðˆ°€‰µ¥ÍÍ¥¹œ‰•Ñ„™¥áÑÕÉ”p‰í™¥áÑÕÉ”¹9…µ•õpˆ€¡Í±Õœí™¥áÑÕÉ”¹M±Õô¤ˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹ÅÕ…±Ì¡•¹ÑÉä¹•ÑMÑÉ¥¹œ ‰Í•¹…É¥¼ˆ¤°™¥áÑÕÉ”¹9…µ”°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰™¥áÑÕÉ•Ì¹ÁÉ•Í•¹Ðˆ°€‰™¥áÑÕÉ”p‰í™¥áÑÕÉ”¹M±ÕõpˆµÕÍÐÕÍ”Í•¹…É¥¼p‰í™¥áÑÕÉ”¹9…µ•õpˆˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡•¹ÑÉä¹•Ñ	½½±•…¸ ‰‰•Ñ…}‰±½­•Èˆ¤€„ôÑÉÕ”¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰™¥áÑÕÉ•Ì¹ÁÉ•Í•¹Ðˆ°€‰™¥áÑÕÉ”p‰í™¥áÑÕÉ”¹M±ÕõpˆµÕÍÐ‰”µ…É­•‰•Ñ…}‰±½­•Èˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°5…¹¥™•ÍÑ¹ÑÉäø%¹‘•á¹ÑÉ¥•Í	åM±Õœ (€€€€€€€ÍÑÉ¥¹œÍ•Ñ¥½¸°(€€€€€€€%I•…‘=¹±å1¥ÍÐñ5…¹¥™•ÍÑ¹ÑÉäø•¹ÑÉ¥•Ì°(€€€€€€€1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€Ù…È‰åM±Õœ€ô¹•Ü¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°5…¹¥™•ÍÑ¹ÑÉäø¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤ì(€€€€€€€™½É•… €¡Ù…È•¹ÑÉä¥¸•¹ÑÉ¥•Ì¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍ±Õœ€ô•¹ÑÉä¹•ÑMÑÉ¥¹œ ‰Í±Õœˆ¤ì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡Í±Õœ¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€ …‰åM±Õœ¹QÉå‘¡Í±Õœ°•¹ÑÉä¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰™¥áÑÕÉ•Ì¹µ…¹¥™•ÍÐˆ°€‰‘ÕÁ±¥…Ñ”íÍ•Ñ¥½¹ôÍ±Õœp‰íÍ±Õõpˆˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸‰åM±Õœì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥Y…±¥‘…Ñ•5…¹¥™•ÍÑ1…‰•±Ì¡5…¹¥™•ÍÑ½Õµ•¹Ðµ…¹¥™•ÍÐ°1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€Ù…È±…‰•±Í	å½µ…¥¸€ô1…‰•±Ì¹±±É½ÕÁÌ(€€€€€€€€€€€€¹M•±•Ñ5…¹ä¡É½ÕÀ€ôøÉ½ÕÀ¤(€€€€€€€€€€€€¹É½ÕÁ	ä¡±…‰•°€ôø±…‰•°¹½µ…¥¸°MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤(€€€€€€€€€€€€¹Q½¥Ñ¥½¹…Éä (€€€€€€€€€€€€€€€É½ÕÀ€ôøÉ½ÕÀ¹-•ä°(€€€€€€€€€€€€€€€É½ÕÀ€ôøÉ½ÕÀ¹M•±•Ð¡±…‰•°€ôø±…‰•°¹]¥É”¤¹Q½!…Í¡M•Ð¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤°(€€€€€€€€€€€€€€€MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤ì((€€€€€€€™½É•… €¡Ù…È•¹ÑÉä¥¸µ…¹¥™•ÍÐ¹¥áÑÕÉ•Ì¤(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍ±Õœ€ô•¹ÑÉä¹•ÑMÑÉ¥¹œ ‰Í±Õœˆ¤€üü€‰±¥¹”í•¹ÑÉä¹1¥¹•ôˆì(€€€€€€€€€€€™½É•… €¡Ù…È±…‰•±I•˜¥¸•¹ÑÉä¹•ÑMÑÉ¥¹ÉÉ…ä ‰±…‰•±Ìˆ¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€Ù…È‘½Ñ%¹‘•à€ô±…‰•±I•˜¹%¹‘•á=˜ œ¸œ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ì(€€€€€€€€€€€€€€€¥˜€¡‘½Ñ%¹‘•à€ðô€Àñð‘½Ñ%¹‘•à€ôô±…‰•±I•˜¹1•¹Ñ €´€Ä¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰±…‰•±Ì¹…¹½¹¥…°ˆ°€‰™¥áÑÕÉ”p‰íÍ±Õõpˆ±…‰•°p‰í±…‰•±I•™õpˆµÕÍÐÕÍ”‘½µ…¥¸¹Ý¥É”™½Éµ…Ðˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€Ù…È‘½µ…¥¸€ô±…‰•±I•™l¸¹‘½Ñ%¹‘•átì(€€€€€€€€€€€€€€€Ù…ÈÝ¥É”€ô±…‰•±I•™l¡‘½Ñ%¹‘•à€¬€Ä¤¸¹tì(€€€€€€€€€€€€€€€¥˜€ …±…‰•±Í	å½µ…¥¸¹QÉå•ÑY…±Õ”¡‘½µ…¥¸°½ÕÐÙ…ÈÙ…±¥‘1…‰•±Ì¤ñð€…Ù…±¥‘1…‰•±Ì¹½¹Ñ…¥¹Ì¡Ý¥É”¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰±…‰•±Ì¹…¹½¹¥…°ˆ°€‰™¥áÑÕÉ”p‰íÍ±ÕõpˆÉ•™•É•¹•ÌÕ¹­¹½Ý¸±…‰•°p‰í±…‰•±I•™õpˆˆ°5…¹¥™•ÍÑA…Ñ ¤¤ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥Y…±¥‘…Ñ•‘µ¥¹MÕÉ™…•Ì¡1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€™½É•… €¡Ù…ÈÍÕÉ™…”¥¸‘µ¥¹MÕÉ™…•Ì¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡ÍÕÉ™…”¹A…Ñ¡…¹‘¥‘…Ñ•Ì¹¹ä¡Á…Ñ €ôø¥±”¹á¥ÍÑÌ¡Õ±±A…Ñ ¡Á…Ñ ¤¤ñð¥É•Ñ½Éä¹á¥ÍÑÌ¡Õ±±A…Ñ ¡Á…Ñ ¤¤¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰…‘µ¥¸¹¥¹Ñ•É™…”ˆ°€‰µ¥ÍÍ¥¹œíÍÕÉ™…”¹9…µ•ôÍÕÉ™…”ì•áÁ•Ñ•½¹”½˜èíÍÑÉ¥¹œ¹)½¥¸ ˆ°€ˆ°ÍÕÉ™…”¹A…Ñ¡…¹‘¥‘…Ñ•Ì¥ôˆ¤¤ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥Y…±¥‘…Ñ•5¥É…Ñ¥½¹Ì¡1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€™½É•… €¡Ù…ÈÁ…Ñ ¥¸I•ÅÕ¥É•‘5¥É…Ñ¥½¹Ì¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …¥±”¹á¥ÍÑÌ¡Õ±±A…Ñ ¡Á…Ñ ¤¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰µ¥É…Ñ¥½¹Ì¹ÁÉ•Í•¹Ðˆ°€‰µ¥ÍÍ¥¹œÉ•ÅÕ¥É•ME1¥Ñ”µ¥É…Ñ¥½¸èíÁ…Ñ¡ôˆ°Á…Ñ ¤¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”Ù½¥Y…±¥‘…Ñ•AÉ½©•Ñ1…å½ÕÐ¡1¥ÍÐñY…±¥‘…Ñ¥½¹…¥±ÕÉ”ø™…¥±ÕÉ•Ì¤(€€€ì(€€€€€€€Ù…È½¹ÑÉ…ÑA…Ñ €ô€‰‘½Ì½ÁÉ½©•Ðµ±…å½ÕÐµØÄ¹µˆì(€€€€€€€Ù…È½¹ÑÉ…ÑÕ±±A…Ñ €ôÕ±±A…Ñ ¡½¹ÑÉ…ÑA…Ñ ¤ì(€€€€€€€Ù…È½¹ÑÉ…ÑQ•áÐ€ô¥±”¹á¥ÍÑÌ¡½¹ÑÉ…ÑÕ±±A…Ñ ¤€ü¥±”¹I•…‘±±Q•áÐ¡½¹ÑÉ…ÑÕ±±A…Ñ ¤€èÍÑÉ¥¹œ¹µÁÑäì(€€€€€€€Ù…È­¹½Ý¹AÉ½©•ÑA…Ñ¡Ì€ôAÉ½©•Ñ1…å½ÕÐ¹M•±•Ð¡ÁÉ½©•Ð€ôøÁÉ½©•Ð¹A…Ñ ¤¹Q½!…Í¡M•Ð¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤ì((€€€€€€€™½É•… €¡Ù…ÈÁÉ½©•Ð¥¸AÉ½©•Ñ1…å½ÕÐ¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …½¹ÑÉ…ÑQ•áÐ¹½¹Ñ…¥¹Ì¡ÁÉ½©•Ð¹9…µ”°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤(€€€€€€€€€€€€€€€ñð€…½¹ÑÉ…ÑQ•áÐ¹½¹Ñ…¥¹Ì¡ÁÉ½©•Ð¹A…Ñ °MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰ÁÉ½©•ÑÌ¹±…å½ÕÐˆ°€‰±…å½ÕÐ½¹ÑÉ…ÐµÕÍÐ‘•±…É”íÁÉ½©•Ð¹9…µ•ô…ÐíÁÉ½©•Ð¹A…Ñ¡ôˆ°½¹ÑÉ…ÑA…Ñ ¤¤ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡ÁÉ½©•Ð¹5ÕÍÑá¥ÍÐ€˜˜€…¥±”¹á¥ÍÑÌ¡Õ±±A…Ñ ¡ÁÉ½©•Ð¹A…Ñ ¤¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰ÁÉ½©•ÑÌ¹±…å½ÕÐˆ°€‰É•ÅÕ¥É•ÁÉ½©•Ð¥Ìµ¥ÍÍ¥¹œèíÁÉ½©•Ð¹A…Ñ¡ôˆ°ÁÉ½©•Ð¹A…Ñ ¤¤ì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€™½É•… €¡Ù…ÈÁÉ½©•Ñ¥±”¥¸¹Õµ•É…Ñ•AÉ½©•Ñ¥±•Ì ¤¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …­¹½Ý¹AÉ½©•ÑA…Ñ¡Ì¹½¹Ñ…¥¹Ì¡ÁÉ½©•Ñ¥±”¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€™…¥±ÕÉ•Ì¹‘¡¹•Ü ‰ÁÉ½©•ÑÌ¹±…å½ÕÐˆ°€‰ÁÉ½©•Ð™¥±”¥Ì½ÕÑÍ¥‘”Ñ¡”ØÄ±…å½ÕÐ½¹ÑÉ…ÐèíÁÉ½©•Ñ¥±•ôˆ°ÁÉ½©•Ñ¥±”¤¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”%¹Õµ•É…‰±”ñÍÑÉ¥¹œø¹Õµ•É…Ñ•AÉ½©•Ñ¥±•Ì ¤(€€€ì(€€€€€€€™½É•… €¡Ù…È‘¥É•Ñ½Éä¥¸¹•Ýmtì€‰ÍÉŒˆ°€‰Ñ•ÍÑÌˆ°€‰Ñ½½±Ìˆô¤(€€€€€€€ì(€€€€€€€€€€€Ù…È™Õ±±¥É•Ñ½Éä€ôÕ±±A…Ñ ¡‘¥É•Ñ½Éä¤ì(€€€€€€€€€€€¥˜€ …¥É•Ñ½Éä¹á¥ÍÑÌ¡™Õ±±¥É•Ñ½Éä¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€™½É•… €¡Ù…È™¥±”¥¸¥É•Ñ½Éä¹¹Õµ•É…Ñ•¥±•Ì¡™Õ±±¥É•Ñ½Éä°€ˆ¨¹ÍÁÉ½¨ˆ°M•…É¡=ÁÑ¥½¸¹±±¥É•Ñ½É¥•Ì¤¹=É‘•È¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€å¥•±É•ÑÕÉ¸A…Ñ ¹•ÑI•±…Ñ¥Ù•A…Ñ ¡É½½Ð°™¥±”¤(€€€€€€€€€€€€€€€€€€€€¹I•Á±…”¡A…Ñ ¹¥É•Ñ½ÉåM•Á…É…Ñ½É¡…È°€œ¼œ¤(€€€€€€€€€€€€€€€€€€€€¹I•Á±…”¡A…Ñ ¹±Ñ¥É•Ñ½ÉåM•Á…É…Ñ½É¡…È°€œ¼œ¤ì(€€€€€€€€€€€ô(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”%¹Õµ•É…‰±”ñÍÑÉ¥¹œø¹Õµ•É…Ñ•=ÁÑ¥½¹…±M…¹¥±•Ì¡ÍÑÉ¥¹œ‘¥É•Ñ½Éä°ÍÑÉ¥¹œÁ…ÑÑ•É¸¤(€€€ì(€€€€€€€Ù…È™Õ±±¥É•Ñ½Éä€ôÕ±±A…Ñ ¡‘¥É•Ñ½Éä¤ì(€€€€€€€¥˜€ …¥É•Ñ½Éä¹á¥ÍÑÌ¡™Õ±±¥É•Ñ½Éä¤¤(€€€€€€€ì(€€€€€€€€€€€å¥•±‰É•…¬ì(€€€€€€€ô((€€€€€€€™½É•… €¡Ù…È™¥±”¥¸¥É•Ñ½Éä¹¹Õµ•É…Ñ•¥±•Ì¡™Õ±±¥É•Ñ½Éä°Á…ÑÑ•É¸°M•…É¡=ÁÑ¥½¸¹±±¥É•Ñ½É¥•Ì¤¹=É‘•È¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤¤(€€€€€€€ì(€€€€€€€€€€€å¥•±É•ÑÕÉ¸A…Ñ ¹•ÑI•±…Ñ¥Ù•A…Ñ ¡É½½Ð°™¥±”¤¹I•Á±…”¡A…Ñ ¹¥É•Ñ½ÉåM•Á…É…Ñ½É¡…È°€œ¼œ¤ì(€€€€€€€ô(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑÉ¥¹œÕ±±A…Ñ ¡ÍÑÉ¥¹œÁ…Ñ ¤€ôøA…Ñ ¹½µ‰¥¹”¡É½½Ð°Á…Ñ ¤ì((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í1½Ý•É…Í•MÑ…‰±”¡ÍÑÉ¥¹œÙ…±Õ”¤€ôø(€€€€€€€Ù…±Õ”¹1•¹Ñ €ø€À€˜˜Ù…±Õ”¹±°¡ €ôø€¡ €øô€„œ€˜˜ €ðô€èœ¤ñð€¡ €øô€œÀœ€˜˜ €ðô€œäœ¤ñð €ôô€|œ¤ì)ô()¥¹Ñ•É¹…°ÍÑ…Ñ¥Œ±…ÍÌ5…¹¥™•ÍÑA…ÉÍ•È)ì(€€€ÁÕ‰±¥ŒÍÑ…Ñ¥Œ5…¹¥™•ÍÑ½Õµ•¹ÐA…ÉÍ”¡ÍÑÉ¥¹œÁ…Ñ °%I•…‘=¹±å1¥ÍÐñÍÑÉ¥¹œø±¥¹•Ì¤(€€€ì(€€€€€€€¥¹ÐüÙ•ÉÍ¥½¸€ô¹Õ±°ì(€€€€€€€Ù…ÈÑ…Í­Ì€ô¹•Ü1¥ÍÐñ5…¹¥™•ÍÑ¹ÑÉäø ¤ì(€€€€€€€Ù…È™¥áÑÕÉ•Ì€ô¹•Ü1¥ÍÐñ5…¹¥™•ÍÑ¹ÑÉäø ¤ì(€€€€€€€Ù…È•ÉÉ½ÉÌ€ô¹•Ü1¥ÍÐñÍÑÉ¥¹œø ¤ì(€€€€€€€5…¹¥™•ÍÑ¹ÑÉäüÕÉÉ•¹Ñ¹ÑÉä€ô¹Õ±°ì(€€€€€€€ÍÑÉ¥¹œüÕÉÉ•¹ÑM•Ñ¥½¸€ô¹Õ±°ì((€€€€€€€™½È€¡Ù…È¤€ô€Àì¤€ð±¥¹•Ì¹½Õ¹Ðì¤¬¬¤(€€€€€€€ì(€€€€€€€€€€€Ù…È±¥¹•9Õµ‰•È€ô¤€¬€Äì(€€€€€€€€€€€Ù…È±¥¹”€ôMÑÉ¥Á½µµ•¹Ð¡±¥¹•Ím¥t¤¹QÉ¥´ ¤ì(€€€€€€€€€€€¥˜€¡±¥¹”¹1•¹Ñ €ôô€À¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡±¥¹”¹MÑ…ÉÑÍ]¥Ñ  ‰mlˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤€˜˜±¥¹”¹¹‘Í]¥Ñ  ‰utˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€ÕÉÉ•¹ÑM•Ñ¥½¸€ô±¥¹•lÈ¸¹xÉt¹QÉ¥´ ¤ì(€€€€€€€€€€€€€€€ÕÉÉ•¹Ñ¹ÑÉä€ô¹•Ü5…¹¥™•ÍÑ¹ÑÉä¡±¥¹•9Õµ‰•È¤ì((€€€€€€€€€€€€€€€ÍÝ¥Ñ €¡ÕÉÉ•¹ÑM•Ñ¥½¸¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€…Í”€‰Ñ…Í¬ˆè(€€€€€€€€€€€€€€€€€€€€€€€Ñ…Í­Ì¹‘¡ÕÉÉ•¹Ñ¹ÑÉä¤ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€€€€€€€€€…Í”€‰™¥áÑÕÉ”ˆè(€€€€€€€€€€€€€€€€€€€€€€€™¥áÑÕÉ•Ì¹‘¡ÕÉÉ•¹Ñ¹ÑÉä¤ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€€€€€€€€€‘•™…Õ±Ðè(€€€€€€€€€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•ÉôèÕ¹­¹½Ý¸Í•Ñ¥½¸mmíÕÉÉ•¹ÑM•Ñ¥½¹õutˆ¤ì(€€€€€€€€€€€€€€€€€€€€€€€ÕÉÉ•¹Ñ¹ÑÉä€ô¹Õ±°ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È•ÅÕ…±Í%¹‘•à€ô±¥¹”¹%¹‘•á=˜ œôœ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ì(€€€€€€€€€€€¥˜€¡•ÅÕ…±Í%¹‘•à€ðô€À¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•Éôè•áÁ•Ñ•­•ä€ôÙ…±Õ”ˆ¤ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È­•ä€ô±¥¹•l¸¹•ÅÕ…±Í%¹‘•át¹QÉ¥´ ¤ì(€€€€€€€€€€€Ù…ÈÉ…ÝY…±Õ”€ô±¥¹•l¡•ÅÕ…±Í%¹‘•à€¬€Ä¤¸¹t¹QÉ¥´ ¤ì(€€€€€€€€€€€¥˜€ …%Í-•ä¡­•ä¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•Éôè¥¹Ù…±¥­•äp‰í­•åõpˆˆ¤ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…ÈÙ…±Õ”€ôA…ÉÍ•Y…±Õ”¡Á…Ñ °±¥¹•9Õµ‰•È°É…ÝY…±Õ”°•ÉÉ½ÉÌ¤ì(€€€€€€€€€€€¥˜€¡ÕÉÉ•¹Ñ¹ÑÉä¥Ì¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹ÅÕ…±Ì¡­•ä°€‰Ù•ÉÍ¥½¸ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•ÉôèÑ½Àµ±•Ù•°­•äp‰í­•åõpˆ¥Ì¹½ÐÍÕÁÁ½ÉÑ•ˆ¤ì(€€€€€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€¥˜€¡Ù…±Õ”¥Ì¥¹ÐÁ…ÉÍ•‘Y•ÉÍ¥½¸¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€Ù•ÉÍ¥½¸€ôÁ…ÉÍ•‘Y•ÉÍ¥½¸ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€•±Í”(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•ÉôèÙ•ÉÍ¥½¸µÕÍÐ‰”…¸¥¹Ñ••Èˆ¤ì(€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡ÕÉÉ•¹ÑM•Ñ¥½¸¥Ì¹½Ð€ ‰Ñ…Í¬ˆ½È€‰™¥áÑÕÉ”ˆ¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•Éôè­•ä½ÕÑÍ¥‘”ÍÕÁÁ½ÉÑ•Í•Ñ¥½¸ˆ¤ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€ÕÉÉ•¹Ñ¹ÑÉä¹M•Ð¡­•ä°Ù…±Õ”¤ì(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸¹•Ü5…¹¥™•ÍÑ½Õµ•¹Ð¡Ù•ÉÍ¥½¸°Ñ…Í­Ì°™¥áÑÕÉ•Ì°•ÉÉ½ÉÌ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ½‰©•ÐüA…ÉÍ•Y…±Õ”¡ÍÑÉ¥¹œÁ…Ñ °¥¹Ð±¥¹•9Õµ‰•È°ÍÑÉ¥¹œÉ…ÝY…±Õ”°1¥ÍÐñÍÑÉ¥¹œø•ÉÉ½ÉÌ¤(€€€ì(€€€€€€€¥˜€¡É…ÝY…±Õ”¹MÑ…ÉÑÍ]¥Ñ  ‰pˆˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤€˜˜É…ÝY…±Õ”¹¹‘Í]¥Ñ  ‰pˆˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤€˜˜É…ÝY…±Õ”¹1•¹Ñ €øô€È¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸É…ÝY…±Õ•lÄ¸¹xÅtì(€€€€€€€ô((€€€€€€€¥˜€¡É…ÝY…±Õ”¥Ì€‰ÑÉÕ”ˆ½È€‰™…±Í”ˆ¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸ÍÑÉ¥¹œ¹ÅÕ…±Ì¡É…ÝY…±Õ”°€‰ÑÉÕ”ˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ì(€€€€€€€ô((€€€€€€€¥˜€¡É…ÝY…±Õ”¹MÑ…ÉÑÍ]¥Ñ  ‰lˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤€˜˜É…ÝY…±Õ”¹¹‘Í]¥Ñ  ‰tˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸A…ÉÍ•MÑÉ¥¹ÉÉ…ä¡Á…Ñ °±¥¹•9Õµ‰•È°É…ÝY…±Õ”°•ÉÉ½ÉÌ¤ì(€€€€€€€ô((€€€€€€€¥˜€¡¥¹Ð¹QÉåA…ÉÍ”¡É…ÝY…±Õ”°½ÕÐÙ…È¥¹Ñ••È¤¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸¥¹Ñ••Èì(€€€€€€€ô((€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•ÉôèÕ¹ÍÕÁÁ½ÉÑ•Ù…±Õ”p‰íÉ…ÝY…±Õ•õpˆˆ¤ì(€€€€€€€É•ÑÕÉ¸¹Õ±°ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ%I•…‘=¹±å1¥ÍÐñÍÑÉ¥¹œøA…ÉÍ•MÑÉ¥¹ÉÉ…ä¡ÍÑÉ¥¹œÁ…Ñ °¥¹Ð±¥¹•9Õµ‰•È°ÍÑÉ¥¹œÉ…ÝY…±Õ”°1¥ÍÐñÍÑÉ¥¹œø•ÉÉ½ÉÌ¤(€€€ì(€€€€€€€Ù…È¥¹¹•È€ôÉ…ÝY…±Õ•lÄ¸¹xÅt¹QÉ¥´ ¤ì(€€€€€€€¥˜€¡¥¹¹•È¹1•¹Ñ €ôô€À¤(€€€€€€€ì(€€€€€€€€€€€É•ÑÕÉ¸mtì(€€€€€€€ô((€€€€€€€Ù…ÈÙ…±Õ•Ì€ô¹•Ü1¥ÍÐñÍÑÉ¥¹œø ¤ì(€€€€€€€™½É•… €¡Ù…ÈÁ…ÉÐ¥¸¥¹¹•È¹MÁ±¥Ð œ°œ°MÑÉ¥¹MÁ±¥Ñ=ÁÑ¥½¹Ì¹QÉ¥µ¹ÑÉ¥•Ì¤¤(€€€€€€€ì(€€€€€€€€€€€¥˜€ …Á…ÉÐ¹MÑ…ÉÑÍ]¥Ñ  ‰pˆˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ñð€…Á…ÉÐ¹¹‘Í]¥Ñ  ‰pˆˆ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ñðÁ…ÉÐ¹1•¹Ñ €ð€È¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•ÉÉ½ÉÌ¹‘ ‰íÁ…Ñ¡ôéí±¥¹•9Õµ‰•Éôè…ÉÉ…åÌµÕÍÐ½¹Ñ…¥¸ÅÕ½Ñ•ÍÑÉ¥¹Ìˆ¤ì(€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…±Õ•Ì¹‘¡Á…ÉÑlÄ¸¹xÅt¤ì(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸Ù…±Õ•Ìì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÍÑÉ¥¹œMÑÉ¥Á½µµ•¹Ð¡ÍÑÉ¥¹œ±¥¹”¤(€€€ì(€€€€€€€Ù…È¥¹MÑÉ¥¹œ€ô™…±Í”ì(€€€€€€€™½È€¡Ù…È¤€ô€Àì¤€ð±¥¹”¹1•¹Ñ ì¤¬¬¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡±¥¹•m¥t€ôô€œˆœ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥¹MÑÉ¥¹œ€ô€…¥¹MÑÉ¥¹œì(€€€€€€€€€€€ô(€€€€€€€€€€€•±Í”¥˜€¡±¥¹•m¥t€ôô€œŒœ€˜˜€…¥¹MÑÉ¥¹œ¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸±¥¹•l¸¹¥tì(€€€€€€€€€€€ô(€€€€€€€ô((€€€€€€€É•ÑÕÉ¸±¥¹”ì(€€€ô((€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥Œ‰½½°%Í-•ä¡ÍÑÉ¥¹œ­•ä¤€ôø(€€€€€€€­•ä¹1•¹Ñ €ø€À€˜˜­•ä¹±°¡ €ôø€¡ €øô€„œ€˜˜ €ðô€èœ¤ñð€¡ €øô€œ€˜˜ €ðô€hœ¤ñð€¡ €øô€œÀœ€˜˜ €ðô€œäœ¤ñð €ôô€|œ¤ì)ô()¥¹Ñ•É¹…°Í•…±•É•½É5…¹¥™•ÍÑ½Õµ•¹Ð (€€€¥¹ÐüY•ÉÍ¥½¸°(€€€%I•…‘=¹±å1¥ÍÐñ5…¹¥™•ÍÑ¹ÑÉäøQ…Í­Ì°(€€€%I•…‘=¹±å1¥ÍÐñ5…¹¥™•ÍÑ¹ÑÉäø¥áÑÕÉ•Ì°(€€€%I•…‘=¹±å1¥ÍÐñÍÑÉ¥¹œøÉÉ½ÉÌ¤ì()¥¹Ñ•É¹…°Í•…±•±…ÍÌ5…¹¥™•ÍÑ¹ÑÉä¡¥¹Ð±¥¹”¤)ì(€€€ÁÉ¥Ù…Ñ”É•…‘½¹±ä¥Ñ¥½¹…ÉäñÍÑÉ¥¹œ°½‰©•Ðüø™¥•±‘Ì€ô¹•Ü¡MÑÉ¥¹½µÁ…É•È¹=É‘¥¹…°¤ì((€€€ÁÕ‰±¥Œ¥¹Ð1¥¹”ì•Ðìô€ô±¥¹”ì((€€€ÁÕ‰±¥ŒÙ½¥M•Ð¡ÍÑÉ¥¹œ­•ä°½‰©•ÐüÙ…±Õ”¤€ôø™¥•±‘Ím­•åt€ôÙ…±Õ”ì((€€€ÁÕ‰±¥ŒÍÑÉ¥¹œü•ÑMÑÉ¥¹œ¡ÍÑÉ¥¹œ­•ä¤€ôø™¥•±‘Ì¹QÉå•ÑY…±Õ”¡­•ä°½ÕÐÙ…ÈÙ…±Õ”¤€üÙ…±Õ”…ÌÍÑÉ¥¹œ€è¹Õ±°ì((€€€ÁÕ‰±¥Œ‰½½°ü•Ñ	½½±•…¸¡ÍÑÉ¥¹œ­•ä¤€ôø™¥•±‘Ì¹QÉå•ÑY…±Õ”¡­•ä°½ÕÐÙ…ÈÙ…±Õ”¤€üÙ…±Õ”…Ì‰½½°ü€è¹Õ±°ì((€€€ÁÕ‰±¥Œ%I•…‘=¹±å1¥ÍÐñÍÑÉ¥¹œø•ÑMÑÉ¥¹ÉÉ…ä¡ÍÑÉ¥¹œ­•ä¤€ôø(€€€€€€€™¥•±‘Ì¹QÉå•ÑY…±Õ”¡­•ä°½ÕÐÙ…ÈÙ…±Õ”¤€˜˜Ù…±Õ”¥Ì%I•…‘=¹±å1¥ÍÐñÍÑÉ¥¹œøÍÑÉ¥¹Ì€üÍÑÉ¥¹Ì€èmtì)ô()¥¹Ñ•É¹…°Í•…±•É•½ÉI•ÅÕ¥É•‘¥áÑÕÉ”¡ÍÑÉ¥¹œM±Õœ°ÍÑÉ¥¹œ9…µ”¤ì()¥¹Ñ•É¹…°Í•…±•É•½ÉI•ÅÕ¥É•‘Q…Í¬¡ÍÑÉ¥¹œM±Õœ°ÍÑÉ¥¹œ9…µ”¤ì()¥¹Ñ•É¹…°Í•…±•É•½ÉI•ÅÕ¥É•‘MÕÉ™…”¡ÍÑÉ¥¹œ9…µ”°%I•…‘=¹±å1¥ÍÐñÍÑÉ¥¹œøA…Ñ¡…¹‘¥‘…Ñ•Ì¤ì()¥¹Ñ•É¹…°Í•…±•É•½ÉI•ÅÕ¥É•‘AÉ½©•Ð¡ÍÑÉ¥¹œ9…µ”°ÍÑÉ¥¹œA…Ñ °‰½½°5ÕÍÑá¥ÍÐ¤ì()¥¹Ñ•É¹…°Í•…±•É•½ÉY…±¥‘…Ñ¥½¹…¥±ÕÉ”¡ÍÑÉ¥¹œ¡•­%°ÍÑÉ¥¹œ5•ÍÍ…”°ÍÑÉ¥¹œüA…Ñ €ô¹Õ±°¤)ì(€€€ÁÕ‰±¥ŒÍÑÉ¥¹œQ½¥ÍÁ±…åMÑÉ¥¹œ ¤€ôø€‰í¡•­%‘ôèí5•ÍÍ…•ôˆì)ô(