using Hedgehog.LocalRuntime;

var runtimeRoot = Path.Combine(Path.GetTempPath(), $"hedgehog-local-runtime-test-{Guid.NewGuid():N}");
try
{
    var result = await LocalRuntimeSmoke.RunAsync(LocalClusterOptions.CreateDefault(runtimeRoot));

    Equal(2, result.HeadCount);
    Equal(3, result.StorageNodeCount);
    Equal(2, result.PublishedObjects);
    Equal(2, result.VerifiedRetrievals);
    Equal(true, result.DeleteVerified);
    Equal(2, result.MetadataObjectRows);
    Equal(6, result.HealthyReplicaRows);

    Console.WriteLine("Hedgehog.LocalRuntime.Tests passed.");
}
finally
{
    if (Directory.Exists(runtimeRoot))
    {
        Directory.Delete(runtimeRoot, recursive: true);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
    }
}
