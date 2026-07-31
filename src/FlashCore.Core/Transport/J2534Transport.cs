using FlashCore.Abstractions.Interfaces;

namespace FlashCore.Core.Transport;

public interface IJ2534Channel : IDisposable
{
    bool IsOpen { get; }
    void Open(string? deviceName, uint baudRate, uint transmitId, uint receiveId, uint stminMicroseconds);
    void Close();
    byte[] Request(ReadOnlySpan<byte> payload, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class J2534Transport(IJ2534Channel channel) : ITransport
{
    private bool _disposed;
    public bool IsConnected => channel.IsOpen;

    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var transmitId = GetUInt(parameters, "RequestId", 0x7E0);
        var receiveId = GetUInt(parameters, "ResponseId", 0x7E8);
        var stmin = GetUInt(parameters, "StminTxMicroseconds", 0);
        channel.Open(parameters.PortName, (uint)parameters.BaudRate, transmitId, receiveId, stmin);
        return Task.FromResult(channel.IsOpen);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        channel.Close();
        return Task.CompletedTask;
    }

    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("J2534 channel is not open.");
        return Task.FromResult(channel.Request(request.Span, TimeSpan.FromSeconds(10), cancellationToken));
    }

    private static uint GetUInt(DeviceConnectionParams parameters, string key, uint fallback) =>
        parameters.CustomParams?.TryGetValue(key, out var value) == true ? Convert.ToUInt32(value) : fallback;

    public void Dispose()
    {
        if (_disposed) return;
        channel.Dispose();
        _disposed = true;
    }
}
