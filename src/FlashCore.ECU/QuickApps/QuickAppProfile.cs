using System.Security.Cryptography;
using System.Text.Json;

namespace FlashCore.ECU.QuickApps;

public enum QuickAppRisk { Cosmetic, Comfort, Workshop, Powertrain, SafetyCritical, Restricted }
public enum QuickAppChangeKind { Adaptation, LongCoding, BasicSetting }
public enum ResearchConfidence { Unverified, Corroborated, ModuleMatched, BenchValidated }

public sealed record ControlUnitRequirement(
    string Address,
    IReadOnlyList<string> PartNumbers,
    IReadOnlyList<string> SoftwareVersions,
    IReadOnlyList<string> OdxIdentifiers);

public sealed record QuickAppChange(
    string ControlUnitAddress,
    QuickAppChangeKind Kind,
    string Channel,
    string ExpectedValue,
    string DesiredValue,
    string RollbackValue,
    string? SecurityAccess = null);

public sealed record ValidationEvidence(
    string Source,
    ResearchConfidence Confidence,
    string? ModulePartNumber,
    string? SoftwareVersion,
    string Notes);

public sealed record QuickAppProfile(
    string Id,
    string Name,
    string Vehicle,
    string Market,
    int FirstModelYear,
    int LastModelYear,
    QuickAppRisk Risk,
    bool ExecutionEnabled,
    IReadOnlyList<string> RequiredEquipment,
    IReadOnlyList<ControlUnitRequirement> ControlUnits,
    IReadOnlyList<QuickAppChange> Changes,
    IReadOnlyList<ValidationEvidence> Evidence)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) errors.Add("Profile ID and name are required.");
        if (FirstModelYear > LastModelYear) errors.Add("Model-year range is reversed.");
        if (Changes.Count == 0) errors.Add("At least one change is required.");
        foreach (var change in Changes)
        {
            if (string.IsNullOrWhiteSpace(change.Channel)) errors.Add("Change channel cannot be empty.");
            if (string.IsNullOrWhiteSpace(change.ExpectedValue)) errors.Add($"{change.Channel} has no expected value.");
            if (string.IsNullOrWhiteSpace(change.RollbackValue)) errors.Add($"{change.Channel} has no rollback value.");
            if (!string.Equals(change.ExpectedValue, change.RollbackValue, StringComparison.Ordinal))
                errors.Add($"{change.Channel} rollback value must equal the validated original value.");
        }
        if (ExecutionEnabled && !Evidence.Any(item => item.Confidence == ResearchConfidence.BenchValidated))
            errors.Add("Executable profiles require bench-validated evidence.");
        if (Risk == QuickAppRisk.Restricted && ExecutionEnabled) errors.Add("Restricted profiles cannot be executable.");
        return errors;
    }
}

public sealed record SignedQuickAppProfile(JsonElement Profile, string? Signature);

public static class QuickAppProfileLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<QuickAppProfile> LoadAsync(
        string path,
        bool requireSignature,
        string? trustedPublicKeyPem = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var envelope = await JsonSerializer.DeserializeAsync<SignedQuickAppProfile>(stream, Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Quick App profile is empty.");
        if (requireSignature)
        {
            if (string.IsNullOrWhiteSpace(envelope.Signature) || string.IsNullOrWhiteSpace(trustedPublicKeyPem))
                throw new CryptographicException("A signature and trusted public key are required.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(trustedPublicKeyPem);
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Profile, Options);
            if (!rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("Quick App profile signature is invalid.");
        }
        var profile = envelope.Profile.Deserialize<QuickAppProfile>(Options) ??
            throw new InvalidDataException("Quick App profile payload is invalid.");
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid Quick App profile: " + string.Join("; ", errors));
        return profile;
    }

    public static async Task SaveSignedAsync(
        string path,
        QuickAppProfile profile,
        RSA signingKey,
        CancellationToken cancellationToken = default)
    {
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid Quick App profile: " + string.Join("; ", errors));
        var element = JsonSerializer.SerializeToElement(profile, Options);
        var payload = JsonSerializer.SerializeToUtf8Bytes(element, Options);
        var signature = Convert.ToBase64String(signingKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new SignedQuickAppProfile(element, signature), Options), cancellationToken)
            .ConfigureAwait(false);
    }
}
