namespace FlashCore.ECU.QuickApps;

public enum QuickAppCompatibilityStatus { Supported, Unsupported, Unverified }

public sealed record QuickAppCompatibilityResult(
    string ProfileId,
    string Name,
    QuickAppCompatibilityStatus Status,
    IReadOnlyList<string> Reasons);

public static class QuickAppCompatibilityScanner
{
    public static QuickAppCompatibilityResult Evaluate(QuickAppProfile profile, VehicleFingerprint vehicle)
    {
        var unsupported = new List<string>();
        var unverified = new List<string>();
        if (vehicle.ModelYear is null) unverified.Add("Vehicle model year is unknown.");
        else if (vehicle.ModelYear < profile.FirstModelYear || vehicle.ModelYear > profile.LastModelYear)
            unsupported.Add($"Model year {vehicle.ModelYear} is outside {profile.FirstModelYear}-{profile.LastModelYear}.");
        if (!string.Equals(vehicle.Market, profile.Market, StringComparison.OrdinalIgnoreCase))
            unsupported.Add($"Vehicle market '{vehicle.Market}' does not match '{profile.Market}'.");
        foreach (var equipment in profile.RequiredEquipment.Where(equipment =>
                     !vehicle.InstalledEquipment.Contains(equipment, StringComparer.OrdinalIgnoreCase)))
            unsupported.Add($"Required equipment is missing: {equipment}.");

        foreach (var requirement in profile.ControlUnits)
        {
            var unit = vehicle.FindUnit(requirement.Address);
            if (unit is null)
            {
                unsupported.Add($"Control unit {requirement.Address} is not installed.");
                continue;
            }
            MatchRequirement(requirement.PartNumbers, unit.PartNumber, "part number", requirement.Address, unsupported, unverified);
            MatchRequirement(requirement.SoftwareVersions, unit.SoftwareVersion, "software version", requirement.Address, unsupported, unverified);
            MatchRequirement(requirement.OdxIdentifiers, unit.OdxIdentifier, "ODX identifier", requirement.Address, unsupported, unverified);
        }

        if (!profile.ExecutionEnabled) unverified.Add("Profile has not been enabled by independent validation.");
        var status = unsupported.Count > 0 ? QuickAppCompatibilityStatus.Unsupported :
            unverified.Count > 0 ? QuickAppCompatibilityStatus.Unverified : QuickAppCompatibilityStatus.Supported;
        return new(profile.Id, profile.Name, status, [.. unsupported, .. unverified]);
    }

    private static void MatchRequirement(
        IReadOnlyList<string> accepted,
        string? actual,
        string label,
        string address,
        ICollection<string> unsupported,
        ICollection<string> unverified)
    {
        if (accepted.Count == 0) return;
        if (string.IsNullOrWhiteSpace(actual)) unverified.Add($"Control unit {address} {label} is unknown.");
        else if (!accepted.Contains(actual, StringComparer.OrdinalIgnoreCase))
            unsupported.Add($"Control unit {address} {label} '{actual}' is not approved.");
    }
}

public sealed record QuickAppDryRunChange(
    string ControlUnitAddress,
    QuickAppChangeKind Kind,
    string Channel,
    string ExpectedValue,
    string DesiredValue,
    string RollbackValue);

public sealed record QuickAppDryRunReport(
    string ProfileId,
    string Name,
    QuickAppRisk Risk,
    QuickAppCompatibilityStatus Compatibility,
    bool IsExecutable,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<QuickAppDryRunChange> Changes);

public static class QuickAppDryRunAnalyzer
{
    public static QuickAppDryRunReport Analyze(QuickAppProfile profile, VehicleFingerprint vehicle)
    {
        var compatibility = QuickAppCompatibilityScanner.Evaluate(profile, vehicle);
        var blockers = compatibility.Reasons.ToList();
        if (profile.Risk == QuickAppRisk.Restricted) blockers.Add("Restricted functions are disabled by policy.");
        return new(profile.Id, profile.Name, profile.Risk, compatibility.Status,
            compatibility.Status == QuickAppCompatibilityStatus.Supported && profile.ExecutionEnabled && blockers.Count == 0,
            blockers,
            profile.Changes.Select(change => new QuickAppDryRunChange(
                change.ControlUnitAddress, change.Kind, change.Channel,
                change.ExpectedValue, change.DesiredValue, change.RollbackValue)).ToArray());
    }
}
