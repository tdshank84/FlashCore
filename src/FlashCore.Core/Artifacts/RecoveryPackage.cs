using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlashCore.Core.Artifacts;

public sealed record RecoveryArtifact(string Kind, string Path);
public sealed record RecoveryPackageRequest(
    string EcuIdentifier,
    string FirmwareChecksum,
    IReadOnlyList<RecoveryArtifact> Artifacts);
public sealed record RecoveryPackageEntry(string Kind, string FileName, long Length, string Sha256);
public sealed record RecoveryPackageManifest(
    string Format,
    string FlashCoreVersion,
    DateTimeOffset CreatedUtc,
    string EcuIdentifier,
    string FirmwareChecksum,
    IReadOnlyList<RecoveryPackageEntry> Entries);

public static class RecoveryPackage
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ecu-info", "flash-plan", "trace", "profile", "journal", "checksums"
    };

    public static async Task<RecoveryPackageManifest> CreateAsync(
        string outputPath,
        RecoveryPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var duplicateNames = request.Artifacts.GroupBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNames is not null) throw new InvalidDataException($"Duplicate artifact name: {duplicateNames.Key}.");

        var entries = new List<RecoveryPackageEntry>();
        foreach (var artifact in request.Artifacts)
        {
            if (!AllowedKinds.Contains(artifact.Kind)) throw new InvalidDataException($"Unsupported artifact kind: {artifact.Kind}.");
            if (!File.Exists(artifact.Path)) throw new FileNotFoundException("Recovery artifact was not found.", artifact.Path);
            await using var stream = File.OpenRead(artifact.Path);
            entries.Add(new(artifact.Kind, Path.GetFileName(artifact.Path), stream.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))));
        }

        var manifest = new RecoveryPackageManifest(
            "FlashCore-Recovery-1", "1.0.8", DateTimeOffset.UtcNow,
            request.EcuIdentifier, request.FirmwareChecksum, entries);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var output = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var pair in request.Artifacts.Zip(entries))
        {
            var archiveEntry = archive.CreateEntry($"artifacts/{pair.Second.Kind}/{pair.Second.FileName}", CompressionLevel.Optimal);
            await using var target = archiveEntry.Open();
            await using var source = File.OpenRead(pair.First.Path);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using (var target = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(target, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
        return manifest;
    }
}
