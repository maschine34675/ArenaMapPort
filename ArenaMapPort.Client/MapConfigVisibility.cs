using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Keep F12 focused on the two maps currently being tuned. All maps retain their bound
    /// settings, saved values and runtime defaults; only ConfigurationManager display metadata
    /// changes. Its installed SettingSearcher removes Browsable=false before advanced filtering.
    /// Call after every repair has bound its settings, independently of category or key names.
    /// </summary>
    internal static class MapConfigVisibility
    {
        // BepInEx 5.4 ConfigEntryBase.Description has no setter. Replace its metadata object,
        // never the entry itself or the possibly shared original description/tags array.
        static readonly FieldInfo DescriptionField = typeof(ConfigEntryBase).GetField(
            "<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly HashSet<string> HiddenMapKeys = CreateHiddenMapKeys();

        internal static void Install()
        {
            if (DescriptionField == null || DescriptionField.FieldType != typeof(ConfigDescription))
            {
                Plugin.Log.LogWarning("[ArenaMapPort][Config] Cannot hide inactive-map options: " +
                    "this BepInEx version has an unsupported configuration metadata layout.");
                return;
            }

            int hidden = 0;
            foreach (var pair in Plugin.Instance.Config)
            {
                if (!ShouldHide(pair.Key.Section) && !EquatorProfiles.IsLegacyDefinition(pair.Key)) continue;
                try
                {
                    var entry = pair.Value;
                    var original = entry.Description;
                    var tags = original.Tags ?? Array.Empty<object>();
                    // Append last so pre-existing display tags cannot override the hide policy.
                    // The last-tag check also makes repeated calls safe for late-bound settings.
                    if (tags.Length == 0 || !(tags[tags.Length - 1] is BrowsableAttribute last) || last.Browsable)
                    {
                        var hiddenTags = new object[tags.Length + 1];
                        Array.Copy(tags, hiddenTags, tags.Length);
                        hiddenTags[tags.Length] = BrowsableAttribute.No;
                        DescriptionField.SetValue(entry,
                            new ConfigDescription(original.Description, original.AcceptableValues, hiddenTags));
                    }
                    hidden++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[ArenaMapPort][Config] Could not hide '{pair.Key}': " +
                        $"{e.GetType().Name}: {e.Message}");
                }
            }

            Plugin.Log.LogInfo($"[ArenaMapPort][Config] Hidden {hidden} inactive-map/legacy-profile options from F12; " +
                "Equator, Bay5 and shared settings remain available. Saved values are unchanged.");
        }

        static HashSet<string> CreateHiddenMapKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var map in ArenaMapRegistry.All)
            {
                if (map.Key != ArenaMapRegistry.Equator.Key && map.Key != ArenaMapRegistry.Bay5.Key)
                    keys.Add(map.Key);
            }
            return keys;
        }

        static bool ShouldHide(string section)
        {
            // Every per-map category uses "Category (arena_key)". Match the complete suffix,
            // so future categories are included without hiding global settings mentioning a map.
            if (string.IsNullOrEmpty(section) || !section.EndsWith(")", StringComparison.Ordinal)) return false;
            int start = section.LastIndexOf(" (", StringComparison.Ordinal);
            return start >= 0 && HiddenMapKeys.Contains(section.Substring(start + 2, section.Length - start - 3));
        }
    }
}
