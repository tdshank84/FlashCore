using System.Text.Json;
using FlashCore.Abstractions.Interfaces;

namespace FlashCore.Core.Transport;

public sealed record TranscriptExchange(string Request, string Response);

public sealed class TranscriptReplayTransport(IReadOnlyList<TranscriptExchange> exchanges) : ITransport
{
    private int _position;
    private bool _disposed;
    public bool IsConnected { get; private set; }
    public int Remaining => exchanges.Count - _position;

    public static async Task<TranscriptReplayTransport> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var entries = await JsonSerializer.DeserializeAsync<List<TranscriptExchange>>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Transcript is empty.");
        return new(entries);
    }

    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("Transcript transport is not connected.");
        if (_position >= exchanges.Count) throw new EndOfStreamException("Transcript has no remaining exchanges.");
        var expected = exchanges[_position++];
        var actual = Convert.ToHexString(request.Span);
        if (!string.Equals(actual, expected.Request, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Transcript mismatch at exchange {_position}: expected {expected.Request}, received {actual}.");
        return Task.FromResult(Convert.FromHexString(expected.Response));
    }

    public void Dispose()
    {
        IsConnected = false;
        _disposed = true;
    }
}
