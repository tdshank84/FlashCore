using System.Security.Cryptography;
using System.Text.Json;

namespace FlashCore.ECU.Simos18.Configuration;

public sealed record SignedSimos18Profile(JsonElement Profile, string? Signature = null);

public sealed class Simos18ProfileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<Simos18EcuProfile> LoadAsync(
        string path,
        bool requireSignature = false,
        string? trustedPublicKeyPem = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var envelope = await JsonSerializer.DeserializeAsync<SignedSimos18Profile>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Profile JSON is empty.");
        var profile = envelope.Profile.Deserialize<Simos18EcuProfile>(JsonOptions)
            ?? throw new InvalidDataException("Profile payload is missing.");
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid ECU profile: " + string.Join("; ", errors));

        if (requireSignature || !string.IsNullOrWhiteSpace(envelope.Signature))
        {
            if (string.IsNullOrWhiteSpace(envelope.Signature) || string.IsNullOrWhiteSpace(trustedPublicKeyPem))
                throw new CryptographicException("A signed profile and trusted RSA public key are required.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(trustedPublicKeyPem);
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Profile, JsonOptions);
            if (!rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException("ECU profile signature is invalid.");
        }
        return profile;
    }

    public async Task SaveAsync(
        string path,
        Simos18EcuProfile profile,
        RSA? signingKey = null,
        CancellationToken cancellationToken = default)
    {
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid ECU profile: " + string.Join("; ", errors));
        var element = JsonSerializer.SerializeToElement(profile, JsonOptions);
        var payload = JsonSerializer.SerializeToUtf8Bytes(element, JsonOptions);
        var signature = signingKey is null ? null : Convert.ToBase64String(
            signingKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new SignedSimos18Profile(element, signature), JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }
}
