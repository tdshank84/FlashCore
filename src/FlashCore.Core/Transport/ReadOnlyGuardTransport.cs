using FlashCore.Abstractions.Interfaces;

namespace FlashCore.Core.Transport;

public sealed class ReadOnlyGuardTransport(
    ITransport inner,
    IReadOnlySet<byte>? allowedServices = null) : ITransport
{
    private readonly IReadOnlySet<byte> _allowed = allowedServices ??
        new HashSet<byte> { 0x10, 0x11, 0x22, 0x23, 0x3E };
    public bool IsConnected => inner.IsConnected;
    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default) =>
        inner.ConnectAsync(parameters, cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        if (request.IsEmpty || !_allowed.Contains(request.Span[0]))
            throw new InvalidOperationException(
                $"HIL read-only guard blocked UDS service {(request.IsEmpty ? "<empty>" : $"0x{request.Span[0]:X2}")}.");
        return inner.SendAsync(request, cancellationToken);
    }
    public void Dispose() => inner.Dispose();
}
