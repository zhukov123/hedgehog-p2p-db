namespace Hedgehog.Metadata.Core;

public sealed record MetadataError(string Code, string Message)
{
    public static MetadataError Validation(string message) => new("validation", message);

    public static MetadataError Conflict(string message) => new("conflict", message);

    public static MetadataError NotFound(string message) => new("not_found", message);
}

public sealed record MetadataResult<T>
{
    private MetadataResult(T? value, MetadataError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public MetadataError? Error { get; }

    public static MetadataResult<T> Ok(T value) => new(value, null);

    public static MetadataResult<T> Fail(MetadataError error) => new(default, error);
}
