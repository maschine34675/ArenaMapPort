using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>Latch diagnostic execution and F12 visibility together at plugin startup.</summary>
    internal static class DeveloperOptions
    {
        // ConfigurationManager discovers this exact type name and public field by reflection.
        sealed class ConfigurationManagerAttributes
        {
            public bool? IsAdvanced = true;
        }

        internal static bool Enabled { get; private set; }

        internal static void Install(ConfigFile config)
        {
            Enabled = config.Bind("General", "DeveloperMode", false, new ConfigDescription(
                "Show all supported-map tuning controls and enable diagnostic tools (F7–F10). " +
                "Restart the game after changing this option. " +
                "Saved presets and custom values are retained in the developer archive.", null,
                new ConfigurationManagerAttributes())).Value;
        }
    }
}
