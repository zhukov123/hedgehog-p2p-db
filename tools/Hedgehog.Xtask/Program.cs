using Hedgehog.Types;

if (args is not ["validate-scaffold-contract"])
{
    Console.Error.WriteLine("usage: dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract");
    return 2;
}

string[] requiredDocs =
[
    "README.md",
    "p2p-object-store-guide.md",
    "p2p-object-store-key-model.md",
    "p2p-object-store-sqlite-schema-plan.md",
    "p2p-nosql-implementation-contract.md",
    "p2p-nosql-scaffold-contract.md",
];

(string Path, string Phrase)[] requiredPhrases =
[
    ("p2p-object-store-key-model.md", "object_lookup_hash = HMAC-SHA256(dataset_lookup_key, normalized_object_name)"),
    ("p2p-object-store-sqlite-schema-plan.md", "object_lookup_hash blob not null"),
    ("p2p-nosql-implementation-contract.md", "Use `Microsoft.Data.Sqlite` with SQLite for v1-alpha."),
    ("p2p-nosql-scaffold-contract-part-01.md", "Hedgehog.Metadata.Sqlite"),
];

string[] quarantineScanDocs =
[
    "p2p-object-store-sqlite-schema-plan.md",
    "p2p-nosql-replication-repair-state-machine.md",
];

string[] quarantinedTokens =
[
    "COMMIT_PENDING",
    "AVAILABLE",
    "TRANSFER_ASSIGNED",
    "UPLOADING",
];

var failures = new List<string>();

foreach (var path in requiredDocs)
{
    if (!File.Exists(path))
    {
        failures.Add($"missing required doc: {path}");
    }
}

foreach (var (path, phrase) in requiredPhrases)
{
    if (!File.Exists(path))
    {
        failures.Add($"missing required phrase source: {path}");
        continue;
    }

    var text = File.ReadAllText(path);
    if (!text.Contains(phrase, StringComparison.Ordinal))
    {
        failures.Add($"{path} missing required phrase: {phrase}");
    }
}

foreach (var group in Labels.AllGroups)
{
    foreach (var label in group)
    {
        if (label.Wire.Any(char.IsUpper))
        {
            failures.Add($"{label.Domain} label is not lowercase wire format: {label.Wire}");
        }
    }
}

foreach (var path in quarantineScanDocs)
{
    if (!File.Exists(path))
    {
        continue;
    }

    var text = File.ReadAllText(path);
    foreach (var token in quarantinedTokens)
    {
        if (text.Contains(token, StringComparison.Ordinal))
        {
            failures.Add($"{path} contains quarantined token: {token}");
        }
    }
}

if (failures.Count == 0)
{
    Console.WriteLine("scaffold contract validation passed");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine($"validation failed: {failure}");
}

return 1;
