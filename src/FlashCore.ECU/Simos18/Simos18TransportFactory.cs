using FlashCore.Abstractions.Interfaces;
using FlashCore.Core.Transport;
using FlashCore.ECU.Simos18.Simulation;
using Microsoft.Extensions.Logging;

namespace FlashCore.ECU.Simos18;

public enum Simos18TransportKind { Auto, BridgeLeg, SocketCan, J2534, Replay, Simulation }

public sealed class Simos18TransportFactory(ILogger logger)
{
    public async Task<ITransport> CreateAsync(
        DeviceConnectionParams parameters,
        CancellationToken cancellationToken = default)
    {
        var kind = ResolveKind(parameters);
        return kind switch
        {
            Simos18TransportKind.BridgeLeg => new Simos18Communication(logger),
            Simos18TransportKind.SocketCan => new SocketCanIsoTpTransport(),
            Simos18TransportKind.J2534 => new J2534Transport(GetJ2534Channel(parameters)),
            Simos18TransportKind.Replay => await TranscriptReplayTransport.LoadAsync(
                GetString(parameters, "ReplayPath"), cancellationToken).ConfigureAwait(false),
            Simos18TransportKind.Simulation => new Simos18SimulationTransport(),
            _ => throw new InvalidOperationException($"Unsupported transport kind: {kind}.")
        };
    }

    public static Simos18TransportKind ResolveKind(DeviceConnectionParams parameters)
    {
        if (parameters.CustomParams?.TryGetValue("TransportKind", out var configured) == true)
        {
            if (configured is Simos18TransportKind kind && kind != Simos18TransportKind.Auto) return kind;
            if (Enum.TryParse<Simos18TransportKind>(Convert.ToString(configured), true, out kind) &&
                kind != Simos18TransportKind.Auto) return kind;
        }
        if (parameters.CustomParams?.ContainsKey("ReplayPath") == true) return Simos18TransportKind.Replay;
        if (parameters.CustomParams?.TryGetValue("SimulationMode", out var simulation) == true && simulation is true)
            return Simos18TransportKind.Simulation;
        if (parameters.CustomParams?.ContainsKey("J2534Channel") == true) return Simos18TransportKind.J2534;
        if (OperatingSystem.IsLinux() && parameters.PortName.StartsWith("can", StringComparison.OrdinalIgnoreCase))
            return Simos18TransportKind.SocketCan;
        return Simos18TransportKind.BridgeLeg;
    }

    private static IJ2534Channel GetJ2534Channel(DeviceConnectionParams parameters) =>
        parameters.CustomParams?.TryGetValue("J2534Channel", out var channel) == true && channel is IJ2534Channel typed
            ? typed
            : throw new InvalidOperationException("J2534 transport requires an IJ2534Channel adapter.");

    private static string GetString(DeviceConnectionParams parameters, string key) =>
        parameters.CustomParams?.TryGetValue(key, out var value) == true && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException($"Transport option '{key}' is required.");
}
