using FlashCore.Abstractions.Interfaces;
using Microsoft.Extensions.Logging;

namespace FlashCore.ECU.Simos18;

public sealed class TesterPresentScheduler(
    ITransport transport,
    TimeSpan interval,
    ILogger logger,
    Action<Exception>? onFailure = null) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private Exception? _failure;

    public void Start(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) throw new InvalidOperationException("TesterPresent scheduler is already running.");
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
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
                var response = await transport.SendAsync(new byte[] { 0x3E, 0x00 }, cancellationToken)
                    .ConfigureAwait(false);
                if (response.Length < 2 || response[0] != 0x7E || response[1] != 0x00)
                    throw new IOException("ECU did not acknowledge the scheduled TesterPresent request.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _failure = exception;
            logger.LogError(exception, "Scheduled TesterPresent failed");
            onFailure?.Invoke(exception);
            _cancellation?.Cancel();
        }
    }

    public async Task StopAsync()
    {
        if (_loop is null) return;
        _cancellation?.Cancel();
        await _loop.ConfigureAwait(false);
        _loop = null;
        _cancellation?.Dispose();
        _cancellation = null;
        if (_failure is not null)
            throw new IOException("Scheduled TesterPresent failed.", _failure);
    }

    public void ThrowIfFailed()
    {
        if (_failure is not null) throw new IOException("Scheduled TesterPresent failed.", _failure);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
