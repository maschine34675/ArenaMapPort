using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ArenaMapPort.Shared;
using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>Small player menu over the retained map recipes; diagnostics require a restart.</summary>
    internal static class MapConfigVisibility
    {
        static readonly FieldInfo DescriptionField = typeof(ConfigEntryBase).GetField(
            "<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Dictionary<ConfigEntryBase, ConfigDescription> Originals =
            new Dictionary<ConfigEntryBase, ConfigDescription>();

        internal static void Install() => Apply(Plugin.Instance.Config, DeveloperOptions.Enabled);

        internal static void Apply(ConfigFile config, bool developer)
        {
            if (DescriptionField == null || DescriptionField.FieldType != typeof(ConfigDescription))
            {
                Plugin.Log.LogWarning("[ArenaMapPort][Config] Unsupported BepInEx metadata layout; option filtering unavailable.");
                return;
            }
            int visible = 0, hidden = 0;
            foreach (var pair in config)
            {
                var entry = pair.Value;
                if (!Originals.TryGetValue(entry, out var original)) Originals.Add(entry, original = entry.Description);
                bool show = !IsPermanentHidden(pair.Key, original) && (developer || IsPlayerOption(pair.Key));
                // Rebuild from the original: repeated application cannot grow tags, lose slider
                // ranges/custom drawers or expose an internal migration marker.
                var tags = original.Tags ?? Array.Empty<object>();
                var filtered = new object[tags.Length + 1];
                Array.Copy(tags, filtered, tags.Length);
                filtered[tags.Length] = show ? BrowsableAttribute.Yes : BrowsableAttribute.No;
                DescriptionField.SetValue(entry, new ConfigDescription(original.Description, original.AcceptableValues, filtered));
                if (show) visible++; else hidden++;
            }
            Plugin.Log.LogDebug($"[ArenaMapPort][Config] mode={(developer ? "developer" : "player")}, visible={visible}, hidden={hidden}; saved values retained. Mode changes require restart.");
        }

        internal static bool IsPlayerOption(ConfigDefinition definition)
        {
            // General/ArenaInstallPath is the Arena installation override (0.2.3, ArenaInstallationGate): a normal
            // control, because an owner whose Arena has no launcher registry entry has to find it in F12.
            return (definition.Section == "Appearance" && definition.Key == "NaturalEquatorNight") ||
                (definition.Section == "General" && definition.Key == "DeveloperMode") ||
                (definition.Section == "General" && definition.Key == "ArenaInstallPath");
        }

        static bool IsPermanentHidden(ConfigDefinition definition, ConfigDescription original)
        {
            if (EquatorProfiles.IsLegacyDefinition(definition)) return true;
            foreach (var tag in original.Tags ?? Array.Empty<object>())
                if (tag is BrowsableAttribute browsable && !browsable.Browsable) return true;
            // Sections of parked maps (in the Catalog, not in the released All) stay hidden for good.
            foreach (var map in ArenaMapRegistry.Catalog)
                if (!ArenaMapRegistry.All.Any(released => released.Key == map.Key) &&
                    definition.Section.EndsWith("(" + map.Key + ")", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
