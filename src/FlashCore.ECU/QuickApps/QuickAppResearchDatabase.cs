using System.Text.Json;

namespace FlashCore.ECU.QuickApps;

public sealed record QuickAppResearchEntry(
    string AppId,
    string ControlUnitAddress,
    string? PartNumber,
    string? SoftwareVersion,
    QuickAppChangeKind Kind,
    string Channel,
    string CandidateValue,
    ResearchConfidence Confidence,
    IReadOnlyList<string> Sources,
    string Notes);

public sealed record QuickAppResearchDatabase(
    string DatabaseVersion,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<QuickAppResearchEntry> Entries)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var entry in Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.AppId) || string.IsNullOrWhiteSpace(entry.Channel))
                errors.Add("Research entries require an app ID and channel.");
            if (entry.Sources.Count == 0) errors.Add($"{entry.AppId}/{entry.Channel} has no source.");
            foreach (var source in entry.Sources)
                if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    errors.Add($"{entry.AppId}/{entry.Channel} has an invalid source URI.");
            if (entry.Confidence >= ResearchConfidence.ModuleMatched &&
                (string.IsNullOrWhiteSpace(entry.PartNumber) || string.IsNullOrWhiteSpace(entry.SoftwareVersion)))
                errors.Add($"{entry.AppId}/{entry.Channel} requires module and software identifiers at {entry.Confidence} confidence.");
        }
        return errors;
    }
}

public static class QuickAppResearchDatabaseStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<QuickAppResearchDatabase> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var database = await JsonSerializer.DeserializeAsync<QuickAppResearchDatabase>(stream, Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Research database is empty.");
        var errors = database.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid research database: " + string.Join("; ", errors));
        return database;
    }

    public static async Task SaveAsync(string path, QuickAppResearchDatabase database, CancellationToken cancellationToken = default)
    {
        var errors = database.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid research database: " + string.Join("; ", errors));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(database, Options), cancellationToken).ConfigureAwait(false);
    }
}
