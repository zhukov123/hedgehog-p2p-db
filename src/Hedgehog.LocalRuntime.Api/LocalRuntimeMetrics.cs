using System.Collections.Concurrent;
using System.Text;
using Hedgehog.LocalRuntime;
using Hedgehog.LocalRuntime.Api;

internal sealed class LocalRuntimeMetrics
{
    private readonly ConcurrentDictionary<string, long> operationCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> operationLatencyMilliseconds = new(StringComparer.Ordinal);
    private long bytesWritten;
    private long bytesRead;

    public void RecordOperation(
        string operation,
        string result,
        TimeSpan elapsed,
        long bytes = 0)
    {
        var key = $"{operation}|{result}";
        operationCounts.AddOrUpdate(key, 1, static (_, current) => current + 1);
        var elapsedMilliseconds = Math.Max(0, (long)elapsed.TotalMilliseconds);
        operationLatencyMilliseconds.AddOrUpdate(
            key,
            elapsedMilliseconds,
            (_, current) => current + elapsedMilliseconds);

        if (bytes > 0)
        {
            if (operation == "put")
            {
                Interlocked.Add(ref bytesWritten, bytes);
            }
            else if (operation == "get")
            {
                Interlocked.Add(ref bytesRead, bytes);
            }
        }
    }

    public string RenderPrometheus(
        LocalClusterSnapshot snapshot,
        IReadOnlyDictionary<string, long> metadataCounts,
        RecoveryReadinessDto recovery)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# HELP hedgehog_runtime_operations_total Total local runtime API operations.");
        builder.AppendLine("# TYPE hedgehog_runtime_operations_total counter");
        foreach (var (key, count) in operationCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var parts = key.Split('|');
            builder.AppendLine($"hedgehog_runtime_operations_total{{operation=\"{Escape(parts[0])}\",result=\"{Escape(parts[1])}\"}} {count}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_operation_latency_ms_total Total observed operation latency in milliseconds.");
        builder.AppendLine("# TYPE hedgehog_runtime_operation_latency_ms_total counter");
        foreach (var (key, elapsedMs) in operationLatencyMilliseconds.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var parts = key.Split('|');
            builder.AppendLine($"hedgehog_runtime_operation_latency_ms_total{{operation=\"{Escape(parts[0])}\",result=\"{Escape(parts[1])}\"}} {elapsedMs}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_bytes_written_total Total plaintext bytes accepted by the local runtime API.");
        builder.AppendLine("# TYPE hedgehog_runtime_bytes_written_total counter");
        builder.AppendLine($"hedgehog_runtime_bytes_written_total {Interlocked.Read(ref bytesWritten)}");
        builder.AppendLine("# HELP hedgehog_runtime_bytes_read_total Total plaintext bytes returned by the local runtime API.");
        builder.AppendLine("# TYPE hedgehog_runtime_bytes_read_total counter");
        builder.AppendLine($"hedgehog_runtime_bytes_read_total {Interlocked.Read(ref bytesRead)}");

        builder.AppendLine("# HELP hedgehog_runtime_tenants Current tenant dataset registrations.");
        builder.AppendLine("# TYPE hedgehog_runtime_tenants gauge");
        builder.AppendLine($"hedgehog_runtime_tenants {snapshot.Tenants.Count}");
        builder.AppendLine("# HELP hedgehog_runtime_heads Current head nodes.");
        builder.AppendLine("# TYPE hedgehog_runtime_heads gauge");
        builder.AppendLine($"hedgehog_runtime_heads {snapshot.Heads.Count}");
        builder.AppendLine("# HELP hedgehog_runtime_storage_nodes Current storage nodes.");
        builder.AppendLine("# TYPE hedgehog_runtime_storage_nodes gauge");
        builder.AppendLine($"hedgehog_runtime_storage_nodes {snapshot.StorageNodes.Count}");

        builder.AppendLine("# HELP hedgehog_runtime_storage_used_bytes Storage bytes used by node.");
        builder.AppendLine("# TYPE hedgehog_runtime_storage_used_bytes gauge");
        foreach (var node in snapshot.StorageNodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            builder.AppendLine($"hedgehog_runtime_storage_used_bytes{{node=\"{Escape(node.NodeId)}\"}} {node.UsedBytes}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_storage_free_bytes Storage bytes free by node.");
        builder.AppendLine("# TYPE hedgehog_runtime_storage_free_bytes gauge");
        foreach (var node in snapshot.StorageNodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            builder.AppendLine($"hedgehog_runtime_storage_free_bytes{{node=\"{Escape(node.NodeId)}\"}} {node.FreeBytes}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_storage_replicas Replica files by node.");
        builder.AppendLine("# TYPE hedgehog_runtime_storage_replicas gauge");
        foreach (var node in snapshot.StorageNodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            builder.AppendLine($"hedgehog_runtime_storage_replicas{{node=\"{Escape(node.NodeId)}\"}} {node.Replicas.Count}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_recovery_ready Recovery readiness decision from the canonical evaluator.");
        builder.AppendLine("# TYPE hedgehog_runtime_recovery_ready gauge");
        builder.AppendLine($"hedgehog_runtime_recovery_ready {Convert.ToInt32(recovery.Ready)}");
        builder.AppendLine("# HELP hedgehog_runtime_recovery_gate Recovery gate outcomes from the canonical evaluator.");
        builder.AppendLine("# TYPE hedgehog_runtime_recovery_gate gauge");
        foreach (var gate in recovery.Gates.OrderBy(gate => gate.Name, StringComparer.Ordinal))
        {
            builder.AppendLine(
                $"hedgehog_runtime_recovery_gate{{gate=\"{Escape(gate.Name)}\",status=\"{Escape(gate.Status)}\"}} 1");
        }

        builder.AppendLine("# HELP hedgehog_runtime_metadata_rows SQLite metadata row counts.");
        builder.AppendLine("# TYPE hedgehog_runtime_metadata_rows gauge");
        foreach (var (table, count) in metadataCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"hedgehog_runtime_metadata_rows{{table=\"{Escape(table)}\"}} {count}");
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
