using FlashCore.Abstractions.Models;

namespace FlashCore.Core.Planning;

public sealed record FlashPlanStep(int Sequence, FlashOperation Operation, string Description, uint? Address = null, uint? Size = null);

public sealed class FlashPlan
{
    public required string Id { get; init; }
    public required string TargetEcu { get; init; }
    public required string FileChecksum { get; init; }
    public required IReadOnlyList<FlashPlanStep> Steps { get; init; }

    public static FlashPlan Create(FlashFile file, string checksum)
    {
        ArgumentNullException.ThrowIfNull(file);
        var steps = new List<FlashPlanStep>
        {
            new(1, FlashOperation.PreFlash, "Validate target and flash file"),
            new(2, FlashOperation.SecurityAccess, "Obtain programming authorization"),
            new(3, FlashOperation.Erasing, "Erase target memory")
        };
        steps.AddRange(file.Blocks.Select((block, index) =>
            new FlashPlanStep(index + 4, FlashOperation.Programming,
                $"Program block {index + 1}", block.StartAddress, (uint)block.Data.Length)));
        steps.Add(new(steps.Count + 1, FlashOperation.Verifying, "Verify programmed data"));
        steps.Add(new(steps.Count + 1, FlashOperation.Finalizing, "Finalize and reset ECU"));

        return new FlashPlan
        {
            Id = Guid.NewGuid().ToString("N"),
            TargetEcu = file.TargetECU,
            FileChecksum = checksum,
            Steps = steps
        };
    }
}
