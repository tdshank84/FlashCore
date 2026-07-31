namespace FlashCore.Abstractions.Models;

public enum DeviceState
{
    Disconnected,
    Connecting,
    Connected,
    Identified,
    ProgrammingSession,
    SecurityUnlocked,
    Erasing,
    Programming,
    Verifying,
    Finalizing,
    Faulted,
    Disposed
}

public enum OperationErrorCode
{
    None,
    Busy,
    Cancelled,
    TimedOut,
    NotConnected,
    InvalidState,
    ValidationFailed,
    TransportFailure,
    NegativeResponse,
    VerificationFailed,
    Unsupported,
    Unexpected
}

public sealed record OperationError(
    OperationErrorCode Code,
    string Message,
    bool IsRetryable = false,
    byte? NegativeResponseCode = null,
    Exception? Exception = null);

public sealed record OperationResult(
    string OperationName,
    bool IsSuccess,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    OperationError? Error = null)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    public static OperationResult Success(string name, DateTimeOffset startedAt) =>
        new(name, true, startedAt, DateTimeOffset.UtcNow);

    public static OperationResult Failure(
        string name,
        DateTimeOffset startedAt,
        OperationError error) =>
        new(name, false, startedAt, DateTimeOffset.UtcNow, error);
}

public sealed class OperationCompletedEventArgs(OperationResult result) : EventArgs
{
    public OperationResult Result { get; } = result;
}

public class DiagnosticNegativeResponseException : IOException
{
    public byte Service { get; }
    public byte ResponseCode { get; }

    public DiagnosticNegativeResponseException(byte service, byte responseCode, string message) : base(message)
    {
        Service = service;
        ResponseCode = responseCode;
    }
}
