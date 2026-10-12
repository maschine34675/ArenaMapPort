using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ArenaMapPort.Shared;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.DataProviding;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Rehydrates Arena's authored acoustic graph before EFT loads its original propagation bake.
    /// All references are wired while inactive: the native loader reads portal endpoints BEFORE
    /// AudioRoomStorage initializes rooms. Missing/invalid packages retain the early port's safe
    /// uninitialized path. Native initialization is bounded and caller cancellation stays cancellation.
    /// Runtime acceptance and limitations are recorded in docs/progress/spatial-audio.md.
    /// </summary>
    internal static class NativeSpatialAudio
    {
        internal const string Tag = "[ArenaMapPort][Acoustics]";
        static readonly Dictionary<string, ConfigEntry<bool>> Enabled = new Dictionary<string, ConfigEntry<bool>>();
        static State _state;
        internal static State Current => _state;
        static readonly HashSet<SpatialAudioCrossSceneGroup> OwnedGroups = new HashSet<SpatialAudioCrossSceneGroup>();

        internal sealed class State
        {
            internal string MapKey;
            internal SpatialAudioSystem System;
            internal GameObject Root;
            internal SpatialAudioCrossSceneGroup Group;
            internal SpatialAudioLocationInfo Info, PreviousInfo;
            internal AudioOcclusionSettings Settings, PreviousSettings;
            internal SpatialAudioPoolsConfig PreviousPools;
            internal CancellationTokenSource Cancellation;
            internal CancellationToken CallerToken;
            internal RoomToneController RoomTones;
            internal readonly List<AudioClip> Clips = new List<AudioClip>();
            internal readonly Dictionary<RoomAmbientData, string> ToneBindings = new Dictionary<RoomAmbientData, string>();
            internal string PackageDirectory;
            internal bool RefreshTones;
            internal bool Ready, Finished, Released;
            internal JObject Data;
            internal SpatialAudioDataContainer Container;
            internal AmbientBusLease IndoorBus;
        }

        internal static void Install(Harmony harmony)
        {
            // Maps that have a generated acoustics/<key> package (csproj ArenaMapPortStageAcoustics). RoomReverb and
            // PrecipitationAudio list the same maps: RoomReverb indexes its own Enabled table with this state's MapKey.
            foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit })
                Enabled[map.Key] = Plugin.Instance.Settings.Bind("Audio (" + map.Key + ")", "NativeSpatialAudio", true,
                    "Use original rooms, openings and baked sound paths for native wall/room attenuation and room tones. " +
                    "Takes effect next raid. Missing or invalid data retains unoccluded playback.");
            // Vanilla clears ALL groups when any one is destroyed. Restrict that cleanup to our own
            // group so preparation failure cannot unregister a donor or another mod's group.
            harmony.Patch(AccessTools.Method(typeof(SpatialAudioCrossSceneGroup), "OnDestroy"),
                prefix: new HarmonyMethod(typeof(NativeSpatialAudio), nameof(GroupDestroyPrefix)));
            SceneManager.sceneUnloaded += scene =>
            {
                if (_state != null && ArenaMapRaid.MapForScriptsScene(scene.name)?.Key == _state.MapKey)
                    Release(_state, true);
            };
            Plugin.Instance.gameObject.AddComponent<RoomToneRunner>();
            AudioSettings.OnAudioConfigurationChanged += deviceChanged =>
            {
                // Unity discards script-generated clip data on AudioSettings.Reset. Rebuild on
                // the main thread only if room tones were already started in this raid.
                if (_state?.RoomTones != null) _state.RefreshTones = true;
            };
        }

        static bool GroupDestroyPrefix(SpatialAudioCrossSceneGroup __instance)
        {
            if (!OwnedGroups.Remove(__instance)) return true;
            SpatialAudioCrossSceneGroup.AllCrossGroups.Remove(__instance);
            return false;
        }

        internal static bool Prepare(SpatialAudioSystem system, ref CancellationToken token, out State state)
        {
            state = null;
            string key = ArenaMapRaid.CurrentMap?.Key;
            if (key == null || !Enabled.TryGetValue(key, out var enabled) || !enabled.Value) return false;
            try
            {
                if (_state != null) throw new InvalidOperationException("An owned acoustic graph already exists");
                var dir = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "acoustics", key);
                var data = JObject.Parse(File.ReadAllText(Path.Combine(dir, "spatial-audio.json")));
                Require((int)data["schema_version"] == 1 && (string)data["map_key"] == key, "package identity");
                var bakePath = CheckedFile(dir, (JObject)data["bake"]);
                Require(data["systems"].Count() == 1 && data["groups"].Count() == 1, "one native graph required");
                Require(!Object.FindObjectsOfType<SpatialAudioRoom>().Any() &&
                        !Object.FindObjectsOfType<BaseSpatialAudioPortal>().Any(), "foreign acoustic graph present");
                state = new State { MapKey = key, System = system, Data = data, CallerToken = token,
                    PreviousInfo = system._locationInfo, PreviousSettings = system.OcclusionSettings,
                    PreviousPools = system.poolsConfig };
                _state = state;
                state.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                state.Cancellation.CancelAfter(TimeSpan.FromSeconds(30));
                state.Info = ScriptableObject.CreateInstance<SpatialAudioLocationInfo>();
                var info = data["systems"][0]["location_info"];
                state.Info.name = (string)info["name"];
                state.Info.relativeBakeDataPath = bakePath; // rooted Path.Combine operand; plugin-local file
                var settings = info["bake_settings"];
                state.Info.bakeSettings = new LocationBakeSettings {
                    maxDepthIndoorToIndoor = (int)settings["max_depth_indoor_to_indoor"],
                    maxDepthIndoorToOutdoor = (int)settings["max_depth_indoor_to_outdoor"],
                    maxDepthOutdoorToOutdoor = (int)settings["max_depth_outdoor_to_outdoor"],
                    maxDistanceOutdoorToOutdoor = (int)settings["max_distance_outdoor_to_outdoor"],
                    maxDistanceIndoorToOutdoor = (int)settings["max_distance_indoor_to_outdoor"],
                    maxDistanceIndoorToIndoor = (int)settings["max_distance_indoor_to_indoor"] };
                state.Info.roomsCount = (uint)info["rooms_count"];
                state.Info.portalsCount = (uint)info["portals_count"];
                state.Info.routesCount = (uint)info["routes_count"];
                state.Info.roomPairsCount = (uint)info["room_pairs_count"];
                state.Info.maxRoutesCount = (uint)info["max_routes_count"];
                state.Info.maxRoutePortalsCount = (uint)info["max_route_portals_count"];
                state.Settings = ScriptableObject.CreateInstance<AudioOcclusionSettings>();
                system.OcclusionSettings = state.Settings; // native method_0 applies EFT backend settings
                var pools = data["systems"][0]["pools_config"];
                system.poolsConfig = new SpatialAudioPoolsConfig {
                    parallelPropagationCalculatorsPreCacheCount = (int)pools["parallel_propagation_calculators_pre_cache_count"],
                    combinedCalculatorsPreCacheCount = (int)pools["combined_calculators_pre_cache_count"],
                    transmissionCalculatorsPoolCount = (int)pools["transmission_calculators_pool_count"],
                    pendingCompleteCalculatorsCount = (int)pools["pending_complete_calculators_count"] };
                system._locationInfo = state.Info;
                BuildGraph(state, dir);
                state.Ready = true;
                token = state.Cancellation.Token;
                Plugin.Log.LogDebug($"{Tag} {key}: prepared rooms={state.Info.roomsCount}, portals={state.Info.portalsCount}, " +
                    $"bake pairs={state.Info.roomPairsCount}, routes={state.Info.routesCount}, sha256={(string)data["bake"]["sha256"]}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} Preparation rejected; using unoccluded fallback: {e}");
                if (state != null) Release(state, false);
                state = null;
                return false;
            }
        }

        internal static async Task ObserveInitialization(Task original, State state)
        {
            state.Container = DataProvider.GetData<SpatialAudioDataContainer>();
            try
            {
                await original;
                state.CallerToken.ThrowIfCancellationRequested();
                Require(SpatialAudioSystem.Initialized && !state.Released, "native initialization incomplete");
                var container = DataProvider.GetData<SpatialAudioDataContainer>();
                Require(container != null && container.RoomsByID.Count == state.Info.roomsCount &&
                    container.PortalsByID.Count == state.Info.portalsCount &&
                    container.RoomPairsByID.Count == state.Info.roomPairsCount &&
                    container.RoutesDataCount == state.Info.roomPairsCount &&
                    container.RoomPairsByID.Values.Sum(pair => pair.Routes.Length) == state.Info.routesCount, "loaded bake counts differ");
                Require(state.Group.AudioRooms.All(r => r.IsValid), "room initialization failed");
                state.Finished = true;
                state.Cancellation.CancelAfter(Timeout.Infinite);
                Plugin.Log.LogDebug($"{Tag} {state.MapKey}: native initialized; valid rooms={container.RoomsByID.Count}, " +
                    $"portals={container.PortalsByID.Count}, pairs={container.RoomPairsByID.Count}, routes={container.RoomPairsByID.Values.Sum(pair => pair.Routes.Length)}; room tones wait for raid start");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} Native initialization failed; using unoccluded fallback: {e}");
                Release(state, true);
                state.CallerToken.ThrowIfCancellationRequested();
            }
        }

        static void BuildGraph(State state, string dir)
        {
            state.PackageDirectory = dir;
            var data = state.Data;
            var nodes = new Dictionary<long, GameObject>();
            var colliders = new Dictionary<long, BoxCollider>();
            var areas = new Dictionary<long, AudioTriggerArea>();
            var rooms = new Dictionary<long, SpatialAudioRoom>();
            var portals = new Dictionary<long, SpatialAudioPortal>();
            var clips = new Dictionary<string, AudioClip>();
            foreach (JObject clip in data["clips"])
            {
                CheckedFile(dir, (JObject)clip["decoded"]);
                // Do not create procedural AudioClips before LocalGame.ResetAudioBuffer:
                // that reset loses their PCM data, even if playback starts afterward.
                clips.Add((string)clip["id"], null);
            }
            state.Root = new GameObject("ArenaMapPort Native Acoustics " + state.MapKey);
            state.Root.SetActive(false);
            SceneManager.MoveGameObjectToScene(state.Root, SceneManager.GetSceneByName(ArenaMapRaid.CurrentMap.ScriptsSceneName));
            foreach (var node in data["nodes"])
            {
                var go = new GameObject((string)node["name"]);
                go.transform.SetParent(node["parent_id"].Type == JTokenType.Null ? state.Root.transform : nodes[(long)node["parent_id"]].transform, false);
                go.transform.localPosition = Vector(node["position"]);
                var r = node["rotation"];
                var q = new Quaternion((float)r["x"], (float)r["y"], (float)r["z"], (float)r["w"]);
                Require(Math.Abs(Quaternion.Dot(q, q) - 1) < 0.01f, "invalid node rotation");
                go.transform.localRotation = q;
                go.transform.localScale = Vector(node["scale"]);
                Require(go.transform.localScale.x >= 0 && go.transform.localScale.y >= 0 && go.transform.localScale.z >= 0, "negative node scale");
                go.layer = (int)node["layer"];
                go.SetActive((bool)node["active_self"]);
                nodes.Add((long)node["id"], go);
            }
            foreach (var item in data["colliders"])
            {
                var c = nodes[(long)item["node_id"]].AddComponent<BoxCollider>();
                c.center = Vector(item["center"]); c.size = Vector(item["size"]);
                Require(c.size.x > 0 && c.size.y > 0 && c.size.z >= 0, "invalid acoustic volume");
                c.isTrigger = (bool)item["is_trigger"]; c.enabled = (bool)item["enabled"];
                Require(c.isTrigger, "acoustic collider must be a trigger");
                colliders.Add((long)item["id"], c);
            }
            foreach (var item in data["areas"])
            {
                var a = nodes[(long)item["node_id"]].AddComponent<AudioTriggerArea>();
                a.areaCollider = colliders[(long)item["collider_id"]];
                a._validatePlayerCenterInside = (bool)item["validate_player_center_inside"];
                a.enabled = (bool)item["enabled"];
                areas.Add((long)item["id"], a);
            }
            foreach (var item in data["rooms"])
            {
                var room = nodes[(long)item["node_id"]].AddComponent<SpatialAudioRoom>();
                room._iD = (short)item["room_id"];
                room.priority = (int)item["priority"];
                room.WallOcclusion = (float)item["wall_occlusion"];
                room.Outdoor = (bool)item["outdoor"]; room.Isolated = (bool)item["isolated"];
                room._type = (EAudioRoomTypeMask)(int)item["type"];
                room.FitToGeometry = (bool)item["fit_to_geometry"];
                room.OnlyWired = (bool)item["only_wired"];
                room._roomSize = (float)item["room_size"];
                room._bounds = new Bounds(Vector(item["bounds"]["center"]), 2 * Vector(item["bounds"]["extents"]));
                room.Areas = item["area_ids"].Select(id => areas[(long)id]).ToList();
                room.AmbientData = ReadAmbient(item["ambient_data"], clips);
                if (item["ambient_data"]["room_tone"].Type != JTokenType.Null)
                    state.ToneBindings.Add(room.AmbientData, (string)item["ambient_data"]["room_tone"]);
                room.Name = (string)item["path"];
                room.enabled = (bool)item["enabled"];
                rooms.Add((long)item["id"], room);
            }
            foreach (var item in data["portals"])
            {
                var p = nodes[(long)item["node_id"]].AddComponent<SpatialAudioPortal>();
                p._iD = (short)item["portal_id"]; p._transmission = (float)item["transmission"];
                p.Depth = (float)item["depth"]; p.portalName = (string)item["portal_name"];
                p.FitToGeometry = (bool)item["fit_to_geometry"]; p.AutoRotate = (bool)item["auto_rotate"];
                p.portalType = (BaseSpatialAudioPortal.PortalType)(int)item["portal_type"];
                p.state = (BaseSpatialAudioPortal.PortalState)(byte)item["state"];
                p.portalCollider = colliders[(long)item["collider_id"]];
                p.traversalMaxCost = (float)item["traversal_max_cost"]; p.ToOutdoor = (bool)item["to_outdoor"];
                p.openEnvelope = Curve(item["open_envelope"]); p.closeEnvelope = Curve(item["close_envelope"]);
                p.openFadeTime = (float)item["open_fade_time"]; p.closeFadeTime = (float)item["close_fade_time"];
                p._connectedRooms = item["connected_room_ids"].Select(id => rooms[(long)id]).ToList();
                p.DoorID = (string)item["door_id"] ?? "";
                p.enabled = (bool)item["enabled"];
                Require(!(bool)item["active_in_hierarchy"] || p._connectedRooms.Count == 2, "active portal requires two source rooms");
                portals.Add((long)item["id"], p);
            }
            foreach (var item in data["rooms"])
                rooms[(long)item["id"]].roomConnections = item["connections"].Select(c => new SpatialAudioRoom.RoomConnection {
                    connectedRoom = rooms[(long)c["room_id"]],
                    connectingPortals = c["portal_ids"].Select(id => (BaseSpatialAudioPortal)portals[(long)id]).ToArray()
                }).ToList();
            var groupData = data["groups"][0];
            state.Group = nodes[(long)groupData["node_id"]].AddComponent<SpatialAudioCrossSceneGroup>();
            OwnedGroups.Add(state.Group);
            state.Root.SetActive(true);
            // Explicit source group arrays exclude inactive originals, which reuse valid native IDs.
            state.Group.AudioRooms = groupData["room_ids"].Select(id => rooms[(long)id]).ToList();
            state.Group.AudioPortals = groupData["portal_ids"].Select(id => (BaseSpatialAudioPortal)portals[(long)id]).ToList();
            Require(state.Group.AudioRooms.All(r => r.isActiveAndEnabled && r.IsRoomValid()), "invalid active room");
            Require(state.Group.AudioPortals.All(p => p.isActiveAndEnabled && p.FrontRoom.isActiveAndEnabled && p.BackRoom.isActiveAndEnabled), "invalid active portal");
            Require(state.Group.AudioRooms.Select(r => r.ID).Distinct().Count() == state.Info.roomsCount &&
                state.Group.AudioPortals.Select(p => p.ID).Distinct().Count() == state.Info.portalsCount, "graph identity/count mismatch");
            Physics.SyncTransforms();
        }

        public sealed class RoomToneRunner : MonoBehaviour
        {
            bool _reportedFailure;
            void Update()
            {
                var state = _state;
                RoomReverb.Tick(state);
                if (state == null) { _reportedFailure = false; return; }
                if (_reportedFailure || !state.Finished || (state.RoomTones != null && !state.RefreshTones) ||
                    !Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started ||
                    state.System == null || state.System.ListenerCurrentRoom?.IsValid != true) return;
                try
                {
                    if (state.RefreshTones)
                    {
                        ReleaseRoomTones(state);
                        state.RefreshTones = false;
                    }
                    var clips = new Dictionary<string, AudioClip>();
                    foreach (JObject clip in state.Data["clips"])
                    {
                        var native = ReadClip(state.PackageDirectory, clip);
                        state.Clips.Add(native);
                        clips.Add((string)clip["id"], native);
                    }
                    foreach (var binding in state.ToneBindings) binding.Key.RoomTone = clips[binding.Value];
                    // LocalGame resets sources and mixer buses during deployment. Start only after
                    // Started, as the existing ambient driver does; earlier sources are stopped.
                    // Equator and AirPit: their ambient driver already holds the interior bus (AmbientAudioProfiles); Bay5's
                    // driver holds only the exterior bus, so its room tones take the shared interior lease themselves.
                    if (state.IndoorBus == null && !AmbientAudioProfiles.LeasesBus(state.MapKey, AmbientBus.In) &&
                        MonoBehaviourSingleton<BetterAudio>.Instantiated)
                    {
                        var mixer = MonoBehaviourSingleton<BetterAudio>.Instance.Master;
                        state.IndoorBus = AmbientBusLease.Acquire(mixer, "AmbientInVolume");
                    }
                    // The existing ambient driver owns its original players. This controller owns
                    // only the newly recovered room tones, without creating a second ambient manager.
                    state.RoomTones = new RoomToneController();
                    Plugin.Log.LogDebug($"{Tag} {state.MapKey}: room-tone controller started after audio reset");
                }
                catch (Exception e) { _reportedFailure = true; Plugin.Log.LogError($"{Tag} Room-tone startup: {e}"); }
            }
        }

        static RoomAmbientData ReadAmbient(JToken d, Dictionary<string, AudioClip> clips) => new RoomAmbientData {
            FadeInSeconds = (float)d["fade_in_seconds"], FadeOutSeconds = (float)d["fade_out_seconds"],
            RoomToneVolume = (float)d["room_tone_volume"],
            RoomTone = d["room_tone"].Type == JTokenType.Null ? null : clips[(string)d["room_tone"]],
            OutdoorAmbientVolume = (float)d["outdoor_ambient_volume"], OutdoorAmbientHighCutFreq = (float)d["outdoor_ambient_high_cut_freq"],
            UseVolumeRange = (bool)d["use_volume_range"], PrecipitationVolume = (float)d["precipitation_volume"],
            PrecipitationVolumeRange = new Vector2((float)d["precipitation_volume_range"][0], (float)d["precipitation_volume_range"][1]),
            ReverbPreset = (string)d["reverb_preset"], ReverbSendVolume = (float)d["reverb_send_volume"] };

        static AudioClip ReadClip(string dir, JObject clip)
        {
            var decoded = (JObject)clip["decoded"];
            string path = CheckedFile(dir, decoded);
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                Require(new string(reader.ReadChars(4)) == "RIFF", "room tone WAV header");
                reader.ReadUInt32(); Require(new string(reader.ReadChars(4)) == "WAVE", "room tone WAV format");
                int channels = 0, rate = 0; byte[] pcm = null;
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    string id = new string(reader.ReadChars(4)); int size = reader.ReadInt32();
                    Require(size >= 0 && reader.BaseStream.Position + size <= reader.BaseStream.Length, "room tone WAV chunk");
                    long next = reader.BaseStream.Position + size + (size & 1);
                    if (id == "fmt ") {
                        Require(size >= 16 && reader.ReadUInt16() == 1, "room tone PCM16 format");
                        channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                        reader.ReadUInt32(); reader.ReadUInt16(); Require(reader.ReadUInt16() == 16, "room tone PCM16 width");
                    }
                    else if (id == "data") pcm = reader.ReadBytes(size);
                    reader.BaseStream.Position = next;
                }
                Require(channels == (int)decoded["channels"] && rate == (int)decoded["sampleRate"] &&
                    pcm != null && pcm.Length == (int)decoded["frames"] * channels * 2, "room tone decoded dimensions");
                var samples = new float[pcm.Length / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
                var result = AudioClip.Create((string)clip["name"], samples.Length / channels, channels, rate, false);
                if (!result.SetData(samples, 0))
                {
                    Object.Destroy(result);
                    throw new InvalidDataException("room tone PCM upload failed");
                }
                return result;
            }
        }

        static AnimationCurve Curve(JToken value)
        {
            var curve = new AnimationCurve(value["keys"].Select(k => new Keyframe((float)k["time"], (float)k["value"],
                (float)k["inSlope"], (float)k["outSlope"], (float)k["inWeight"], (float)k["outWeight"]) {
                    weightedMode = (WeightedMode)(int)k["weightedMode"] }).ToArray());
            return curve;
        }

        static Vector3 Vector(JToken value)
        {
            var v = value is JArray ? new Vector3((float)value[0], (float)value[1], (float)value[2]) :
                new Vector3((float)value["x"], (float)value["y"], (float)value["z"]);
            Require(!float.IsNaN(v.sqrMagnitude) && !float.IsInfinity(v.sqrMagnitude), "nonfinite vector");
            return v;
        }

        static string CheckedFile(string dir, JObject item)
        {
            var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(dir, (string)item["file"]));
            Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "package path escaped directory");
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
            {
                Require(stream.Length == (long)item["bytes"], "package length: " + path);
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                Require(hash == (string)item["sha256"], "package hash: " + path);
            }
            return path;
        }

        static object Field(object instance, string name) => instance == null ? null :
            AccessTools.Field(instance.GetType(), name)?.GetValue(instance);

        static void Release(State state, bool stopNative)
        {
            if (state.Released) return;
            state.Released = true;
            state.Cancellation?.Cancel();
            try
            {
                CleanupStep(RoomReverb.Stop);
                CleanupStep(ReverbAudioProbe.Stop);
                ReleaseRoomTones(state);
                CleanupStep(SpatialAudioProbe.Stop);
                CleanupStep(() => { state.IndoorBus?.Dispose(); state.IndoorBus = null; });
                if (stopNative && state.System != null)
                {
                    if (SpatialAudioSystem.Initialized) state.System.Dispose();
                    else
                    {
                        CleanupStep(() => (Field(state.System, "_dataLoader") as SpatialAudioDataLoader)?.StopLoadCoroutine());
                        CleanupStep(state.System.method_8);
                        CleanupStep(() => (Field(state.System, "_poolService") as SpatialAudioSystemPoolService)?.DisposePools());
                        CleanupStep(() => (Field(state.System, "_fastOccluder") as IDisposable)?.Dispose());
                        CleanupStep(() => (Field(state.System, "_multiSourceOccluder") as IDisposable)?.Dispose());
                        CleanupStep(() => (Field(state.System, "_interactiveObjectOccluder") as IDisposable)?.Dispose());
                        CleanupStep(() => (Field(state.System, "_unsubscribeOnDestroy") as Action)?.Invoke());
                        CleanupStep(() => (Field(state.System, "_audioRoomStorage") as AudioRoomStorage)?.Clear());
                        if (DataProvider.TryGetData<SpatialAudioDataContainer>(out var container) &&
                            ReferenceEquals(container, state.Container) && !container.IsDisposed)
                            CleanupStep(container.Dispose);
                        state.System.enabled = false; // no partial native Update after fallback
                    }
                    SpatialAudioSystem.Initialized = false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"{Tag} Native cleanup: {e}"); }
            finally
            {
                if (stopNative && _state == state) {
                    SpatialAudioSystem.Initialized = false;
                    if (state.System != null) state.System.enabled = false;
                }
                if (state.System != null) {
                    state.System._locationInfo = state.PreviousInfo;
                    state.System.OcclusionSettings = state.PreviousSettings;
                    state.System.poolsConfig = state.PreviousPools;
                }
                if (state.Root != null) Object.DestroyImmediate(state.Root);
                if (state.Group != null) { SpatialAudioCrossSceneGroup.AllCrossGroups.Remove(state.Group); OwnedGroups.Remove(state.Group); }
                if (state.Info != null) Object.Destroy(state.Info);
                if (state.Settings != null) Object.Destroy(state.Settings);
                state.Cancellation?.Dispose();
                if (_state == state) _state = null;
                Plugin.Log.LogDebug($"{Tag} {state.MapKey}: released graph and room tones; initialized={SpatialAudioSystem.Initialized}, groups={SpatialAudioCrossSceneGroup.AllCrossGroups.Count}");
            }
        }

        internal static void Probe()
        {
            if (!DeveloperOptions.Enabled) return;
            if (_state == null || !_state.Finished || _state.System == null) return;
            var system = _state.System;
            var room = system.ListenerCurrentRoom;
            Plugin.Log.LogDebug($"{Tag} listener room={room?.ID} name={room?.Name} type={room?.Type} valid={room?.IsValid}; " +
                $"initialized={SpatialAudioSystem.Initialized}; room-tone={_state.RoomTones?._prevEnteredRoom?.ID}");
            if (_state.RoomTones?._prevEnteredRoom is SpatialAudioRoom toneRoom &&
                Field(toneRoom, "_roomAmbientSoundPlayer") is RoomAmbientSoundPlayer player && player._source != null)
            {
                var source = player._source.source1;
                var mixer = MonoBehaviourSingleton<BetterAudio>.Instance.Master;
                mixer.GetFloat("AmbientInVolume", out float bus);
                Plugin.Log.LogDebug($"{Tag} room-tone clip={source.clip?.name} playing={source.isPlaying} " +
                    $"samples={source.timeSamples} load={source.clip?.loadState} volume={source.volume:F4} " +
                    $"indoorBusDb={bus:F1} mixer={source.outputAudioMixerGroup?.name} listenerPaused={AudioListener.pause}");
            }
            RoomReverb.Log();
            SpatialAudioProbe.Log();
            foreach (var r in _state.Group.AudioRooms)
                Plugin.Log.LogDebug($"{Tag} room {r.ID} '{r.name}' type={r.Type} center={r.Bounds.center:F2} size={r.Bounds.size:F2} areas={r.Areas.Count}");
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        static void ReleaseRoomTones(State state)
        {
            CleanupStep(() => state.RoomTones?.Dispose()); state.RoomTones = null;
            if (state.Root != null)
                foreach (var room in state.Root.GetComponentsInChildren<SpatialAudioRoom>(true))
                    CleanupStep(() => (Field(room, "_roomAmbientSoundPlayer") as RoomAmbientSoundPlayer)?.TryReleaseSourceIfNotNull());
            foreach (var binding in state.ToneBindings) binding.Key.RoomTone = null;
            foreach (var clip in state.Clips) if (clip != null) Object.Destroy(clip);
            state.Clips.Clear();
        }

        static void CleanupStep(Action step)
        {
            try { step(); }
            catch (Exception e) { Plugin.Log.LogError($"{Tag} Cleanup step: {e}"); }
        }
    }
}
