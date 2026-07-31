using System.Text.Json;

namespace FlashCore.ECU.QuickApps;

public sealed record QuickAppCategory(string Name, IReadOnlyList<string> Apps);

public sealed record QuickAppCatalog(
    string CatalogVersion,
    string Vehicle,
    string Market,
    int FirstModelYear,
    int LastModelYear,
    string ExecutionPolicy,
    IReadOnlyList<string> CompatibilityKeys,
    IReadOnlyList<QuickAppCategory> Categories)
{
    public int AppCount => Categories.Sum(category => category.Apps.Count);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Vehicle)) errors.Add("Vehicle is required.");
        if (FirstModelYear > LastModelYear) errors.Add("Model-year range is reversed.");
        if (!string.Equals(ExecutionPolicy, "catalog-only", StringComparison.Ordinal))
            errors.Add("Unvalidated Quick App catalogs must use catalog-only execution policy.");
        if (Categories.Count == 0) errors.Add("At least one category is required.");
        var names = Categories.SelectMany(category => category.Apps).ToArray();
        foreach (var duplicate in names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            errors.Add($"Duplicate Quick App name: {duplicate.Key}.");
        if (names.Any(string.IsNullOrWhiteSpace)) errors.Add("Quick App names cannot be empty.");
        return errors;
    }
}

public static class QuickAppCatalogLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<QuickAppCatalog> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var catalog = await JsonSerializer.DeserializeAsync<QuickAppCatalog>(stream, Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Quick App catalog is empty.");
        var errors = catalog.Validate();
        if (errors.Count > 0) throw new InvalidDataException("Invalid Quick App catalog: " + string.Join("; ", errors));
        return catalog;
    }
}
