#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ArenaMapPort.Client
{
    /// <summary>Acoustic class of the room the listener stands in, as far as gunshot reverb is concerned.</summary>
    internal enum RoomShotClass
    {
        /// <summary>Open air (or an outdoor-flagged room): no shoebox room, the shipped acoustic model stays.</summary>
        Outdoor,
        /// <summary>A big enclosed space: AirPit's main hall and passport hall. Long tail.</summary>
        Hall,
        /// <summary>An ordinary indoor room: shops, booths, offices.</summary>
        Shop,
        /// <summary>A steel body: the IndoorMetalThin rooms (AirPit's airliners, Bay5's and Bowl's containers) and a big
        /// IndoorMetalThick room (Bowl's crashed Mi-26).</summary>
        Metal
    }

    /// <summary>One shoebox room for the Meta XR Audio plugin. Immutable. Material names are the value names of
    /// <c>MetaXRAudioRoomAcousticProperties.MaterialPreset</c> (strings, so this model needs no Meta reference).</summary>
    internal sealed class RoomShotPreset
    {
        internal readonly float Width, Height, Depth, ClutterFactor;
        internal readonly string Right, Left, Ceiling, Floor, Front, Back;
        /// <summary>One entry per tuning value the model had to replace (unknown material name, unusable clutter); empty
        /// when the tuning was used as given. Entries read <c>HallCeiling='Bogus'->Concrete</c>.</summary>
        internal readonly string[] Corrections;

        internal RoomShotPreset(float width, float height, float depth,
            string right, string left, string ceiling, string floor, string front, string back, float clutterFactor,
            string[] corrections = null)
        {
            Width = width; Height = height; Depth = depth; ClutterFactor = clutterFactor;
            Right = right; Left = left; Ceiling = ceiling; Floor = floor; Front = front; Back = back;
            Corrections = corrections ?? Array.Empty<string>();
        }

        /// <summary>The six materials in the plugin's wall order: right, left, ceiling, floor, front, back.</summary>
        internal string[] Materials => new[] { Right, Left, Ceiling, Floor, Front, Back };

        /// <summary>True when at least one tuning value was replaced by the class default (see <see cref="Corrections"/>).</summary>
        internal bool UsedClassDefaults => Corrections.Length > 0;

        internal string DimensionsText => string.Format(CultureInfo.InvariantCulture, "{0:0.0}x{1:0.0}x{2:0.0}", Width, Height, Depth);
        internal string MaterialsText => string.Join(",", Materials);
        internal string CorrectionsText => string.Join("; ", Corrections);
    }

    /// <summary>Tuning of one indoor class: the four side walls (right, left, front, back) share <see cref="Walls"/>.
    /// Immutable; values are NOT validated here (the model validates them while resolving a room).</summary>
    internal sealed class RoomShotClassTuning
    {
        internal readonly string Walls, Ceiling, Floor;
        internal readonly float Clutter;

        internal RoomShotClassTuning(string walls, string ceiling, string floor, float clutter)
        {
            Walls = walls; Ceiling = ceiling; Floor = floor; Clutter = clutter;
        }
    }

    /// <summary>
    /// All live-tunable values of the room table (config section <c>ShotAcousticsLab</c>): material names and clutter of the
    /// three indoor classes plus the shoebox size cap. Immutable; <see cref="Default"/> is the built-in starting table.
    /// </summary>
    internal sealed class RoomShotTuning
    {
        internal readonly RoomShotClassTuning Hall, Shop, Metal;
        /// <summary>Largest shoebox edge in metres (config <c>HallMaxDimension</c>). It caps the edges of every indoor class
        /// (the name is historical: on the real maps only halls ever reach it).</summary>
        internal readonly float HallMaxDimension;

        internal RoomShotTuning(RoomShotClassTuning hall, RoomShotClassTuning shop, RoomShotClassTuning metal, float hallMaxDimension)
        {
            Hall = hall ?? throw new ArgumentNullException(nameof(hall));
            Shop = shop ?? throw new ArgumentNullException(nameof(shop));
            Metal = metal ?? throw new ArgumentNullException(nameof(metal));
            HallMaxDimension = hallMaxDimension;
        }

        /// <summary>The built-in table: the choice of the second host measurement round (docs/progress/shot-acoustics.md,
        /// "Tuning round"). Still INVENTED data, see <see cref="RoomShotAcousticsModel"/>.</summary>
        internal static readonly RoomShotTuning Default = new RoomShotTuning(
            new RoomShotClassTuning(RoomShotAcousticsModel.Concrete, RoomShotAcousticsModel.Concrete,
                RoomShotAcousticsModel.CeramicTile, RoomShotAcousticsModel.HallClutter),
            new RoomShotClassTuning(RoomShotAcousticsModel.GypsumBoard, RoomShotAcousticsModel.AcousticTile,
                RoomShotAcousticsModel.CeramicTile, RoomShotAcousticsModel.ShopClutter),
            new RoomShotClassTuning(RoomShotAcousticsModel.Steel, RoomShotAcousticsModel.Steel,
                RoomShotAcousticsModel.Carpet, RoomShotAcousticsModel.MetalClutter),
            RoomShotAcousticsModel.DefaultMaxDimension);

        /// <summary>The tuning of an indoor class; null for <see cref="RoomShotClass.Outdoor"/>.</summary>
        internal RoomShotClassTuning For(RoomShotClass roomClass)
        {
            switch (roomClass)
            {
                case RoomShotClass.Hall: return Hall;
                case RoomShotClass.Shop: return Shop;
                case RoomShotClass.Metal: return Metal;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Maps one acoustic room to an invented shoebox room for the Meta XR Audio plugin, so a gunshot in a big hall can ring
    /// differently from one inside a small shop.
    ///
    /// EVERY NUMBER AND EVERY MATERIAL BELOW IS INVENTED. Neither EFT nor Arena carries any per-room value for gunshot
    /// reverb: all 24 raw Arena levels hold zero reverb zones, filters or Meta XR components, and
    /// <c>RoomAmbientData.ReverbPreset</c> is empty on every one of the 91 rooms. The thresholds and the four classes are
    /// the author's educated guesses; the materials, clutter factors and the size cap are the outcome of two host
    /// measurement rounds: the first showed that near-perfectly reflective presets (Concrete, PlasterOnBrick, CeramicTile)
    /// make a small shop ring about six times louder and longer than the hall, the opposite of the intent; the second
    /// picked the current table (hard hall shell, GypsumBoard shop walls under an AcousticTile ceiling, 50 m cap), which
    /// gives the hall the long late tail and the shop a clear early slap that dies quickly. They are still invented
    /// values, and every one of them is a live lab key (<see cref="RoomShotTuning"/>) for tuning by ear.
    /// The table is generic by room type and size (not per map), which is why it is code and not descriptor data.
    ///
    /// Pure and Unity-independent on purpose: plain floats, ints and strings, covered by RoomShotAcousticsModelTests.
    /// Runtime: RoomShotAcoustics.cs. Background: docs/progress/shot-acoustics.md.
    /// </summary>
    internal static class RoomShotAcousticsModel
    {
        /// <summary>Smallest shoebox edge in metres; keeps degenerate bounds (zero-size rooms) away from the plugin.</summary>
        internal const float MinDimension = 2f;
        /// <summary>Default largest shoebox edge in metres (config <c>HallMaxDimension</c>). INVENTED; 50 m was picked in the
        /// second host measurement round (80 m gave a shorter late tail in the hall).</summary>
        internal const float DefaultMaxDimension = 50f;
        /// <summary>A not-outdoor room whose larger floor edge reaches this many metres is a Hall. INVENTED.</summary>
        internal const float HallMinFloorEdge = 40f;
        /// <summary>
        /// A not-outdoor IndoorMetalThick room whose larger floor edge reaches this many metres is a steel body (Metal); a
        /// smaller one stays a Shop. INVENTED, chosen between the only two IndoorMetalThick rooms in the four spatial-audio
        /// graphs: Bowl's crashed Mi-26 (<c>SpatialAudioSystem/Helicopter/Helicopter</c>, room_id 4, 20.8 x 4.5 x 13.8 m: a
        /// steel hull, its AABB larger than the hull because it lies rotated by about 30 degrees) is a Metal body, while
        /// Bay5's <c>PlantBuldingA_back_indoor</c> (room_id 27, 9.0 x 2.7 x 10.0 m) keeps the Shop result it was released
        /// with. The threshold is not a measured value.
        /// </summary>
        internal const float MetalThickBodyMinFloorEdge = 15f;

        // EAudioRoomTypeMask bits (Audio.SpatialSystem, EFT 0.16.9.5), mirrored here to stay free of EFT types.
        internal const int OutdoorGeneric = 0x1, OutdoorVehicle = 0x2, OutdoorCommon = 0x40;
        internal const int OutdoorBits = OutdoorGeneric | OutdoorVehicle | OutdoorCommon; // EAudioRoomTypeMask.Outdoor = 0x43
        internal const int IndoorMetalThin = 0x200;
        internal const int IndoorMetalThick = 0x10000;

        /// <summary>"No room applied yet" marker for <see cref="NeedsApply"/> (a real room ID is a short).</summary>
        internal const int NoRoom = int.MinValue;

        /// <summary>Plugin material names used by <see cref="RoomShotTuning.Default"/>. Every one must be a
        /// <c>MaterialPreset</c> value name.</summary>
        internal const string Steel = "Steel", Carpet = "Carpet", Concrete = "Concrete", CeramicTile = "CeramicTile",
            GypsumBoard = "GypsumBoard", AcousticTile = "AcousticTile";

        /// <summary>Metal: steel shell, soft floor, plenty of seats and luggage. INVENTED.</summary>
        internal const float MetalClutter = 0.7f;
        /// <summary>Hall: fairly empty, but not a bare box. INVENTED; kept by the second measurement round.</summary>
        internal const float HallClutter = 0.4f;
        /// <summary>Shop: shelves full of goods, counters. INVENTED; kept by the second measurement round.</summary>
        internal const float ShopClutter = 0.7f;

        /// <summary>Every value name of <c>MetaXRAudioRoomAcousticProperties.MaterialPreset</c>, in enum order. The lab keys
        /// offer exactly these (see <see cref="MaterialChoices"/>); a name outside the list is replaced by the class default.
        /// Do not modify.</summary>
        internal static readonly string[] MaterialNames =
        {
            "AcousticTile", "Brick", "BrickPainted", "Cardboard", "Carpet", "CarpetHeavy", "CarpetHeavyPadded", "CeramicTile",
            "Concrete", "ConcreteRough", "ConcreteBlock", "ConcreteBlockPainted", "Curtain", "Foliage", "Glass", "GlassHeavy",
            "Grass", "Gravel", "GypsumBoard", "Marble", "Mud", "PlasterOnBrick", "PlasterOnConcreteBlock", "Rubber", "Soil",
            "SoundProof", "Snow", "Steel", "Stone", "Vent", "Water", "WoodThin", "WoodThick", "WoodFloor", "WoodOnConcrete",
            "MetaDefault"
        };

        /// <summary>Is this an exact (ordinal, case-sensitive) <c>MaterialPreset</c> value name?</summary>
        internal static bool IsMaterialName(string name) => name != null && Array.IndexOf(MaterialNames, name) >= 0;

        /// <summary>
        /// The choices one material lab key offers: every <c>MaterialPreset</c> name, with <paramref name="keyDefault"/> (the
        /// key's own default) moved to index 0 and the rest in enum order. BepInEx's <c>AcceptableValueList</c> clamps a value
        /// that is not in the list to its FIRST entry, whenever the .cfg is loaded or an entry is set, so a hand typo
        /// (<c>HallWalls = Concerte</c>) or an empty value lands on the key's default instead of on whatever the enum lists
        /// first (AcousticTile, a very absorptive material). Each of the nine material keys therefore gets its own list.
        /// A <paramref name="keyDefault"/> that is not a material name leaves the enum order unchanged. A new array on every
        /// call, so no caller can alter <see cref="MaterialNames"/>.
        /// </summary>
        internal static string[] MaterialChoices(string keyDefault)
        {
            if (!IsMaterialName(keyDefault)) return (string[])MaterialNames.Clone();
            var choices = new string[MaterialNames.Length];
            choices[0] = keyDefault;
            int next = 1;
            foreach (var name in MaterialNames)
                if (!string.Equals(name, keyDefault, StringComparison.Ordinal)) choices[next++] = name;
            return choices;
        }

        /// <summary>
        /// Is this room open air? The flag or any outdoor type bit says so, except that an isolated room is sealed from
        /// outside by definition: the isolated flag wins, the same precedence RoomReverb uses for its mixer categories
        /// (isolated, then outdoor, then indoor).
        /// </summary>
        internal static bool IsOutdoor(int typeMask, bool outdoor, bool isolated)
            => !isolated && (outdoor || (typeMask & OutdoorBits) != 0);

        /// <summary>Classifies one room. Order of rules: Outdoor, Metal (IndoorMetalThin, or an IndoorMetalThick room whose
        /// larger floor edge reaches <see cref="MetalThickBodyMinFloorEdge"/>), Hall, Shop.</summary>
        internal static RoomShotClass Classify(int typeMask, bool outdoor, bool isolated, float width, float depth)
        {
            if (IsOutdoor(typeMask, outdoor, isolated)) return RoomShotClass.Outdoor;
            if ((typeMask & IndoorMetalThin) != 0) return RoomShotClass.Metal;
            // Bowl's Mi-26 (IndoorMetalThick, 20.8 x 13.8 m floor): a steel hull, not a shop. Bay5's 10 m back room is below the edge.
            if ((typeMask & IndoorMetalThick) != 0 && Math.Max(width, depth) >= MetalThickBodyMinFloorEdge) return RoomShotClass.Metal;
            if (Math.Max(width, depth) >= HallMinFloorEdge) return RoomShotClass.Hall;
            return RoomShotClass.Shop;
        }

        /// <summary>
        /// Classifies a room and builds its shoebox preset from <paramref name="tuning"/> (null means
        /// <see cref="RoomShotTuning.Default"/>). <paramref name="width"/>, <paramref name="height"/> and
        /// <paramref name="depth"/> are the full room size in metres (world X, Y, Z: twice the bounds extents). The preset
        /// is null for Outdoor. Every preset edge is clamped to [<see cref="MinDimension"/>, the tuning's cap]. Never throws
        /// on a bad tuning: an unknown material name, or a clutter factor that is not a number, is replaced by the class
        /// default and listed in <see cref="RoomShotPreset.Corrections"/>; a clutter factor outside 0..1 is clamped and listed.
        /// </summary>
        internal static RoomShotClass Resolve(int typeMask, bool outdoor, bool isolated,
            float width, float height, float depth, RoomShotTuning tuning, out RoomShotPreset preset)
        {
            var roomClass = Classify(typeMask, outdoor, isolated, width, depth);
            if (roomClass == RoomShotClass.Outdoor)
            {
                preset = null;
                return roomClass;
            }
            tuning = tuning ?? RoomShotTuning.Default;
            var given = tuning.For(roomClass);
            var fallback = RoomShotTuning.Default.For(roomClass);
            var corrections = new List<string>();
            string prefix = roomClass.ToString();
            string walls = Material(given.Walls, fallback.Walls, prefix + "Walls", corrections);
            string ceiling = Material(given.Ceiling, fallback.Ceiling, prefix + "Ceiling", corrections);
            string floor = Material(given.Floor, fallback.Floor, prefix + "Floor", corrections);
            float clutter = Clutter(given.Clutter, fallback.Clutter, prefix + "Clutter", corrections);

            float cap = tuning.HallMaxDimension;
            // A bad cap never inverts the clamp range; a NaN cap falls back to the default instead of poisoning the clamp.
            float max = float.IsNaN(cap) ? DefaultMaxDimension : Math.Max(cap, MinDimension);
            preset = new RoomShotPreset(Clamp(width, max), Clamp(height, max), Clamp(depth, max),
                walls, walls, ceiling, floor, walls, walls, clutter, corrections.ToArray());
            return roomClass;
        }

        static string Material(string name, string classDefault, string key, List<string> corrections)
        {
            if (IsMaterialName(name)) return name;
            corrections.Add(string.Format(CultureInfo.InvariantCulture, "{0}='{1}'->{2}", key, name ?? "(null)", classDefault));
            return classDefault;
        }

        static float Clutter(float value, float classDefault, string key, List<string> corrections)
        {
            if (value >= 0f && value <= 1f) return value;
            // NaN fails both comparisons above; the plugin's field is [Range(0, 1)].
            float result = float.IsNaN(value) ? classDefault : Math.Min(Math.Max(value, 0f), 1f);
            corrections.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1}->{2}", key, value, result));
            return result;
        }

        /// <summary>
        /// Must the listener's room be pushed to the plugin again? Yes on the first room, on a different room, after any
        /// change of a lab tuning key (the runtime counts those: <paramref name="tuningVersion"/> differs from the version
        /// that was applied, even when the room is unchanged), and after an audio configuration change (AudioSettings.Reset,
        /// a device switch) while a room is applied: the plugin's managed model value can then still read ShoeboxRoom
        /// although its native side lost it, and its setter does nothing for an unchanged value, so only an explicit
        /// re-application restores it. Versions are plain counters compared for equality, so a wrap-around is harmless and
        /// an unchanged tuning can never make this return true on every frame.
        /// </summary>
        internal static bool NeedsApply(int appliedRoomId, int roomId, int appliedTuningVersion, int tuningVersion,
            bool audioConfigurationChanged)
        {
            if (appliedRoomId == NoRoom) return true;
            return audioConfigurationChanged || roomId != appliedRoomId || tuningVersion != appliedTuningVersion;
        }

        /// <summary>
        /// Can this process hear anything? A headless client (Fika's dedicated host runs batch mode without graphics; not
        /// verified here) has no audio device worth driving. EFT creates the AudioListener object on EVERY client, headless
        /// ones included (ClientApplication.StartTask), so its presence says nothing: this check is what keeps such a
        /// client from touching the plugin's native acoustics.
        /// </summary>
        internal static bool IsAudioClient(bool batchMode, bool nullGraphicsDevice) => !batchMode && !nullGraphicsDevice;

        static float Clamp(float value, float max)
        {
            if (float.IsNaN(value)) return MinDimension;
            return Math.Min(Math.Max(value, MinDimension), max);
        }
    }
}
