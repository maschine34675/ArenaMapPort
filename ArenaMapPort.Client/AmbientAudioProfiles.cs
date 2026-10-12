#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArenaMapPort.Client
{
    /// <summary>The two native ambient parent buses of the master mixer (<c>AmbientOutVolume</c> and
    /// <c>AmbientInVolume</c>). EFT's LocalGame resets both to -80 dB; the imported ambience opens the ones its
    /// groups feed (see <see cref="AmbientBusLease"/>).</summary>
    internal enum AmbientBus
    {
        /// <summary>Exterior ambience: <c>AmbientOut/CommonAmbOutEffects</c> and the <c>CommonAmbOutBypass</c> group.</summary>
        Out = 0,
        /// <summary>Interior ambience: <c>AmbientIn/CommonAmbInEffects</c>.</summary>
        In = 1,
    }

    /// <summary>Where one imported <c>AmbientSoundPlayerGroup</c> sends its players, found by the name of the
    /// GameObject that carries the group component.</summary>
    internal sealed class AmbientGroupRoute
    {
        internal string GroupName { get; }
        /// <summary><c>AudioMixer.FindMatchingGroups</c> path of the native mixer group.</summary>
        internal string MixerPath { get; }
        /// <summary>Exact name the single match must have.</summary>
        internal string MixerName { get; }
        /// <summary>The parent bus this group's sound travels through (the bypass group is an AmbientOut family bus).</summary>
        internal AmbientBus Bus { get; }
        /// <summary>How many players the source scene lists in this group (the contract's <c>states.groups.&lt;name&gt;.players</c>).</summary>
        internal int Members { get; }
        /// <summary>The one group the diagnostic MapAmbience pulse and the reverb reroute address.</summary>
        internal bool Effects { get; }
        /// <summary>EFT permanently mutes the original Returns/Reverb group behind this mixer, so the group is moved to the
        /// compatible ambient effects bus while the native room controller is active (docs/progress/room-reverb.md).</summary>
        internal bool ReverbReroute { get; }

        internal AmbientGroupRoute(string groupName, string mixerPath, string mixerName, AmbientBus bus, int members,
            bool effects = false, bool reverbReroute = false)
        {
            if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(mixerPath) || string.IsNullOrEmpty(mixerName) ||
                !mixerPath.EndsWith(mixerName, StringComparison.Ordinal) || members <= 0)
                throw new ArgumentException("Ambient group route needs a group name, a mixer path ending in the mixer name and members");
            if (reverbReroute && !effects) throw new ArgumentException("Only the effects group can be rerouted to the reverb bus");
            GroupName = groupName; MixerPath = mixerPath; MixerName = mixerName; Bus = bus; Members = members;
            Effects = effects; ReverbReroute = reverbReroute;
        }
    }

    /// <summary>What the client expects of one map's imported ambient graph and where it sends the sound.</summary>
    internal sealed class AmbientAudioProfile
    {
        internal string MapKey { get; }
        /// <summary>Name used in logs and config descriptions.</summary>
        internal string DisplayName { get; }
        /// <summary>The inactive root the importer authors in the Scripts scene (contract <c>audio.root</c>).</summary>
        internal string RootName { get; }
        /// <summary>Every <c>BaseAmbientSoundPlayer</c> under the root, with its <c>AudioSource</c>.</summary>
        internal int Players { get; }
        /// <summary>Players the groups start (enabled and active under the root).</summary>
        internal int ActivePlayers { get; }
        internal int Groups { get; }
        /// <summary><c>AmbientSplineEmitterController</c> components (Bay5's shoreline).</summary>
        internal int Controllers { get; }
        /// <summary>One <c>AmbientPlayerSplineMappedEmitter</c> and a stock <c>SoundAmbientZoneCalculator</c> (Bay5 harbor).</summary>
        internal bool HasShoreline { get; }
        /// <summary>The graph holds <c>SeasonAmbientSoundPlayer</c>s: wait for the season controller and follow its status.</summary>
        internal bool UsesSeasons { get; }
        /// <summary>The bundle keeps the source scene's activation states (importer schema 2): the groups define which players
        /// start, in group order, and the inactive and looping players are pinned exactly. Bay5 (schema 1) starts every
        /// player in hierarchy order.</summary>
        internal bool OriginalActivation { get; }
        /// <summary>Players that exist in the graph but stay inactive under an inactive ancestor, as in the source scene.</summary>
        internal IReadOnlyList<string> InactivePlayers { get; }
        /// <summary>The only players whose <c>AudioSource.loop</c> is set (strict activation contract only).</summary>
        internal IReadOnlyList<string> LoopPlayers { get; }
        /// <summary>One per <c>AmbientSoundPlayerGroup</c>, in source order (the contract's <c>group_mixers</c>).</summary>
        internal IReadOnlyList<AmbientGroupRoute> Routes { get; }

        /// <summary>The parent buses the driver must hold neutral while the graph lives, in first-use order.</summary>
        internal IReadOnlyList<AmbientBus> Buses => Routes.Select(r => r.Bus).Distinct().ToArray();
        internal AmbientGroupRoute EffectsRoute => Routes.Single(r => r.Effects);

        internal AmbientAudioProfile(string mapKey, string displayName, string rootName, int players, int activePlayers,
            int controllers, bool hasShoreline, bool usesSeasons, bool originalActivation,
            string[] inactivePlayers, string[] loopPlayers, AmbientGroupRoute[] routes)
        {
            if (routes == null || routes.Length == 0 || routes.Count(r => r.Effects) != 1 ||
                routes.Select(r => r.GroupName).Distinct().Count() != routes.Length)
                throw new ArgumentException("A profile needs uniquely named routes and exactly one effects route");
            inactivePlayers = inactivePlayers ?? Array.Empty<string>();
            loopPlayers = loopPlayers ?? Array.Empty<string>();
            if (players <= 0 || activePlayers <= 0 || players - activePlayers != inactivePlayers.Length ||
                routes.Sum(r => r.Members) != activePlayers ||
                controllers < 0 || (!originalActivation && (inactivePlayers.Length != 0 || loopPlayers.Length != 0)))
                throw new ArgumentException("Inconsistent ambient player census for " + mapKey);
            MapKey = mapKey; DisplayName = displayName; RootName = rootName;
            Players = players; ActivePlayers = activePlayers; Groups = routes.Length; Controllers = controllers;
            HasShoreline = hasShoreline; UsesSeasons = usesSeasons; OriginalActivation = originalActivation;
            InactivePlayers = inactivePlayers; LoopPlayers = loopPlayers; Routes = routes;
        }

        /// <summary>Index into <see cref="Routes"/> of the group carried by a GameObject of this name, or -1.</summary>
        internal int IndexOfGroup(string groupName)
        {
            for (int i = 0; i < Routes.Count; i++)
                if (Routes[i].GroupName == groupName) return i;
            return -1;
        }
    }

    /// <summary>
    /// The per-map table of the imported-ambience driver (<see cref="Bay5AmbientAudio"/>): root name, graph census, which
    /// Bay5- or Equator-only steps apply, and the group name to mixer/bus routing. Pure data (no Unity, EFT or BepInEx
    /// types), so the xunit project links this file and pins it against the extraction contracts
    /// (<c>tools/extract/native_contracts/&lt;stem&gt;.json</c>, key <c>audio</c>); a map missing here simply has no
    /// imported ambience and the driver returns before it binds or starts anything. The same names appear in the Editor
    /// importer (<c>Bay5AudioImporter</c>), which cannot reference this assembly, and a test compares them.
    ///
    /// Equator: one interior group on AmbientIn, original activation (two inactive dogs, one looping electricity hum),
    /// reverb reroute. Bay5: harbor graph with seasonal players, a stock shoreline and a bypass water group on the
    /// AmbientOut family. AirPit (2026-10-12): 26 plain players in an exterior group (AmbientOut, 4 far sources) and an
    /// interior group (AmbientIn, 22 sources); both buses stay open all raid (no room crossfade yet), the interior group
    /// takes the same reverb reroute as Equator because EFT mutes the CommonAmbInEffects original return.
    /// </summary>
    internal static class AmbientAudioProfiles
    {
        internal const string OutVolumeParameter = "AmbientOutVolume";
        internal const string InVolumeParameter = "AmbientInVolume";

        static readonly AmbientAudioProfile[] Table =
        {
            new AmbientAudioProfile("arena_equator", "Equator", "ArenaMapPort_EquatorNativeAudio",
                players: 25, activePlayers: 23, controllers: 0, hasShoreline: false, usesSeasons: false, originalActivation: true,
                inactivePlayers: new[] { "far_dogs1", "far_dogs2" }, loopPlayers: new[] { "electricity" },
                routes: new[]
                {
                    new AmbientGroupRoute("CommonAmbientInGroup", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects",
                        AmbientBus.In, members: 23, effects: true, reverbReroute: true),
                }),
            new AmbientAudioProfile("arena_bay5", "Bay5", "ArenaMapPort_Bay5NativeAudio",
                players: 24, activePlayers: 24, controllers: 1, hasShoreline: true, usesSeasons: true, originalActivation: false,
                inactivePlayers: null, loopPlayers: null,
                routes: new[]
                {
                    new AmbientGroupRoute("AmbientPlayersGroup", "AmbientOut/CommonAmbOutEffects", "CommonAmbOutEffects",
                        AmbientBus.Out, members: 23, effects: true),
                    new AmbientGroupRoute("AmbientSplineEmitterWaterGroup", "CommonAmbOutBypass", "CommonAmbOutBypass",
                        AmbientBus.Out, members: 1),
                }),
            new AmbientAudioProfile("arena_airpit", "AirPit", "ArenaMapPort_AirPitNativeAudio",
                players: 26, activePlayers: 26, controllers: 0, hasShoreline: false, usesSeasons: false, originalActivation: true,
                inactivePlayers: null, loopPlayers: null,
                routes: new[]
                {
                    new AmbientGroupRoute("CommonAmbientOutGroup", "AmbientOut/CommonAmbOutEffects", "CommonAmbOutEffects",
                        AmbientBus.Out, members: 4),
                    new AmbientGroupRoute("CommonAmbientInGroup", "AmbientIn/CommonAmbInEffects", "CommonAmbInEffects",
                        AmbientBus.In, members: 22, effects: true, reverbReroute: true),
                }),
        };

        /// <summary>Every map with an imported ambient graph, in config binding order.</summary>
        internal static IReadOnlyList<AmbientAudioProfile> All => Table;

        internal static bool TryGet(string mapKey, out AmbientAudioProfile profile)
        {
            profile = null;
            if (string.IsNullOrEmpty(mapKey)) return false;
            foreach (var candidate in Table)
                if (candidate.MapKey == mapKey) { profile = candidate; return true; }
            return false;
        }

        internal static AmbientAudioProfile Get(string mapKey) =>
            TryGet(mapKey, out var profile) ? profile : throw new ArgumentException("No ambient audio profile for " + mapKey, nameof(mapKey));

        /// <summary>The native mixer parameter <see cref="AmbientBusLease"/> accepts for a bus.</summary>
        internal static string Parameter(AmbientBus bus) => bus == AmbientBus.In ? InVolumeParameter : OutVolumeParameter;

        /// <summary>True when the map's ambient driver holds the bus neutral while its graph lives.</summary>
        internal static bool LeasesBus(string mapKey, AmbientBus bus) =>
            TryGet(mapKey, out var profile) && profile.Buses.Contains(bus);
    }
}
