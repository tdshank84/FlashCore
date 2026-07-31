using Microsoft.Extensions.Logging;

namespace FlashCore.ECU.Simos18.Configuration;

public sealed class SupplyVoltageSupervisor(
    ISupplyVoltageMonitor monitor,
    decimal minimumVoltage,
    TimeSpan interval,
    ILogger logger,
    Action<Exception>? onFailure = null) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private Exception? _failure;

    public void Start(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) throw new InvalidOperationException("Voltage supervisor is already running.");
        if (minimumVoltage <= 0 || interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumVoltage));
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cancellation.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var voltage = await monitor.ReadVoltageAsync(cancellationToken).ConfigureAwait(false);
                if (voltage is null) throw new IOException("Supply voltage became unavailable.");
                if (voltage < minimumVoltage)
                    throw new IOException($"Supply voltage dropped to {voltage:F2} V; minimum is {minimumVoltage:F2} V.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _failure = exception;
            logger.LogError(exception, "Supply voltage supervision failed");
            onFailure?.Invoke(exception);
            _cancellation?.Cancel();
        }
    }

    public void ThrowIfFailed()
    {
        if (_failure is not null) throw new IOException("Supply voltage supervision failed.", _failure);
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;
        _cancellation?.Cancel();
        await _loop.ConfigureAwait(false);
        _loop = null;
        _cancellation?.Dispose();
        _cancellation = null;
        ThrowIfFailed();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
