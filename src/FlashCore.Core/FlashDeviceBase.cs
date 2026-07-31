using FlashCore.Abstractions.Interfaces;
using FlashCore.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace FlashCore.Core;

public abstract class FlashDeviceBase : IFlashDevice
{
    protected readonly ILogger _logger;
    protected bool _isConnected;
    protected bool _disposed;
    protected DeviceCapabilities _capabilities = new();
    private readonly OperationCoordinator _operationCoordinator = new();
    private readonly DeviceStateMachine _stateMachine = new();

    public event EventHandler<FlashProgress>? ProgressUpdated;
    public event EventHandler<string>? StatusUpdated;
    public event EventHandler<OperationCompletedEventArgs>? OperationCompleted;
    public bool IsConnected => _isConnected;
    public bool IsBusy => _operationCoordinator.IsBusy;
    public DeviceState State => _stateMachine.State;
    public OperationResult? LastOperationResult { get; private set; }
    public virtual DeviceCapabilities Capabilities => _capabilities;

    protected FlashDeviceBase(ILogger logger)
    {
        _logger = logger;
        InitializeCapabilities();
    }

    protected virtual void InitializeCapabilities()
    {
        _capabilities.SupportsUDS = true;
        _capabilities.MaxPacketSize = 4096;
    }

    public abstract Task<bool> ConnectAsync(DeviceConnectionParams parameters);
    public abstract Task DisconnectAsync();
    public abstract Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default);
    public abstract Task<bool> FlashAsync(FlashFile file, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task<bool> VerifyAsync(FlashFile file, IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task<byte[]> ReadMemoryAsync(uint address, uint size, CancellationToken cancellationToken = default);
    public abstract Task<bool> WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default);
    public abstract Task<bool> SecurityAccessAsync(SecurityAccessType type, CancellationToken cancellationToken = default);
    public abstract Task<bool> DiagnosticSessionControlAsync(DiagnosticSessionType session, CancellationToken cancellationToken = default);

    protected virtual void OnProgressUpdated(FlashProgress progress) => ProgressUpdated?.Invoke(this, progress);
    protected virtual void OnStatusUpdated(string status) => StatusUpdated?.Invoke(this, status);
    protected void TransitionTo(DeviceState state) => _stateMachine.TransitionTo(state);
    public void CancelCurrentOperation() => _operationCoordinator.Cancel();

    protected async Task<bool> ExecuteOperationAsync(Func<Task> operation, string operationName, CancellationToken cancellationToken)
        => await ExecuteOperationAsync(_ => operation(), operationName, cancellationToken).ConfigureAwait(false);

    protected async Task<bool> ExecuteOperationAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        int maxAttempts = 1,
        Func<Exception, bool>? isRetryable = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var progress = new FlashProgress { OperationName = operationName, CurrentOperation = FlashOperation.Connecting };
        OnProgressUpdated(progress);

        var result = await _operationCoordinator.ExecuteAsync(
            operationName, operation, cancellationToken, timeout, maxAttempts, isRetryable).ConfigureAwait(false);
        LastOperationResult = result;
        OperationCompleted?.Invoke(this, new OperationCompletedEventArgs(result));

        if (result.IsSuccess) return true;
        if (result.Error?.Code == OperationErrorCode.Busy)
            throw new InvalidOperationException(result.Error.Message);
        if (result.Error?.Code is OperationErrorCode.Cancelled or OperationErrorCode.TimedOut)
            throw new OperationCanceledException(result.Error.Message, result.Error.Exception, cancellationToken);
        if (result.Error?.Exception is { } exception)
        {
            _stateMachine.ForceFaulted();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        }
        throw new InvalidOperationException(result.Error?.Message ?? $"{operationName} failed.");
    }

    public virtual void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _operationCoordinator.Dispose();
            _stateMachine.TransitionTo(DeviceState.Disposed);
        }
        _disposed = true;
    }
}
