namespace FlashCore.Abstractions.Interfaces;

public interface ITransport : IDisposable
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);
}
