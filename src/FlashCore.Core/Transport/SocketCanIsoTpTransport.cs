using System.Runtime.InteropServices;
using FlashCore.Abstractions.Interfaces;

namespace FlashCore.Core.Transport;

public sealed partial class SocketCanIsoTpTransport : ITransport
{
    private const int AddressFamilyCan = 29;
    private const int SocketDatagram = 2;
    private const int CanIsoTpProtocol = 6;
    private const int SolCanIsoTp = 106;
    private const int CanIsoTpTxStmin = 3;
    private int _socket = -1;
    private bool _disposed;
    public bool IsConnected => _socket >= 0;

    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("SocketCAN ISO-TP requires Linux.");
        if (string.IsNullOrWhiteSpace(parameters.PortName)) throw new ArgumentException("SocketCAN interface name is required.");
        var requestId = GetUInt(parameters, "RequestId", 0x7E0);
        var responseId = GetUInt(parameters, "ResponseId", 0x7E8);
        var interfaceIndex = Native.if_nametoindex(parameters.PortName);
        if (interfaceIndex == 0) throw new IOException($"SocketCAN interface '{parameters.PortName}' was not found.");
        _socket = Native.socket(AddressFamilyCan, SocketDatagram, CanIsoTpProtocol);
        if (_socket < 0) ThrowNative("Unable to create CAN_ISOTP socket.");
        try
        {
            var stmin = GetUInt(parameters, "StminTxNanoseconds", 0);
            if (stmin > 0 && Native.setsockopt(_socket, SolCanIsoTp, CanIsoTpTxStmin, ref stmin, sizeof(uint)) != 0)
                ThrowNative("Unable to configure CAN_ISOTP_TX_STMIN.");
            var address = new SockAddrCan
            {
                Family = AddressFamilyCan,
                InterfaceIndex = (int)interfaceIndex,
                ReceiveId = responseId,
                TransmitId = requestId
            };
            if (Native.bind(_socket, ref address, Marshal.SizeOf<SockAddrCan>()) != 0)
                ThrowNative("Unable to bind CAN_ISOTP socket.");
            return Task.FromResult(true);
        }
        catch
        {
            DisconnectNative();
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectNative();
        return Task.CompletedTask;
    }

    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("SocketCAN transport is not connected.");
        var payload = request.ToArray();
        if (Native.write(_socket, payload, (nuint)payload.Length) != payload.Length) ThrowNative("CAN_ISOTP write failed.");
        var response = new byte[65_535];
        var count = Native.read(_socket, response, (nuint)response.Length);
        if (count <= 0) ThrowNative("CAN_ISOTP read failed.");
        return Task.FromResult(response[..(int)count]);
    }

    private static uint GetUInt(DeviceConnectionParams parameters, string name, uint fallback) =>
        parameters.CustomParams?.TryGetValue(name, out var value) == true ? Convert.ToUInt32(value) : fallback;

    private static void ThrowNative(string message) =>
        throw new IOException($"{message} errno={Marshal.GetLastPInvokeError()}.");

    private void DisconnectNative()
    {
        if (_socket < 0) return;
        Native.close(_socket);
        _socket = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisconnectNative();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrCan
    {
        public ushort Family;
        private ushort _padding;
        public int InterfaceIndex;
        public uint ReceiveId;
        public uint TransmitId;
    }

    private static partial class Native
    {
        [LibraryImport("libc", SetLastError = true)] public static partial int socket(int domain, int type, int protocol);
        [LibraryImport("libc", SetLastError = true)] public static partial uint if_nametoindex([MarshalAs(UnmanagedType.LPStr)] string name);
        [LibraryImport("libc", SetLastError = true)] public static partial int bind(int socket, ref SockAddrCan address, int length);
        [LibraryImport("libc", SetLastError = true)] public static partial int setsockopt(int socket, int level, int option, ref uint value, int length);
        [LibraryImport("libc", SetLastError = true)] public static partial nint write(int file, byte[] buffer, nuint count);
        [LibraryImport("libc", SetLastError = true)] public static partial nint read(int file, byte[] buffer, nuint count);
        [LibraryImport("libc", SetLastError = true)] public static partial int close(int file);
    }
}
