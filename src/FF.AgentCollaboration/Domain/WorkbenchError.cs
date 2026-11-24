namespace FF.AgentCollaboration.Domain;

internal enum WorkbenchFailure
{
    InvalidInput,
    Forbidden,
    NotFound,
    Conflict,
    Unavailable
}

internal sealed class WorkbenchError(string message, WorkbenchFailure failure = WorkbenchFailure.InvalidInput) : Exception(message)
{
    internal WorkbenchFailure Failure { get; } = failure;
}
