using FlashCore.Abstractions.Interfaces;
using FlashCore.ECU.Simos18.Models;

namespace FlashCore.ECU.Simos18.Configuration;

public interface ISupplyVoltageMonitor
{
    Task<decimal?> ReadVoltageAsync(CancellationToken cancellationToken = default);
}

public sealed class ConfiguredSupplyVoltageMonitor(DeviceConnectionParams parameters) : ISupplyVoltageMonitor
{
    public Task<decimal?> ReadVoltageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.CustomParams?.TryGetValue("SupplyVoltage", out var value) != true)
            return Task.FromResult<decimal?>(null);
        var voltage = value switch
        {
            decimal number => number,
            double number => (decimal)number,
            float number => (decimal)number,
            string text when decimal.TryParse(text, out var number) => number,
            _ => (decimal?)null
        };
        return Task.FromResult(voltage);
    }
}

public static class PhysicalExecutionPolicy
{
    public const string ConfirmationText = "ENABLE PHYSICAL ECU PROGRAMMING";

    public static async Task ValidateAsync(
        DeviceConnectionParams parameters,
        Simos18EcuProfile profile,
        Simos18ECUInfo ecu,
        ISupplyVoltageMonitor voltageMonitor,
        CancellationToken cancellationToken)
    {
        var errors = profile.ValidateAgainst(ecu).ToList();
        if (parameters.CustomParams?.TryGetValue("EnablePhysicalProgramming", out var enabled) != true || enabled is not true)
            errors.Add("Physical programming is not explicitly enabled.");
        if (parameters.CustomParams?.TryGetValue("SafetyConfirmation", out var confirmation) != true ||
            !string.Equals(confirmation as string, ConfirmationText, StringComparison.Ordinal))
            errors.Add("The exact physical-programming safety confirmation is missing.");
        var voltage = await voltageMonitor.ReadVoltageAsync(cancellationToken).ConfigureAwait(false);
        if (voltage is null)
            errors.Add("Supply voltage is unavailable; programming is fail-closed.");
        else if (voltage < profile.MinimumSupplyVoltage)
            errors.Add($"Supply voltage {voltage:F2} V is below the required {profile.MinimumSupplyVoltage:F2} V.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Physical execution policy rejected programming: " + string.Join("; ", errors));
    }
}
