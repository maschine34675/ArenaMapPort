using System.Text.Json;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Server;

/// <summary>
/// Original Arena text is shipped as extracted data, never embedded in source.
///
/// Arena's locales carry a map description for only some maps (Equator, Bay5, AirPit and Bowl among the released ones;
/// Iceberg has no Name or Description row at all, PLAN-ICEBERG item 10). The pack
/// (<c>locales/map-descriptions.json</c>, written by <c>tools/extract/extract_map_descriptions.py</c>) must cover exactly
/// the released maps that HAVE an official text (<see cref="OfficialTextMapKeys"/>), plus, optionally, the entries of official-text
/// maps that are not released yet (Autoservice before its promotion); a map without one is not in the pack
/// and answers with its descriptor's <see cref="ArenaMapDescriptor.LocaleDescription"/> (the port's own words, one
/// English string for every language). A damaged, stale or over-complete pack is still discarded as a whole, so the
/// official texts are never half-applied.
/// </summary>
public sealed class ArenaMapDescriptions
{
    /// <summary>
    /// The maps whose official description exists in Arena's locales: the key set of
    /// <c>extract_map_descriptions.DESCRIPTION_KEYS</c> (a test pins the two against each other). A new map joins this
    /// list only together with its Arena location id in that Python table; a released map missing here falls back to its
    /// own <see cref="ArenaMapDescriptor.LocaleDescription"/>. A map listed here may precede its promotion into
    /// <see cref="ArenaMapRegistry.All"/> (Autoservice, PLAN-AUTOSERVICE item 9): <see cref="Load(string, Action{string}, IEnumerable{ArenaMapDescriptor})"/>
    /// then accepts, but does not require, its entry.
    /// </summary>
    public static readonly IReadOnlyList<string> OfficialTextMapKeys = new[]
    {
        ArenaMapRegistry.Equator.Key,
        ArenaMapRegistry.Bay5.Key,
        ArenaMapRegistry.AirPit.Key,
        ArenaMapRegistry.Bowl.Key,
        ArenaMapRegistry.AutoService.Key,
    };

    private readonly Dictionary<string, Dictionary<string, string>> _descriptions = new();

    /// <summary>Whether Arena's locales carry an official description for <paramref name="map"/>.</summary>
    public static bool HasOfficialText(ArenaMapDescriptor map) => OfficialTextMapKeys.Contains(map.Key);

    public static ArenaMapDescriptions Load(string path, Action<string> warn) =>
        Load(path, warn, ArenaMapRegistry.All);

    /// <param name="released">The maps the server registers; the pack must cover exactly those of them that have an
    /// official text. The two-argument overload passes <see cref="ArenaMapRegistry.All"/>.</param>
    public static ArenaMapDescriptions Load(string path, Action<string> warn, IEnumerable<ArenaMapDescriptor> released)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("schema_version").GetInt32() != 1)
                throw new InvalidDataException("Unsupported schema version");

            var result = new ArenaMapDescriptions();
            var releasedKeys = released.Select(map => map.Key).ToHashSet(StringComparer.Ordinal);
            var maps = released.Where(HasOfficialText).Select(map => map.Key).ToHashSet(StringComparer.Ordinal);
            // Official-text maps that are not released yet: their entry is allowed in the pack (the extractor writes it ahead
            // of the promotion) but never required, so a pack of the released maps alone stays valid.
            var pending = OfficialTextMapKeys.Where(key => !releasedKeys.Contains(key)).ToHashSet(StringComparer.Ordinal);
            foreach (var language in root.GetProperty("descriptions").EnumerateObject())
            {
                var values = new Dictionary<string, string>();
                foreach (var entry in language.Value.EnumerateObject())
                {
                    var value = entry.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        throw new InvalidDataException("Empty map description");
                    values.Add(entry.Name, value);
                }
                var covered = values.Keys.Where(key => !pending.Contains(key)).ToHashSet(StringComparer.Ordinal);
                if (!maps.SetEquals(covered))
                    throw new InvalidDataException("Description map set differs from the released maps that have an official text");
                result._descriptions.Add(language.Name, values);
            }
            if (!result._descriptions.ContainsKey("en"))
                throw new InvalidDataException("English fallback is missing");
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                                   or InvalidOperationException or KeyNotFoundException or ArgumentException
                                   or FormatException or OverflowException)
        {
            warn($"Original map descriptions could not be loaded: {ex.Message}. " +
                 "Reinstall ArenaMapPort's locales/map-descriptions.json; using built-in descriptions.");
            return new ArenaMapDescriptions();
        }
    }

    public string Resolve(string language, ArenaMapDescriptor map)
    {
        // EFT uses 'ge' for German, while tools commonly use ISO 'de'.
        if (language == "de") language = "ge";
        if (_descriptions.TryGetValue(language, out var localized) && localized.TryGetValue(map.Key, out var value))
            return value;
        if (_descriptions.TryGetValue("en", out var english) && english.TryGetValue(map.Key, out value))
            return value;
        return map.LocaleDescription;
    }
}
