using System.Buffers.Binary;
using System.Text;
using FlashCore.Abstractions.Interfaces;

namespace FlashCore.ECU.Simos18.Simulation;

public sealed class Simos18SimulationTransport : ITransport
{
    private readonly List<byte[]> _requests = [];
    private readonly Dictionary<uint, byte> _memory = [];
    private uint _downloadAddress;
    private int _downloadOffset;
    private bool _disposed;

    public bool IsConnected { get; private set; }
    public IReadOnlyList<byte[]> Requests => _requests;
    public int TransferredBytes { get; private set; }

    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("The simulated ECU is not connected.");
        if (request.IsEmpty) throw new ArgumentException("A UDS request cannot be empty.", nameof(request));

        var command = request.ToArray();
        _requests.Add(command);
        return Task.FromResult(CreateResponse(command));
    }

    private byte[] CreateResponse(ReadOnlySpan<byte> request) => request[0] switch
    {
        0x10 when request.Length >= 2 => [0x50, request[1]],
        0x11 when request.Length >= 2 => [0x51, request[1]],
        0x22 when request.Length >= 3 => ReadIdentifier(request[1], request[2]),
        0x23 when request.Length >= 10 => ReadMemory(request),
        0x27 when request.Length >= 2 => SecurityAccess(request[1]),
        0x31 when request.Length >= 4 => [0x71, request[1], request[2], request[3]],
        0x34 when request.Length >= 11 => BeginDownload(request),
        0x36 when request.Length >= 2 => TransferData(request),
        0x37 => [0x77],
        0x3E when request.Length >= 2 => [0x7E, request[1]],
        _ => [0x7F, request[0], 0x11]
    };

    private static byte[] SecurityAccess(byte subFunction) => subFunction switch
    {
        0x11 => [0x67, 0x11, 0x12, 0x34, 0x56, 0x78],
        _ when (subFunction & 1) == 1 => [0x67, subFunction, 0x12, 0x34],
        _ => [0x67, subFunction]
    };

    private byte[] BeginDownload(ReadOnlySpan<byte> request)
    {
        _downloadAddress = BinaryPrimitives.ReadUInt32BigEndian(request[3..7]);
        _downloadOffset = 0;
        return [0x74, 0x10, 0x80];
    }

    private byte[] TransferData(ReadOnlySpan<byte> request)
    {
        var data = request[2..];
        for (var index = 0; index < data.Length; index++)
            _memory[_downloadAddress + (uint)(_downloadOffset + index)] = data[index];
        _downloadOffset += data.Length;
        TransferredBytes += data.Length;
        return [0x76, request[1]];
    }

    private static byte[] ReadIdentifier(byte high, byte low)
    {
        var value = (high, low) switch
        {
            (0xF1, 0x90) => "SIMULATEDVIN00001",
            (0xF1, 0x91) => "SIMOS18-X13-SIM",
            (0xF1, 0x92) => "1.0.8-SIM",
            (0xF1, 0x93) => "X13-SIM",
            (0xF1, 0x94) => "Simos18 Simulator",
            (0xF1, 0xF4) => "SC8",
            _ => string.Empty
        };
        return [0x62, high, low, .. Encoding.ASCII.GetBytes(value)];
    }

    private byte[] ReadMemory(ReadOnlySpan<byte> request)
    {
        var address = BinaryPrimitives.ReadUInt32BigEndian(request[2..6]);
        var size = BinaryPrimitives.ReadUInt32BigEndian(request[6..10]);
        if (size > 0x1000) return [0x7F, 0x23, 0x31];
        var data = new byte[(int)size];
        for (var index = 0; index < data.Length; index++)
            if (_memory.TryGetValue(address + (uint)index, out var value)) data[index] = value;
        return [0x63, .. data];
    }

    public void Dispose()
    {
        if (_disposed) return;
        IsConnected = false;
        _disposed = true;
    }
}
