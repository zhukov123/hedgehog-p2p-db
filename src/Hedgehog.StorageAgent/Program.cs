using Hedgehog.Agent.Store;

var nodeId = GetOption(args, "--node-id") ?? "node-local";
var root = GetOption(args, "--root") ?? Path.Combine(Directory.GetCurrentDirectory(), ".hedgehog", "storage");
var capacityBytes = long.TryParse(GetOption(args, "--capacity-bytes"), out var parsedCapacity)
    ? parsedCapacity
    : 1024L * 1024L * 1024L;
var once = args.Contains("--once", StringComparer.Ordinal);

var agent = new FileStorageAgent(nodeId, root, capacityBytes);
await agent.StartAsync();

var snapshot = await agent.SnapshotAsync();
Console.WriteLine($"storage-agent {snapshot.NodeId} running root={root} capacity={snapshot.CapacityBytes} used={snapshot.UsedBytes}");

if (!once)
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.Ordinal))
        {
            return args[i + 1];
        }
    }

    return null;
}
