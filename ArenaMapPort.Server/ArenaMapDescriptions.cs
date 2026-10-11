using System.Text.Json;
using ArenaMapPort.Shared;

namespace ArenaMapPort.Server;

/// <summary>Original Arena text is shipped as extracted data, never embedded in source.</summary>
public sealed class ArenaMapDescriptions
{
    private readonly Dictionary<string, Dictionary<string, string>> _descriptions = new();

    public static ArenaMapDescriptions Load(string path, Action<string> warn)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("schema_version").GetInt32() != 1)
                throw new InvalidDataException("Unsupported schema version");

            var result = new ArenaMapDescriptions();
            var maps = ArenaMapRegistry.All.Select(map => map.Key).ToHashSet(StringComparer.Ordinal);
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
                if (!maps.SetEquals(values.Keys))
                    throw new InvalidDataException("Description map set differs from the release registry");
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
