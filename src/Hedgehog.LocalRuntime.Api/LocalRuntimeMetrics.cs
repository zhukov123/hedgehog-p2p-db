using System.Collections.Concurrent;
using System.Text;
using Hedgehog.Head;
using Hedgehog.LocalRuntime;

internal sealed class LocalRuntimeMetrics
{
    private readonly ConcurrentDictionary<string, long> operationCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> operationLatencyMilliseconds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> outboxDispatchCounts = new(StringComparer.Ordinal);
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

    public void RecordOutboxDispatch(OutboxDispatchResult result)
    {
        outboxDispatchCounts.AddOrUpdate("claimed", result.Claimed, (_, current) => current + result.Claimed);
        outboxDispatchCounts.AddOrUpdate("delivered", result.Delivered, (_, current) => current + result.Delivered);
        outboxDispatchCounts.AddOrUpdate("failed", result.Failed, (_, current) => current + result.Failed);
    }

    public string RenderPrometheus(
        LocalClusterSnapshot snapshot,
        IReadOnlyDictionary<string, long> metadataCounts,
        LocalOutboxSnapshot outbox)
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

        builder.AppendLine("# HELP hedgehog_runtime_metadata_rows SQLite metadata row counts.");
        builder.AppendLine("# TYPE hedgehog_runtime_metadata_rows gauge");
        foreach (var (table, count) in metadataCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"hedgehog_runtime_metadata_rows{{table=\"{Escape(table)}\"}} {count}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_outbox_dispatch_total Local runtime outbox dispatch results.");
        builder.AppendLine("# TYPE hedgehog_runtime_outbox_dispatch_total counter");
        foreach (var result in new[] { "claimed", "delivered", "failed" })
        {
            outboxDispatchCounts.TryGetValue(result, out var count);
            builder.AppendLine($"hedgehog_runtime_outbox_dispatch_total{{result=\"{result}\"}} {count}");
        }

        builder.AppendLine("# HELP hedgehog_runtime_outbox_rows SQLite outbox rows by dispatcher state.");
        builder.AppendLine("# TYPE hedgehog_runtime_outbox_rows gauge");
        builder.AppendLine($"hedgehog_runtime_outbox_rows{{state=\"pending\"}} {outbox.PendingRows}");
        builder.AppendLine($"hedgehog_runtime_outbox_rows{{state=\"leased\"}} {outbox.LeasedRows}");
        builder.AppendLine($"hedgehog_runtime_outbox_rows{{state=\"failed\"}} {outbox.FailedRows}");
        builder.AppendLine($"hedgehog_runtime_outbox_rows{{state=\"delivered\"}} {outbox.DeliveredRows}");
        builder.AppendLine("# HELP hedgehog_runtime_outbox_oldest_pending_age_seconds Oldest undelivered unleased outbox row age.");
        builder.AppendLine("# TYPE hedgehog_runtime_outbox_oldest_pending_age_seconds gauge");
        builder.AppendLine($"hedgehog_runtime_outbox_oldest_pending_age_seconds {outbox.OldestPendingAgeSeconds}");

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
