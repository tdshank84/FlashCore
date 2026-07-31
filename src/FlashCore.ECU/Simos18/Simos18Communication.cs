using System.Buffers.Binary;
using System.IO.Ports;
using FlashCore.Abstractions.Interfaces;
using FlashCore.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace FlashCore.ECU.Simos18;

/// <summary>
/// USB transport for a Macchina A0 running BridgeLEG USB-ISO-TP firmware.
/// BridgeLEG performs CAN and ISO-TP framing; this class exchanges its binary
/// packet format over the A0's CP210x serial port.
/// </summary>
public sealed class Simos18Communication : ITransport
{
    private const int BridgeBaudRate = 250_000;
    private const int HeaderLength = 8;
    private const int MaxPayloadLength = 65_535;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private SerialPort? _serialPort;
    private ushort _requestId = 0x7E0;
    private ushort _responseId = 0x7E8;
    private bool _disposed;

    public event EventHandler<string>? ErrorOccurred;
    public bool IsConnected => _serialPort?.IsOpen == true;

    public Simos18Communication(ILogger logger) => _logger = logger;

    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(parameters.PortName))
            throw new ArgumentException("A Macchina A0 serial port is required.", nameof(parameters));

        try
        {
            Disconnect();
            _requestId = GetCanId(parameters, "RequestId", 0x7E0);
            _responseId = GetCanId(parameters, "ResponseId", 0x7E8);

            _serialPort = new SerialPort(parameters.PortName, BridgeBaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 10_000,
                WriteTimeout = 10_000,
                DtrEnable = !OperatingSystem.IsWindows(),
                RtsEnable = !OperatingSystem.IsWindows()
            };
            _serialPort.Open();
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
            _logger.LogInformation(
                "Connected to Macchina A0 on {Port} (USB {Baud} baud, CAN {RequestId:X3}/{ResponseId:X3})",
                parameters.PortName, BridgeBaudRate, _requestId, _responseId);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Disconnect();
            _logger.LogError(ex, "Failed to connect to Macchina A0");
            ErrorOccurred?.Invoke(this, ex.Message);
            return Task.FromResult(false);
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public async Task<byte[]> SendUDSCommandAsync(
        byte[] command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0)
            throw new ArgumentException("A UDS command cannot be empty.", nameof(command));
        if (!IsConnected)
            throw new InvalidOperationException("Macchina A0 is not connected.");

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WritePacketAsync(0x00, command, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var response = await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
                if (response.Length >= 3 &&
                    response[0] == 0x7F &&
                    response[2] == 0x78)
                {
                    continue; // UDS response pending
                }

                ThrowIfNegativeResponse(command, response);
                return response;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Macchina A0 request failed");
            ErrorOccurred?.Invoke(this, ex.Message);
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default) =>
        SendUDSCommandAsync(request.ToArray(), cancellationToken);

    internal static void ThrowIfNegativeResponse(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (response.Length < 3 || response[0] != 0x7F)
            return;

        var service = response[1];
        var code = response[2];
        throw new UdsNegativeResponseException(service, code,
            $"UDS service 0x{service:X2} failed with NRC 0x{code:X2} ({DescribeNrc(code)})." +
            (request.Length > 0 && request[0] != service ? $" Requested service was 0x{request[0]:X2}." : string.Empty));
    }

    private static string DescribeNrc(byte code) => code switch
    {
        0x10 => "general reject",
        0x11 => "service not supported",
        0x12 => "sub-function not supported",
        0x13 => "incorrect message length or format",
        0x22 => "conditions not correct",
        0x24 => "request sequence error",
        0x31 => "request out of range",
        0x33 => "security access denied",
        0x35 => "invalid key",
        0x36 => "exceeded number of attempts",
        0x37 => "required time delay not expired",
        0x70 => "upload/download not accepted",
        0x71 => "transfer data suspended",
        0x72 => "general programming failure",
        0x73 => "wrong block sequence counter",
        _ => "unknown negative response"
    };

    /// <summary>Sets BridgeLEG TX separation time in microseconds.</summary>
    public async Task SetTransmitSeparationTimeAsync(
        ushort microseconds,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, microseconds);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WritePacketAsync(0x81, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task WritePacketAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload), "BridgeLEG payload exceeds 65535 bytes.");

        var packet = BuildPacket(command, _responseId, _requestId, payload.Span);
        await _serialPort!.BaseStream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await _serialPort.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] BuildPacket(
        byte command,
        ushort responseId,
        ushort requestId,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload));
        var packet = new byte[HeaderLength + payload.Length];
        packet[0] = 0xF1;
        packet[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), responseId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), requestId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }

    private async Task<byte[]> ReadPacketAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var header = new byte[HeaderLength];
        await ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);

        if (header.AsSpan().SequenceEqual("assertio"u8))
            throw new IOException("BridgeLEG firmware reported an assertion failure.");
        if (header[0] != 0xF1)
            throw new InvalidDataException($"Unexpected BridgeLEG packet marker 0x{header[0]:X2}.");

        var sender = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        var payload = new byte[length];
        await ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);

        if (sender != _responseId)
            throw new InvalidDataException(
                $"Received BridgeLEG packet from CAN ID 0x{sender:X3}; expected 0x{_responseId:X3}.");
        return payload;
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = await _serialPort!.BaseStream
                .ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("Macchina A0 disconnected while receiving data.");
            offset += count;
        }
    }

    private static ushort GetCanId(DeviceConnectionParams parameters, string key, ushort fallback)
    {
        if (parameters.CustomParams?.TryGetValue(key, out var value) != true)
            return fallback;
        return value switch
        {
            ushort id => id,
            int id when id is >= 0 and <= ushort.MaxValue => (ushort)id,
            uint id when id <= ushort.MaxValue => (ushort)id,
            string text when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                             ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) => hex,
            string text when ushort.TryParse(text, out var number) => number,
            _ => throw new ArgumentException($"Invalid {key} CAN identifier.")
        };
    }

    private void Disconnect()
    {
        if (_serialPort is null)
            return;
        try
        {
            if (_serialPort.IsOpen)
                _serialPort.Close();
        }
        finally
        {
            _serialPort.Dispose();
            _serialPort = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Disconnect();
        _requestLock.Dispose();
        _disposed = true;
    }
}

public sealed class UdsNegativeResponseException : DiagnosticNegativeResponseException
{
    public UdsNegativeResponseException(byte service, byte responseCode, string message)
        : base(service, responseCode, message) { }
}
