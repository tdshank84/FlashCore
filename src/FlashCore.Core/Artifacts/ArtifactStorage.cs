namespace FlashCore.Core.Artifacts;

public sealed record ArtifactRetentionOptions(TimeSpan MaximumAge, int MaximumFiles = 100);

public static class ArtifactStorage
{
    public static string GetDefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "FlashCore", "artifacts");
    }

    public static string CreatePath(string prefix, string extension)
    {
        var directory = GetDefaultDirectory();
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.{extension.TrimStart('.')}");
    }

    public static void ApplyRetention(string directory, ArtifactRetentionOptions options)
    {
        if (!Directory.Exists(directory)) return;
        var files = new DirectoryInfo(directory).EnumerateFiles()
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        var cutoff = DateTime.UtcNow - options.MaximumAge;
        foreach (var file in files.Where((file, index) => index >= options.MaximumFiles || file.LastWriteTimeUtc < cutoff))
            file.Delete();
    }
}
