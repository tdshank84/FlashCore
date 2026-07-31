using FlashCore.Abstractions.Models;
using FlashCore.Core.Checksums;

namespace FlashCore.Core.Validation;

public sealed record PreflightContext(
    string? ConnectedEcu = null,
    string? HardwareVersion = null,
    bool RequireVerifiedFile = true,
    uint MaximumAddress = uint.MaxValue);

public sealed record PreflightResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new InvalidDataException("Flash preflight failed: " + string.Join("; ", Errors));
    }
}

public sealed class FlashPreflightValidator(IChecksumService checksumService)
{
    public PreflightResult Validate(FlashFile file, PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (file.RawData.Length == 0 && file.Blocks.Count == 0)
            errors.Add("The flash file contains no data.");
        if (context.RequireVerifiedFile && !file.IsVerified)
            errors.Add("The flash file has not passed parser verification.");
        if (!string.IsNullOrWhiteSpace(file.TargetECU) && !string.IsNullOrWhiteSpace(context.ConnectedEcu) &&
            !context.ConnectedEcu.Contains(file.TargetECU, StringComparison.OrdinalIgnoreCase) &&
            !file.TargetECU.Contains(context.ConnectedEcu, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Target ECU '{file.TargetECU}' does not match connected ECU '{context.ConnectedEcu}'.");
        if (!string.IsNullOrWhiteSpace(file.TargetHW) && !string.IsNullOrWhiteSpace(context.HardwareVersion) &&
            !string.Equals(file.TargetHW, context.HardwareVersion, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Target hardware '{file.TargetHW}' does not match '{context.HardwareVersion}'.");

        var ordered = file.Blocks.OrderBy(block => block.StartAddress).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var block = ordered[index];
            if (block.Data.Length == 0) errors.Add($"Block {index + 1} is empty.");
            if (block.Size != 0 && block.Size != block.Data.Length)
                errors.Add($"Block {index + 1} declares {block.Size} bytes but contains {block.Data.Length}.");
            if (block.Data.Length > 0 && block.StartAddress > context.MaximumAddress - (uint)(block.Data.Length - 1))
                errors.Add($"Block {index + 1} exceeds the permitted address range.");
            if (index > 0)
            {
                var previous = ordered[index - 1];
                var previousEnd = previous.StartAddress + (uint)Math.Max(0, previous.Data.Length - 1);
                if (block.StartAddress <= previousEnd)
                    errors.Add($"Block at 0x{block.StartAddress:X8} overlaps the previous block.");
            }
            if (!string.IsNullOrWhiteSpace(block.Checksum) &&
                !checksumService.Verify(block.Data, block.Checksum, InferAlgorithm(block.Checksum)))
                errors.Add($"Block at 0x{block.StartAddress:X8} has an invalid checksum.");
        }

        if (string.IsNullOrWhiteSpace(file.Checksum))
            warnings.Add("No whole-file checksum is recorded.");

        return new(errors.Count == 0, errors, warnings);
    }

    private static ChecksumAlgorithm InferAlgorithm(string checksum) => checksum.Replace("-", string.Empty).Length switch
    {
        8 => ChecksumAlgorithm.Crc32,
        32 => ChecksumAlgorithm.Md5,
        _ => ChecksumAlgorithm.Sha256
    };
}
