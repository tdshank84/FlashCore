using FlashCore.Abstractions.Models;

namespace FlashCore.Core;

public sealed class OperationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<OperationResult> ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        int maxAttempts = 1,
        Func<Exception, bool>? isRetryable = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var startedAt = DateTimeOffset.UtcNow;
        bool entered;
        try
        {
            entered = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return OperationResult.Failure(operationName, startedAt,
                new OperationError(OperationErrorCode.Cancelled, "The operation was cancelled.", false, null, ex));
        }
        if (!entered)
            return OperationResult.Failure(operationName, startedAt,
                new OperationError(OperationErrorCode.Busy, "Another device operation is already running."));

        try
        {
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } limit) _activeCancellation.CancelAfter(limit);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await operation(_activeCancellation.Token).ConfigureAwait(false);
                    return OperationResult.Success(operationName, startedAt);
                }
                catch (OperationCanceledException ex)
                {
                    var timedOut = !cancellationToken.IsCancellationRequested &&
                                   _activeCancellation.IsCancellationRequested;
                    return OperationResult.Failure(operationName, startedAt,
                        new OperationError(timedOut ? OperationErrorCode.TimedOut : OperationErrorCode.Cancelled,
                            timedOut ? "The operation timed out." : "The operation was cancelled.", false, null, ex));
                }
                catch (Exception ex) when (attempt < Math.Max(1, maxAttempts) && isRetryable?.Invoke(ex) == true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), _activeCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return OperationResult.Failure(operationName, startedAt, Classify(ex));
                }
            }
        }
        finally
        {
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _gate.Release();
        }
    }

    public void Cancel() => _activeCancellation?.Cancel();

    private static OperationError Classify(Exception exception) => exception switch
    {
        DiagnosticNegativeResponseException response => new(OperationErrorCode.NegativeResponse,
            response.Message, IsRetryableNrc(response.ResponseCode), response.ResponseCode, response),
        TimeoutException => new(OperationErrorCode.TimedOut, exception.Message, true, null, exception),
        NotSupportedException => new(OperationErrorCode.Unsupported, exception.Message, false, null, exception),
        InvalidDataException => new(OperationErrorCode.ValidationFailed, exception.Message, false, null, exception),
        IOException => new(OperationErrorCode.TransportFailure, exception.Message, true, null, exception),
        _ => new(OperationErrorCode.Unexpected, exception.Message, false, null, exception)
    };

    private static bool IsRetryableNrc(byte code) => code is 0x21 or 0x37 or 0x71 or 0x78;

    public void Dispose()
    {
        if (_disposed) return;
        _activeCancellation?.Cancel();
        _activeCancellation?.Dispose();
        _disposed = true;
    }
}
