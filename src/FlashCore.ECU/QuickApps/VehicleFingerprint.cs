using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlashCore.ECU.QuickApps;

public sealed record ControlUnitFingerprint(
    string Address,
    string? Name,
    string? PartNumber,
    string? HardwarePartNumber,
    string? SoftwareVersion,
    string? OdxIdentifier,
    string? Coding);

public sealed record VehicleFingerprint(
    string Make,
    string Model,
    int? ModelYear,
    string Market,
    string? Engine,
    string? Transmission,
    IReadOnlyList<string> InstalledEquipment,
    IReadOnlyList<ControlUnitFingerprint> ControlUnits,
    DateTimeOffset CapturedUtc)
{
    public ControlUnitFingerprint? FindUnit(string address) => ControlUnits.FirstOrDefault(unit =>
        string.Equals(NormalizeAddress(unit.Address), NormalizeAddress(address), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeAddress(string address) => address.Trim().TrimStart('0').PadLeft(2, '0');
}

public static partial class VehicleScanImporter
{
    public static async Task<VehicleFingerprint> ImportAsync(
        string path,
        string make = "Volkswagen",
        string model = "Golf Mk7/Mk7.5",
        string market = "US",
        CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Vehicle scan is empty.");
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ImportJson(text, make, model, market)
            : ImportVcdsText(text, make, model, market);
    }

    private static VehicleFingerprint ImportJson(string text, string make, string model, string market)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var unitsElement = TryGet(root, "controlUnits", out var units) ? units :
            throw new InvalidDataException("JSON scan must contain a controlUnits array.");
        var parsed = unitsElement.EnumerateArray().Select(unit => new ControlUnitFingerprint(
            GetRequired(unit, "address"), GetOptional(unit, "name"), GetOptional(unit, "partNumber"),
            GetOptional(unit, "hardwarePartNumber"), GetOptional(unit, "softwareVersion"),
            GetOptional(unit, "odxIdentifier"), GetOptional(unit, "coding"))).ToArray();
        return new(
            GetOptional(root, "make") ?? make,
            GetOptional(root, "model") ?? model,
            GetOptionalInt(root, "modelYear"),
            GetOptional(root, "market") ?? market,
            GetOptional(root, "engine"),
            GetOptional(root, "transmission"),
            GetStringArray(root, "installedEquipment"),
            EnsureUnique(parsed), DateTimeOffset.UtcNow);
    }

    private static VehicleFingerprint ImportVcdsText(string text, string make, string model, string market)
    {
        var units = new List<ControlUnitFingerprint>();
        var matches = AddressLine().Matches(text);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            var section = text[match.Index..end];
            units.Add(new(
                match.Groups[1].Value,
                match.Groups[2].Value.Trim(),
                MatchValue(PartNumber(), section, 1),
                MatchValue(PartNumber(), section, 2),
                MatchValue(SoftwareVersion(), section),
                MatchValue(OdxIdentifier(), section),
                MatchValue(Coding(), section)));
        }
        if (units.Count == 0) throw new InvalidDataException("No VCDS control-unit sections were found.");
        return new(make, model, MatchOptionalInt(ModelYear(), text), market,
            MatchValue(Engine(), text), MatchValue(Transmission(), text), [], EnsureUnique(units), DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<ControlUnitFingerprint> EnsureUnique(IEnumerable<ControlUnitFingerprint> units)
    {
        var result = units.ToArray();
        var duplicate = result.GroupBy(unit => unit.Address, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? result : throw new InvalidDataException($"Duplicate control-unit address: {duplicate.Key}.");
    }

    private static string? MatchValue(Regex regex, string text, int group = 1)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[group].Value.Trim() : null;
    }

    private static int? MatchOptionalInt(Regex regex, string text) =>
        int.TryParse(MatchValue(regex, text), out var value) ? value : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value) => element.TryGetProperty(name, out value);
    private static string GetRequired(JsonElement element, string name) => GetOptional(element, name) ??
        throw new InvalidDataException($"JSON scan field '{name}' is required.");
    private static string? GetOptional(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetOptionalInt(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
            : [];

    [GeneratedRegex(@"(?m)^Address\s+([0-9A-F]{2}):\s*([^\r\n(]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressLine();
    [GeneratedRegex(@"(?m)^\s*Part No SW:\s*([^\r\n]+?)(?:\s+HW:\s*([^\r\n]+))?$")]
    private static partial Regex PartNumber();
    [GeneratedRegex(@"(?m)^\s*Component:.*?([0-9]{3,6})\s*$")]
    private static partial Regex SoftwareVersion();
    [GeneratedRegex(@"(?m)^\s*ASAM Dataset:\s*([^\s]+)")]
    private static partial Regex OdxIdentifier();
    [GeneratedRegex(@"(?m)^\s*Coding:\s*([^\r\n]+)")]
    private static partial Regex Coding();
    [GeneratedRegex(@"(?im)^Model Year:\s*(20[0-9]{2})")]
    private static partial Regex ModelYear();
    [GeneratedRegex(@"(?im)^Engine:\s*([^\r\n]+)")]
    private static partial Regex Engine();
    [GeneratedRegex(@"(?im)^Transmission:\s*([^\r\n]+)")]
    private static partial Regex Transmission();
}
