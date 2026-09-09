namespace Snook.Domain;

public enum SnookErrorCode
{
    ValidationFailed,
    NotFound,
    InvalidTransition,
    RevisionConflict,
    DependencyBlocked,
    ConcurrencyPolicyBlocked,
    StoreUnavailable,
    SchemaIncompatible,
    InternalError
}

public sealed class SnookException : Exception
{
    public SnookException(SnookErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public SnookException(SnookErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public SnookErrorCode Code { get; }
}
