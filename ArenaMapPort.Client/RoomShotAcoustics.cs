using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using ArenaMapPort.Shared;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Per-room gunshot reverb from INVENTED room data (docs/progress/shot-acoustics.md).
    ///
    /// Vanilla: every gunshot is a ReverbSuperSource whose audible reverb comes from Meta XR Audio spatialized sources
    /// (MetaXRAudioSource, reverb and early-reflection sends from the Gunshots AudioGroupPreset). The plugin's
    /// process-global acoustic model is AcousticRayTracing, which needs baked .xramap geometry; the unity mixer return
    /// for guns is muted and cannot be driven, and the ambient RoomReverb chain never reaches gunshots.
    /// Ported scene: carries no .xramap geometry (zero MetaXRAcousticMap, zero room components), so ray tracing most
    /// likely renders nothing and a shot in AirPit's 100 m hall sounds like a shot in a 5 m shop. Neither EFT nor Arena
    /// ships any per-room gunshot value (zero reverb zones or filters in all 24 raw Arena levels, empty
    /// RoomAmbientData.ReverbPreset on all 91 rooms), so there is nothing to restore: every size, material and clutter
    /// factor is made up (RoomShotAcousticsModel).
    /// Repair (on by default since 2026-10-16, user decision after listening on all three maps; off per map restores the
    /// shipped acoustics): while the listener stands in an acoustic room of a released map, ONE
    /// MetaXRAudioRoomAcousticProperties (the plugin's single global shoebox room, centred on the listener) is fed that
    /// room's invented class preset and the plugin's acoustic model is switched to ShoeboxRoom; outdoors, and when the
    /// switch is turned off or the raid ends, the shipped model and the plugin's default room come back. All the work
    /// happens on a room change, a change of any ShotAcousticsLab key (materials, clutter, size cap: the tuning version
    /// counts them) or an audio configuration change (device switch, AudioSettings.Reset: the model is then re-sent to
    /// the native side, whose copy the plugin's managed value cannot vouch for); the component itself makes two small
    /// native calls per frame by Meta's design.
    /// The class table is tuned live in the developer config section ShotAcousticsLab (docs/progress/shot-acoustics.md):
    /// the first host measurement showed that the near-perfectly reflective first presets make a shop ring louder and
    /// longer than the hall, so every material and clutter factor is a key, read on each application.
    /// Host test: GPU-2 2026-10-15/16 on AirPit, Equator and Bay5 (ReverbPulse buses Gunshots, GunshotsFirstPerson and
    /// GunshotsThirdPerson, hall vs shop vs switch off); evidence in docs/progress/shot-acoustics.md.
    /// </summary>
    internal static class RoomShotAcoustics
    {
        internal const string Tag = "[ArenaMapPort][ShotAcoustics]";
        const string RoomObjectName = "ArenaMapPort Shot Acoustics Room";

        static readonly Dictionary<string, ConfigEntry<bool>> Enabled = new Dictionary<string, ConfigEntry<bool>>();
        const string LabSection = "ShotAcousticsLab";
        static ClassEntries _hall, _shop, _metal;
        static ConfigEntry<float> _hallMaxDimension;
        static int _tuningVersion; // counts every change of a ShotAcousticsLab key; the model compares it with the applied one

        static NativeSpatialAudio.State _owner;
        static bool _failed;
        static int _appliedRoomId = RoomShotAcousticsModel.NoRoom;
        static int _appliedTuningVersion;
        static bool _hasSavedModel, _roomPushed;
        static bool _audioReset; // AudioSettings.OnAudioConfigurationChanged since the last application
        static int _audioClient; // 0 = not decided yet, 1 = a client that can be heard, -1 = headless / no graphics device
        static int _savedModel;
        static GameObject _roomObject;
        static Behaviour _room; // the plugin's room component; typed through MetaPlugin only

        internal static void Install()
        {
            // The same maps as the other per-map audio keys (every released map), so a newly released map gets its switch.
            foreach (var map in ArenaMapRegistry.All)
                Enabled[map.Key] = Plugin.Instance.Settings.Bind("Audio (" + map.Key + ")", "RoomShotAcoustics", true,
                    "Per-room gunshot reverb, on by default. The acoustic room you stand in (hall, shop, metal body) selects " +
                    "a shoebox room model for the Meta XR Audio plugin, so shots ring differently in a big hall than in a " +
                    "small shop. Outdoors the shipped model stays. The room data is INVENTED: neither EFT nor Arena provides " +
                    "these values, they are a design choice tuned by measurement and ear. Off restores the shipped gunshot " +
                    "acoustics. Live. This is a Settings key: in player mode it is visible and changeable only with " +
                    "General/DeveloperMode enabled.");
            // The lab keys (developer config): one tuning round by measurement and ear, no rebuild. Defaults are the model's
            // INVENTED starting table. The materials are the plugin's MaterialPreset names, offered as a list per key.
            var defaults = RoomShotTuning.Default;
            _hall = BindClass("Hall", defaults.Hall,
                "A big enclosed space (AirPit's main and passport hall: floor edge of 40 m or more).");
            _shop = BindClass("Shop", defaults.Shop, "Every other indoor room: shops, booths, offices.");
            _metal = BindClass("Metal", defaults.Metal, "Narrow steel bodies (the airliner rooms; IndoorMetalThin).");
            _hallMaxDimension = Plugin.Instance.Settings.Bind(LabSection, "HallMaxDimension", defaults.HallMaxDimension,
                new ConfigDescription("Largest shoebox edge in metres (the 111 m hall is capped to this). In practice only halls " +
                    "reach it, but it caps every indoor class. Invented tuning value. Live; re-applies the current room.",
                    new AcceptableValueRange<float>(10f, 200f)));
            _hallMaxDimension.SettingChanged += MarkTuningChanged;
            // Same pattern as RoomReverb: a device switch or AudioSettings.Reset can drop the plugin's native state while its
            // managed model value still reads ShoeboxRoom, so the next Tick re-applies the current room.
            AudioSettings.OnAudioConfigurationChanged += deviceChanged => _audioReset = true;
        }

        sealed class ClassEntries
        {
            internal ConfigEntry<string> Walls, Ceiling, Floor;
            internal ConfigEntry<float> Clutter;
        }

        // The list of one material key. BepInEx clamps any value outside an AcceptableValueList to the list's FIRST entry (on
        // loading the .cfg, and on every set), so the key's own default leads its list: a typo in the file lands on the default
        // (RoomShotAcousticsModel.MaterialChoices), not on the enum's first material.
        static AcceptableValueList<string> MaterialList(string keyDefault) =>
            new AcceptableValueList<string>(RoomShotAcousticsModel.MaterialChoices(keyDefault));

        static ClassEntries BindClass(string name, RoomShotClassTuning defaults, string rooms)
        {
            const string suffix = " INVENTED starting value, tune by measurement and ear (docs/progress/shot-acoustics.md). " +
                "Live; re-applies the current room. Only used while RoomShotAcoustics is on. A value outside the list becomes the default.";
            var entries = new ClassEntries
            {
                Walls = Plugin.Instance.Settings.Bind(LabSection, name + "Walls", defaults.Walls,
                    new ConfigDescription(rooms + " Meta XR Audio material of the four side walls (right, left, front, back)." + suffix, MaterialList(defaults.Walls))),
                Ceiling = Plugin.Instance.Settings.Bind(LabSection, name + "Ceiling", defaults.Ceiling,
                    new ConfigDescription(rooms + " Meta XR Audio material of the ceiling." + suffix, MaterialList(defaults.Ceiling))),
                Floor = Plugin.Instance.Settings.Bind(LabSection, name + "Floor", defaults.Floor,
                    new ConfigDescription(rooms + " Meta XR Audio material of the floor." + suffix, MaterialList(defaults.Floor))),
                Clutter = Plugin.Instance.Settings.Bind(LabSection, name + "Clutter", defaults.Clutter,
                    new ConfigDescription(rooms + " Clutter factor: diffuses the reflections and reverberation to simulate objects in the room, 0 = a completely empty room (Meta's description)." + suffix,
                        new AcceptableValueRange<float>(0f, 1f)))
            };
            entries.Walls.SettingChanged += MarkTuningChanged;
            entries.Ceiling.SettingChanged += MarkTuningChanged;
            entries.Floor.SettingChanged += MarkTuningChanged;
            entries.Clutter.SettingChanged += MarkTuningChanged;
            return entries;
        }

        // Any lab key change: the next Tick applies the current room again even though the room is unchanged.
        static void MarkTuningChanged(object sender, EventArgs args) => _tuningVersion++;

        static RoomShotClassTuning Read(ClassEntries e) => new RoomShotClassTuning(e.Walls.Value, e.Ceiling.Value, e.Floor.Value, e.Clutter.Value);

        // The current lab values. A name outside a key's list is already clamped to that key's default by the entry itself; the
        // model still validates them (an unknown name falls back to the class default), the guard for any other path.
        static RoomShotTuning ReadTuning() =>
            new RoomShotTuning(Read(_hall), Read(_shop), Read(_metal), _hallMaxDimension.Value);

        /// <summary>Called every frame from NativeSpatialAudio.RoomToneRunner. Never throws.</summary>
        internal static void Tick(NativeSpatialAudio.State state)
        {
            try
            {
                if (state != _owner)
                {
                    StopQuietly();
                    _owner = state;
                }
                if (state == null || _failed) return;
                if (!ArenaMapRaid.IsLoaded()) return;
                if (!Enabled.TryGetValue(state.MapKey, out var enabled) || !enabled.Value)
                {
                    Revert(); // the switch was turned off during the raid
                    return;
                }
                if (!state.Finished || state.Released || state.System == null ||
                    !MonoBehaviourSingleton<BetterAudio>.Instantiated ||
                    !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started ||
                    !HasAudioOutput()) return;
                var room = state.System.ListenerCurrentRoom;
                if (room == null || !room.IsValid) return;
                int version = _tuningVersion; // read before the values in Apply: a newer value never gets an older version
                bool reassert = _audioReset && _appliedRoomId != RoomShotAcousticsModel.NoRoom;
                if (!RoomShotAcousticsModel.NeedsApply(_appliedRoomId, room.ID, _appliedTuningVersion, version, reassert)) return;
                Apply(state, room, version, reassert);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>Restores the shipped acoustics and forgets the raid. Also the NativeSpatialAudio cleanup step.</summary>
        internal static void Stop()
        {
            try { Revert(); }
            finally
            {
                _owner = null; _failed = false; _appliedRoomId = RoomShotAcousticsModel.NoRoom; _audioReset = false;
            }
        }

        static void StopQuietly()
        {
            try { Stop(); }
            catch (Exception e) { Plugin.Log.LogError($"{Tag} restoring the shipped acoustics failed: {e}"); }
        }

        // EFT creates the AudioListener object (with its AudioListenerConsistencyManager) on EVERY client in
        // ClientApplication.StartTask, a headless one included, so its presence proves nothing. What keeps a headless client
        // away from the plugin's native calls is (1) this batch-mode / null-graphics-device test (the way Fika's dedicated
        // host is launched; not verified here) and (2) a listener parked at the reset position (0,-1000,0), where
        // ListenerCurrentRoom never becomes valid. Decided once: neither can change in a running process.
        static bool HasAudioOutput()
        {
            if (_audioClient == 0)
                _audioClient = RoomShotAcousticsModel.IsAudioClient(Application.isBatchMode,
                    SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) ? 1 : -1;
            return _audioClient > 0 && Singleton<AudioListenerConsistencyManager>.Instantiated;
        }

        static void Apply(NativeSpatialAudio.State state, ISpatialAudioRoom room, int tuningVersion, bool reassert)
        {
            // The live room carries the same type, flags and bounds as the graph JSON (the graph's room_id repeats on
            // inactive source rooms, ListenerCurrentRoom is already the active one), so no JSON lookup is needed.
            var size = room.Bounds.size;
            var roomClass = RoomShotAcousticsModel.Resolve((int)room.Type, room.IsOutdoor, room.IsIsolated,
                size.x, size.y, size.z, ReadTuning(), out var preset);
            if (preset == null)
            {
                if (_hasSavedModel) MetaPlugin.SetModel(_savedModel);
                if (_room != null) _room.enabled = false; // the plugin's room is idle while the shipped model runs
            }
            else
            {
                if (_room == null) _room = CreateRoom(state);
                MetaPlugin.Configure(_room, preset);
                MetaPlugin.UpdateNow(_room); // native room now, not on the component's next Update
                _roomPushed = true;
                _room.enabled = true;
                if (!_hasSavedModel) { _savedModel = MetaPlugin.GetModel(); _hasSavedModel = true; }
                MetaPlugin.SetShoebox();
            }
            // After an audio configuration change the setters above may be no-ops (the managed value already matches), so
            // send the model to the native side explicitly. Only when this repair ever changed the model.
            string resent = reassert && _hasSavedModel ? " model-resent-rc=" + MetaPlugin.ResendModel() : "";
            _appliedRoomId = room.ID;
            _appliedTuningVersion = tuningVersion;
            _audioReset = false;
            var c = CultureInfo.InvariantCulture;
            Plugin.Log.LogDebug($"{Tag} {state.MapKey}: room={room.ID}/{room.Type} class={roomClass} " +
                $"dims={preset?.DimensionsText ?? "none"} materials={preset?.MaterialsText ?? "none"} " +
                $"clutter={(preset != null ? preset.ClutterFactor.ToString("0.00", c) : "none")} model={MetaPlugin.ModelName()}{resent}" +
                (preset != null && preset.UsedClassDefaults ? $" class-default-fallback=[{preset.CorrectionsText}]" : ""));
        }

        static Behaviour CreateRoom(NativeSpatialAudio.State state)
        {
            // The plugin allows exactly one room component and this repair is the only creator of one (zero existed on the
            // host; the plugin's own startup check reports more than one). There is deliberately no scene search for a
            // foreign one here: FindObjectsOfType over a ported scene costs about 30 ms on the main thread, a hitch in live play.
            if (state.Root == null) throw new InvalidOperationException("acoustic root is gone");
            MetaPlugin.RequireUpdateMethod(); // fail before touching anything if the plugin changed
            _roomObject = new GameObject(RoomObjectName);
            _roomObject.transform.SetParent(state.Root.transform, false);
            return MetaPlugin.AddRoom(_roomObject);
        }

        // Puts the shipped acoustics back: model first, then the plugin's default room (only if we ever pushed one). Every
        // step runs even when an earlier one throws; the exception reaches the caller afterwards.
        static void Revert()
        {
            try
            {
                if (_hasSavedModel) MetaPlugin.SetModel(_savedModel);
            }
            finally
            {
                try
                {
                    if (_roomObject != null) Object.DestroyImmediate(_roomObject);
                    if (_roomPushed) MetaPlugin.RestorePluginDefaultRoom();
                }
                finally
                {
                    _roomObject = null; _room = null; _roomPushed = false;
                    _hasSavedModel = false; _appliedRoomId = RoomShotAcousticsModel.NoRoom; _audioReset = false;
                }
            }
        }

        static void Fail(Exception e)
        {
            _failed = true;
            Plugin.Log.LogError($"{Tag} shot acoustics stopped for this raid, shipped acoustics restored: {e}");
            try { Revert(); }
            catch (Exception restore) { Plugin.Log.LogError($"{Tag} restoring the shipped acoustics also failed: {restore}"); }
        }

        /// <summary>
        /// Everything that names a Meta XR Audio type lives here, in NoInlining methods, so that no other method of this
        /// class needs the plugin's types to be resolved: a vanilla map or the hideout never JITs one of them.
        /// </summary>
        static class MetaPlugin
        {
            static readonly Dictionary<string, MetaXRAudioRoomAcousticProperties.MaterialPreset> Materials =
                new Dictionary<string, MetaXRAudioRoomAcousticProperties.MaterialPreset>();
            static MethodInfo _update;

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static string ModelName() => MetaXRAcousticSettings.Instance.AcousticModel.ToString();

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static int GetModel() => (int)MetaXRAcousticSettings.Instance.AcousticModel;

            // The setter calls the native SetAcousticModel only when the value differs.
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void SetModel(int model) => MetaXRAcousticSettings.Instance.AcousticModel = (Meta.XR.Acoustics.AcousticModel)model;

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void SetShoebox() => MetaXRAcousticSettings.Instance.AcousticModel = Meta.XR.Acoustics.AcousticModel.ShoeboxRoom;

            // Sends the managed model value to the native side unconditionally (the plugin's own ApplyAllSettings does the
            // same at startup); the setters above skip an unchanged value. Returns the native result code.
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static int ResendModel() =>
                MetaXRAcousticNativeInterface.Interface.SetAcousticModel(MetaXRAcousticSettings.Instance.AcousticModel);

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static Behaviour AddRoom(GameObject go) => go.AddComponent<MetaXRAudioRoomAcousticProperties>();

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void Configure(Behaviour behaviour, RoomShotPreset preset)
            {
                var room = (MetaXRAudioRoomAcousticProperties)behaviour;
                room.lockPositionToListener = true; // a shot is heard from where the listener stands
                room.width = preset.Width; room.height = preset.Height; room.depth = preset.Depth;
                room.rightMaterial = Material(preset.Right); room.leftMaterial = Material(preset.Left);
                room.ceilingMaterial = Material(preset.Ceiling); room.floorMaterial = Material(preset.Floor);
                room.frontMaterial = Material(preset.Front); room.backMaterial = Material(preset.Back);
                room.clutterFactor = preset.ClutterFactor;
            }

            static MetaXRAudioRoomAcousticProperties.MaterialPreset Material(string name)
            {
                if (!Materials.TryGetValue(name, out var value))
                    Materials[name] = value = (MetaXRAudioRoomAcousticProperties.MaterialPreset)Enum.Parse(
                        typeof(MetaXRAudioRoomAcousticProperties.MaterialPreset), name);
                return value;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void RequireUpdateMethod() => UpdateMethod();

            static MethodInfo UpdateMethod()
            {
                // Private in the plugin: it converts the presets to absorption coefficients and calls the native
                // SetAdvancedBoxRoomParameters / SetRoomClutterFactor.
                return _update ?? (_update = AccessTools.Method(typeof(MetaXRAudioRoomAcousticProperties), "Update")
                    ?? throw new MissingMethodException(nameof(MetaXRAudioRoomAcousticProperties), "Update"));
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void UpdateNow(Behaviour room) => UpdateMethod().Invoke(room, null);

            // Same recipe as the plugin's own CheckSceneHasRoom at startup ("No Meta XR Audio Room found, setting default
            // room"): a fresh component holds the plugin defaults, one Update pushes them to the native side.
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal static void RestorePluginDefaultRoom()
            {
                var temporary = new GameObject("ArenaMapPort Temporary Room");
                try { UpdateNow(temporary.AddComponent<MetaXRAudioRoomAcousticProperties>()); }
                finally { Object.DestroyImmediate(temporary); }
            }
        }
    }
}
