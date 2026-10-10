using BepInEx.Configuration;
using System.ComponentModel;

namespace ArenaMapPort.Client
{
    /// <summary>Latch diagnostic execution and F12 visibility together at plugin startup.</summary>
    internal static class DeveloperOptions
    {
        internal static bool Enabled { get; private set; }

        internal static void Install(ConfigFile config)
        {
            Enabled = config.Bind("General", "DeveloperMode", false, new ConfigDescription(
                "Show all supported-map tuning controls and enable diagnostic tools (F7–F10). " +
                "Edit this key in the configuration file, then restart the game. " +
                "Saved presets and custom values are retained in the developer archive.", null, BrowsableAttribute.No)).Value;
        }
    }
}
