namespace Hedgehog.Metadata.Core;

public readonly record struct ObjectId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct VersionId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ReplicaId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ActorId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct NodeId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct RepairLeaseId(string Value)
{
    public override string ToString() => Value;
}
