namespace LenxTool.Core.Errors;

public enum AppErrorCode
{
    Unknown,
    InvalidRequest,
    Conflict,
    CredentialsInvalid,
    AccessDenied,
    ProviderRateLimited,
    ProviderUnavailable,
    Timeout,
    NetworkUnavailable,
    OperationCancelled,
    FileNotFound,
    FileAccessDenied,
    DatabaseCorrupted,
    DatabaseMigrationFailed,
    UpdateVerificationFailed
}

public sealed record AppError(
    AppErrorCode Code,
    string Title,
    string UserMessage,
    string Suggestion,
    string? TechnicalDetails = null,
    string? Provider = null,
    string? RequestId = null,
    TimeSpan? RetryAfter = null,
    bool IsRetryable = false);

public sealed class AppException(AppError error, Exception? innerException = null)
    : Exception(error.UserMessage, innerException)
{
    public AppError Error { get; } = error;
}
