using FlashCore.ECU.Simos18.Models;

namespace FlashCore.ECU.Simos18.Configuration;

public sealed record Simos18EcuProfile
{
    public required string Name { get; init; }
    public required string HardwareNumber { get; init; }
    public required string BootloaderIdentifier { get; init; }
    public required ushort EraseRoutineId { get; init; }
    public required uint LoaderAddress { get; init; }
    public required ushort SampleModeDid { get; init; }
    public required string UnlockLoaderPath { get; init; }
    public IReadOnlyList<string> SampleModeMarkers { get; init; } = ["X13", "X14"];
    public byte ProgrammingSession { get; init; } = 0x02;
    public byte SbootSeedSubFunction { get; init; } = 0x01;
    public byte SbootKeySubFunction { get; init; } = 0x02;
    public byte BootloaderSeedSubFunction { get; init; } = 0x03;
    public byte BootloaderKeySubFunction { get; init; } = 0x04;
    public byte ProgrammingSeedSubFunction { get; init; } = 0x11;
    public byte ProgrammingKeySubFunction { get; init; } = 0x12;
    public bool ProtocolValidated { get; init; }
    public decimal MinimumSupplyVoltage { get; init; } = 12.0m;
    public TimeSpan TesterPresentInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumTransferAttempts { get; init; } = 3;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Profile name is required.");
        if (string.IsNullOrWhiteSpace(HardwareNumber)) errors.Add("Exact hardware number is required.");
        if (string.IsNullOrWhiteSpace(BootloaderIdentifier)) errors.Add("Bootloader identifier is required.");
        if (EraseRoutineId == 0) errors.Add("A validated erase routine ID is required.");
        if (LoaderAddress == 0) errors.Add("A validated loader address is required.");
        if (SampleModeDid == 0) errors.Add("A validated sample-mode DID is required.");
        if (string.IsNullOrWhiteSpace(UnlockLoaderPath)) errors.Add("Unlock loader path is required.");
        if (SampleModeMarkers.Count == 0 || SampleModeMarkers.Any(string.IsNullOrWhiteSpace))
            errors.Add("At least one non-empty sample-mode marker is required.");
        if (ProgrammingSession == 0) errors.Add("Programming session must be configured.");
        ValidateSecurityLevel(errors, "SBOOT", SbootSeedSubFunction, SbootKeySubFunction);
        ValidateSecurityLevel(errors, "bootloader", BootloaderSeedSubFunction, BootloaderKeySubFunction);
        ValidateSecurityLevel(errors, "programming", ProgrammingSeedSubFunction, ProgrammingKeySubFunction);
        if (MinimumSupplyVoltage <= 0) errors.Add("Minimum supply voltage must be positive.");
        if (TesterPresentInterval <= TimeSpan.Zero) errors.Add("TesterPresent interval must be positive.");
        if (MaximumTransferAttempts < 1) errors.Add("At least one transfer attempt is required.");
        return errors;
    }

    private static void ValidateSecurityLevel(List<string> errors, string name, byte seed, byte key)
    {
        if (seed == 0 || key == 0 || seed == key)
            errors.Add($"Distinct {name} seed and key subfunctions are required.");
    }

    public IReadOnlyList<string> ValidateAgainst(Simos18ECUInfo ecu)
    {
        var errors = Validate().ToList();
        if (!string.Equals(HardwareNumber.Trim(), ecu.HardwareNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add($"Profile hardware '{HardwareNumber}' does not match ECU '{ecu.HardwareNumber}'.");
        if (!ecu.BootloaderIdentification.Contains(BootloaderIdentifier, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Profile bootloader '{BootloaderIdentifier}' does not match ECU '{ecu.BootloaderIdentification}'.");
        return errors;
    }
}
