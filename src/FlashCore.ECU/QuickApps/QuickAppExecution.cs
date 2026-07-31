namespace FlashCore.ECU.QuickApps;

public sealed record QuickAppExecutionAuthorization(
    bool ProfileSignatureVerified,
    bool BenchValidationAcknowledged,
    bool RecoveryPackageCreated,
    string ConfirmationText);

public sealed record QuickAppValueSnapshot(
    string ControlUnitAddress,
    QuickAppChangeKind Kind,
    string Channel,
    string Before,
    string? After,
    bool Restored);

public sealed record QuickAppExecutionReport(
    bool Succeeded,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    IReadOnlyList<QuickAppValueSnapshot> Changes,
    string Message);

public interface IQuickAppControlUnitClient
{
    Task<string> ReadValueAsync(
        string controlUnitAddress,
        QuickAppChangeKind kind,
        string channel,
        CancellationToken cancellationToken = default);
    Task WriteValueAsync(
        string controlUnitAddress,
        QuickAppChangeKind kind,
        string channel,
        string value,
        string? securityAccess,
        CancellationToken cancellationToken = default);
}

public static class QuickAppRiskPolicy
{
    public const string ConfirmationText = "APPLY VALIDATED QUICK APP";

    public static IReadOnlyList<string> Validate(
        QuickAppProfile profile,
        QuickAppCompatibilityResult compatibility,
        QuickAppExecutionAuthorization authorization)
    {
        var errors = new List<string>();
        if (!profile.ExecutionEnabled) errors.Add("Profile execution is disabled.");
        if (profile.Risk == QuickAppRisk.Restricted) errors.Add("Restricted functions cannot be executed.");
        if (compatibility.Status != QuickAppCompatibilityStatus.Supported)
            errors.Add("Vehicle compatibility is not fully supported.");
        if (!authorization.ProfileSignatureVerified) errors.Add("Profile signature has not been verified.");
        if (!authorization.RecoveryPackageCreated) errors.Add("A recovery package is required.");
        if (!string.Equals(authorization.ConfirmationText, ConfirmationText, StringComparison.Ordinal))
            errors.Add($"Confirmation must exactly match '{ConfirmationText}'.");
        if (profile.Risk is QuickAppRisk.Powertrain or QuickAppRisk.SafetyCritical &&
            !authorization.BenchValidationAcknowledged)
            errors.Add("Powertrain and safety-critical changes require explicit bench-validation acknowledgement.");
        return errors;
    }
}

public sealed class QuickAppTransactionEngine(IQuickAppControlUnitClient client)
{
    public async Task<QuickAppExecutionReport> ExecuteAsync(
        QuickAppProfile profile,
        VehicleFingerprint vehicle,
        QuickAppExecutionAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        var compatibility = QuickAppCompatibilityScanner.Evaluate(profile, vehicle);
        var policyErrors = QuickAppRiskPolicy.Validate(profile, compatibility, authorization);
        if (policyErrors.Count > 0)
            return new(false, false, false, [], "Execution blocked: " + string.Join("; ", policyErrors));

        var snapshots = new List<QuickAppValueSnapshot>();
        try
        {
            foreach (var change in profile.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = await client.ReadValueAsync(change.ControlUnitAddress, change.Kind, change.Channel, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(before, change.ExpectedValue, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Precondition failed for {change.ControlUnitAddress}/{change.Channel}: expected '{change.ExpectedValue}', read '{before}'.");
                snapshots.Add(new(change.ControlUnitAddress, change.Kind, change.Channel, before, null, false));
                await client.WriteValueAsync(change.ControlUnitAddress, change.Kind, change.Channel,
                    change.DesiredValue, change.SecurityAccess, cancellationToken).ConfigureAwait(false);
                var after = await client.ReadValueAsync(change.ControlUnitAddress, change.Kind, change.Channel, cancellationToken)
                    .ConfigureAwait(false);
                snapshots[^1] = snapshots[^1] with { After = after };
                if (!string.Equals(after, change.DesiredValue, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Post-write verification failed for {change.ControlUnitAddress}/{change.Channel}.");
            }
            return new(true, false, true, snapshots, "All changes were verified.");
        }
        catch (Exception exception)
        {
            var rollbackSucceeded = await RollbackAsync(profile, snapshots, CancellationToken.None).ConfigureAwait(false);
            return new(false, snapshots.Count > 0, rollbackSucceeded, snapshots,
                $"Transaction failed: {exception.Message}; rollback {(rollbackSucceeded ? "completed" : "failed")}.");
        }
    }

    private async Task<bool> RollbackAsync(
        QuickAppProfile profile,
        IList<QuickAppValueSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        for (var index = snapshots.Count - 1; index >= 0; index--)
        {
            var snapshot = snapshots[index];
            var change = profile.Changes.First(item => item.ControlUnitAddress == snapshot.ControlUnitAddress &&
                item.Kind == snapshot.Kind && item.Channel == snapshot.Channel);
            try
            {
                await client.WriteValueAsync(snapshot.ControlUnitAddress, snapshot.Kind, snapshot.Channel,
                    snapshot.Before, change.SecurityAccess, cancellationToken).ConfigureAwait(false);
                var restored = await client.ReadValueAsync(snapshot.ControlUnitAddress, snapshot.Kind, snapshot.Channel, cancellationToken)
                    .ConfigureAwait(false);
                var verified = string.Equals(restored, snapshot.Before, StringComparison.Ordinal);
                snapshots[index] = snapshot with { After = restored, Restored = verified };
                succeeded &= verified;
            }
            catch { succeeded = false; }
        }
        return succeeded;
    }
}

public sealed record QuickAppComparison(
    IReadOnlyList<QuickAppValueSnapshot> Changed,
    IReadOnlyList<QuickAppValueSnapshot> Restored,
    IReadOnlyList<QuickAppValueSnapshot> Unverified);

public static class QuickAppComparisonReport
{
    public static QuickAppComparison Create(QuickAppExecutionReport report) => new(
        report.Changes.Where(item => !item.Restored && item.After is not null && item.Before != item.After).ToArray(),
        report.Changes.Where(item => item.Restored).ToArray(),
        report.Changes.Where(item => item.After is null).ToArray());
}
