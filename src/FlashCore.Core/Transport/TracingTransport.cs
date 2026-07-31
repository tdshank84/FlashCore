using System.Text.Json;
using FlashCore.Abstractions.Interfaces;

namespace FlashCore.Core.Transport;

public sealed record TransportTraceEntry(
    DateTimeOffset Timestamp,
    string Direction,
    string Payload,
    string? Error = null);

public sealed record TransportTraceOptions
{
    public long MaximumFileBytes { get; init; } = 5 * 1024 * 1024;
    public bool CaptureSensitivePayloads { get; init; }
    public IReadOnlySet<byte> SensitiveServices { get; init; } = new HashSet<byte> { 0x27, 0x36 };
    public IReadOnlySet<ushort> SensitiveDataIdentifiers { get; init; } = new HashSet<ushort> { 0xF190 };
}

public sealed class TracingTransport(
    ITransport inner,
    string tracePath,
    TransportTraceOptions? options = null) : ITransport
{
    private readonly SemaphoreSlim _traceGate = new(1, 1);
    private readonly TransportTraceOptions _options = options ?? new();

    public bool IsConnected => inner.IsConnected;

    public Task<bool> ConnectAsync(DeviceConnectionParams parameters, CancellationToken cancellationToken = default) =>
        inner.ConnectAsync(parameters, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        inner.DisconnectAsync(cancellationToken);

    public async Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        var sensitiveExchange = IsSensitive(request.Span);
        await AppendAsync(new(DateTimeOffset.UtcNow, "request", FormatPayload(request.Span, sensitiveExchange)), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await AppendAsync(new(DateTimeOffset.UtcNow, "response", FormatPayload(response, sensitiveExchange)), cancellationToken)
                .ConfigureAwait(false);
            return response;
        }
        catch (Exception exception)
        {
            await AppendAsync(new(DateTimeOffset.UtcNow, "error", string.Empty, exception.Message), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private bool IsSensitive(ReadOnlySpan<byte> request)
    {
        if (request.IsEmpty) return false;
        if (_options.SensitiveServices.Contains(request[0])) return true;
        return request.Length >= 3 && request[0] == 0x22 &&
               _options.SensitiveDataIdentifiers.Contains((ushort)((request[1] << 8) | request[2]));
    }

    private async Task AppendAsync(TransportTraceEntry entry, CancellationToken cancellationToken)
    {
        await _traceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(tracePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            RotateIfNeeded();
            await File.AppendAllTextAsync(tracePath, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
            RestrictPermissions();
        }
        finally { _traceGate.Release(); }
    }

    private string FormatPayload(ReadOnlySpan<byte> payload, bool sensitiveExchange)
    {
        if (payload.IsEmpty) return string.Empty;
        if (!_options.CaptureSensitivePayloads && sensitiveExchange)
            return $"REDACTED:SERVICE=0x{payload[0]:X2}:LENGTH={payload.Length}";
        return Convert.ToHexString(payload);
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(tracePath);
        if (!file.Exists || file.Length < _options.MaximumFileBytes) return;
        var rotatedPath = tracePath + ".1";
        if (File.Exists(rotatedPath)) File.Delete(rotatedPath);
        File.Move(tracePath, rotatedPath);
    }

    private void RestrictPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(tracePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose()
    {
        inner.Dispose();
        _traceGate.Dispose();
    }
}
