using BepInEx.Configuration;

namespace ArenaMapPort.Client
{
    /// <summary>The sole player-facing choice. Raid loading captures it before scene repairs run.</summary>
    internal static class PlayerOptions
    {
        static ConfigEntry<bool> _naturalNight;
        internal static bool NaturalEquatorNight => _naturalNight?.Value == true;

        internal static void Install(ConfigFile config) => _naturalNight = config.Bind(
            "Appearance", "NaturalEquatorNight", false,
            "Use Equator's natural Day appearance recipe for the 00:00 raid, without added neon lights, " +
            "chemlights, smoke/haze, temporary PostFX or extra wet-floor reflections. The time remains " +
            "midnight; normal map lamps remain. Weather stays unchanged. Takes effect next raid.");
    }
}
