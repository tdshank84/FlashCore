using FlashCore.Abstractions.Models;
using FlashCore.Core.Checksums;
using FlashCore.Core.Planning;
using FlashCore.Core.Validation;
using FlashCore.ECU.Simos18.Configuration;

namespace FlashCore.ECU.Simos18.Planning;

public sealed record Simos18DryRunReport(
    bool IsReady,
    string Profile,
    string FileSha256,
    long TotalBytes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    FlashPlan Plan);

public sealed class Simos18DryRunAnalyzer
{
    private readonly ChecksumService _checksums = new();

    public Simos18DryRunReport Analyze(FlashFile file, Simos18EcuProfile profile)
    {
        var profileErrors = profile.Validate().ToList();
        var preflight = new FlashPreflightValidator(_checksums).Validate(
            file, new PreflightContext("Simos18", profile.HardwareNumber));
        var checksum = _checksums.Calculate(file.RawData, ChecksumAlgorithm.Sha256);
        var plan = FlashPlan.Create(file, checksum);
        var errors = profileErrors.Concat(preflight.Errors).ToArray();
        return new(errors.Length == 0, profile.Name, checksum,
            file.Blocks.Sum(block => (long)block.Data.Length), errors, preflight.Warnings, plan);
    }
}
