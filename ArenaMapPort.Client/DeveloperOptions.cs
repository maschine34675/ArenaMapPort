using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>Latch diagnostic execution and F12 visibility together at plugin startup.</summary>
    internal static class DeveloperOptions
    {
        internal static bool Enabled { get; private set; }

        internal static void Install(ConfigFile config)
        {
            Enabled = config.Bind("General", "DeveloperMode", false,
                "Show all supported-map tuning controls and enable diagnostic tools (F7–F10). " +
                "Restart the game after changing this option. Saved presets and custom values are retained.").Value;
        }
    }
}
