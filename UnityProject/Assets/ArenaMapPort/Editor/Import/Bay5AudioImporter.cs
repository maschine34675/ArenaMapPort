using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ArenaMapPort.Editor.Common;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.AmbientSplineEmitter;
using BezierSplineTools;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    /// <summary>Rebuilds the Equator, Bay5 and AirPit source ambient graphs with target native components.
    /// The inactive root defers stock Awake/playback until the client has real audio services.
    ///
    /// What is pinned where. The exact census of every map (players, support components, banks, clips, group
    /// mixers, inactive and looping players) is the extraction contract (tools/extract/native_contracts/&lt;stem&gt;.json),
    /// checked by validate_native_features.py on the package and again on the built bundle. This importer therefore takes
    /// its counts from the package (manifest <c>native_audio</c> entry against the hash-verified index) and fails only
    /// on an inconsistent or unsupported package; the schema decides whether the source activation/layer states are
    /// kept (schema 2: Equator, AirPit) or everything is active at layer 0 (schema 1: Bay5), never the map identity.
    /// What the package cannot carry, the Editor assembly cannot share with the client: the root name per map
    /// (<see cref="RootNameFor"/>) and the group name to native mixer pairing (<see cref="GroupMixers"/>) mirror the
    /// client's AmbientAudioProfiles.cs, and ArenaMapPort.Server.Tests/AmbientAudioProfilesTests compares them.</summary>
    public static class Bay5AudioImporter
    {
        public const string RootName = "ArenaMapPort_Bay5NativeAudio";
        public const string EquatorRootName = "ArenaMapPort_EquatorNativeAudio";
        public const string AirPitRootName = "ArenaMapPort_AirPitNativeAudio";

        // Group GameObject name -> the native mixer group the client routes it to. The package names the same mixer in
        // each group's mixerName; a mismatch means the source graph and the client routing table disagree.
        static readonly Dictionary<string, string> GroupMixers = new Dictionary<string, string>
        {
            ["CommonAmbientInGroup"] = "CommonAmbInEffects",
            ["CommonAmbientOutGroup"] = "CommonAmbOutEffects",
            ["AmbientPlayersGroup"] = "CommonAmbOutEffects",
            ["AmbientSplineEmitterWaterGroup"] = "CommonAmbOutBypass",
        };

        // Map key -> (owned root name, asset folder below Assets/ArenaImport/NativeAudio). Throws for a map without
        // imported ambience support, which also keeps a stray native_audio entry from building an unrouted graph.
        static string RootNameFor(string mapKey)
        {
            switch (mapKey)
            {
                case "arena_equator": return EquatorRootName;
                case "arena_bay5": return RootName;
                case "arena_airpit": return AirPitRootName;
                default: throw new InvalidDataException("Native audio has no owned root for map " + mapKey);
            }
        }

        static string AssetFolderFor(string mapKey)
        {
            switch (mapKey)
            {
                case "arena_equator": return "Equator";
                case "arena_bay5": return "Bay5";
                case "arena_airpit": return "AirPit";
                default: throw new InvalidDataException("Native audio has no asset folder for map " + mapKey);
            }
        }

        public static void Build(ArenaPackage package, GameObject scriptsRoot)
        {
            var entry = package.Manifest["native_audio"] as JObject;
            if (entry == null) return;
            string mapKey = ArenaPaths.Map.key;
            string rootName = RootNameFor(mapKey);
            if (scriptsRoot.activeInHierarchy)
                throw new InvalidDataException("Native audio requires an inactive ScriptsRoot.");
            var payload = ReadVerified(package, (string)entry["file"], (string)entry["sha256"]);
            var data = JObject.Parse(System.Text.Encoding.UTF8.GetString(payload));
            int schema = (int)entry["schema_version"];
            if ((schema != 1 && schema != 2) || (int)data["schemaVersion"] != schema ||
                (string)data["mapKey"] != mapKey || string.IsNullOrEmpty((string)data["sourceScene"]))
                throw new InvalidDataException("Unsupported native audio identity/schema.");
            // Schema 2 keeps each source object's activeSelf and layer (inactive ancestors, Equator's dogs); schema 1 is
            // all-active layer 0. The schema, not the map, decides.
            bool preserveStates = schema == 2;
            var players = (JArray)data["players"];
            var support = (JArray)data["support"];
            if (players.Count == 0 || support.Count == 0 || players.Count != (int)entry["players"] ||
                ((JArray)data["clips"]).Count != (int)entry["clips"] || ((JObject)data["banks"]).Count != (int)entry["banks"])
                throw new InvalidDataException("Native audio source census differs from the package manifest.");

            ValidateMembership(players, support);
            string assetDir = ArenaPaths.ImportRoot + "/NativeAudio/" + AssetFolderFor(mapKey);
            Directory.CreateDirectory(assetDir);
            var clips = new Dictionary<string, AudioClip>();
            foreach (JObject item in data["clips"])
            {
                var decoded = item["decoded"];
                var bytes = ReadVerified(package, (string)decoded["file"], (string)decoded["sha256"]);
                string assetPath = assetDir + "/" + SafeId((string)item["id"]) + ".wav";
                File.WriteAllBytes(assetPath, bytes);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (AudioImporter)AssetImporter.GetAtPath(assetPath);
                importer.forceToMono = false;
                importer.loadInBackground = false;
                var settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.PCM;
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                settings.preloadAudioData = true;
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
                if (clip == null || clip.channels != (int)decoded["channels"] || clip.frequency != (int)decoded["sampleRate"] || Math.Abs(clip.samples - (int)decoded["frames"]) > 2)
                    throw new InvalidDataException("Native audio import changed channels/rate/duration: " + assetPath);
                clips.Add((string)item["id"], clip);
            }

            var banks = new Dictionary<string, SoundBank>();
            foreach (var property in ((JObject)data["banks"]).Properties())
            {
                var item = property.Value;
                string assetPath = assetDir + "/" + SafeId(property.Name) + ".asset";
                var bank = AssetDatabase.LoadAssetAtPath<SoundBank>(assetPath);
                if (bank == null) { bank = ScriptableObject.CreateInstance<SoundBank>(); AssetDatabase.CreateAsset(bank, assetPath); }
                bank.name = (string)item["name"];
                bank.CustomLength = (float)item["customLength"];
                bank.BaseVolume = (float)item["baseVolume"]; bank.BasePitch = (float)item["basePitch"];
                bank.DeltaVolume = (float)item["deltaVolume"]; bank.DeltaPitch = (float)item["deltaPitch"];
                bank.Rolloff = (float)item["rolloff"]; bank.IgnoreOcclusion = (bool)item["ignoreOcclusion"];
                bank.Physical = (bool)item["physical"]; bank.HasEnvironment = (bool)item["hasEnvironment"];
                bank.AllowLimitedPlay = (bool)item["allowLimitedPlay"]; bank.ClipLength = (float)item["clipLength"];
                bank.SourceType = BetterAudio.AudioSourceGroupType.Environment;
                bank.BlendOptions = null;
                // PickSingleClip uses environments[0][0]; source-only limiter/enum fields are not used.
                bank.Environments = ((JArray)item["environments"]).Select(environment => new EnvironmentVariety
                {
                    Clips = ((JArray)environment).Select(distance => new DistanceVarity
                    { Clips = ((JArray)distance).Select(p => clips[(string)p["resolved"]]).ToArray() }).ToArray()
                }).ToArray();
                if (bank.Environments.Length == 0 || bank.Environments[0].Clips.Length == 0 || bank.Environments[0].Clips[0].Clips.Length == 0)
                    throw new InvalidDataException("Empty ambient bank: " + bank.name);
                EditorUtility.SetDirty(bank);
                banks.Add(property.Name, bank);
            }

            var root = new GameObject(rootName);
            root.SetActive(false);
            root.transform.SetParent(scriptsRoot.transform, false);
            var objects = new Dictionary<string, GameObject>();
            var hierarchyNodes = new Dictionary<string, JToken>();
            var components = new Dictionary<string, Component>();
            try
            {
                foreach (JObject item in players.Concat(support)) EnsureHierarchy(root, objects, hierarchyNodes, item, preserveStates);
                foreach (JObject item in players)
                {
                    var go = objects[(string)item["path"]];
                    var audio = Add<AudioSource>(go);
                    var source = item["sourceAudio"];
                    audio.playOnAwake = false; audio.loop = (bool)source["Loop"];
                    audio.volume = (float)source["m_Volume"]; audio.pitch = (float)source["m_Pitch"];
                    audio.minDistance = (float)source["MinDistance"]; audio.maxDistance = (float)source["MaxDistance"];
                    audio.rolloffMode = (AudioRolloffMode)(int)source["rolloffMode"];
                    audio.mute = (bool)source["Mute"]; audio.priority = (int)source["Priority"];
                    audio.dopplerLevel = (float)source["DopplerLevel"]; audio.panStereo = (float)source["Pan2D"];
                    audio.spatialize = (bool)source["Spatialize"]; audio.spatializePostEffects = (bool)source["SpatializePostEffects"];
                    audio.bypassEffects = (bool)source["BypassEffects"]; audio.bypassListenerEffects = (bool)source["BypassListenerEffects"];
                    audio.bypassReverbZones = (bool)source["BypassReverbZones"];
                    audio.enabled = (int)source["m_Enabled"] != 0;
                    BaseAmbientSoundPlayer player;
                    switch ((string)item["kind"])
                    {
                        case "AmbientSoundPlayer":
                            var random = Add<AmbientSoundPlayer>(go);
                            random._ambientBank = banks[(string)item["ambientBank"]["resolved"]];
                            random._randomTimeRange = Vec2(item["randomTimeRange"]); player = random; break;
                        case "SeasonAmbientSoundPlayer":
                            var season = Add<SeasonAmbientSoundPlayer>(go);
                            season._randomTimeRange = Vec2(item["randomTimeRange"]);
                            season._seasonAmbientBanks = new SeasonAmbientSoundPlayer.SeasonSoundBanks();
                            foreach (var pair in ((JObject)item["seasonAmbientBanks"]).Properties())
                                season._seasonAmbientBanks.Add(ParseSeason(pair.Name), banks[(string)pair.Value["resolved"]]);
                            season._seasonRandomTimeRange = new SeasonAmbientSoundPlayer.SeasonRandomTimeRange();
                            foreach (var pair in ((JObject)item["seasonRandomTimeRange"]).Properties())
                                season._seasonRandomTimeRange.Add(ParseSeason(pair.Name), Vec2(pair.Value));
                            ((ISerializationCallbackReceiver)season._seasonAmbientBanks).OnBeforeSerialize();
                            ((ISerializationCallbackReceiver)season._seasonRandomTimeRange).OnBeforeSerialize();
                            player = season; break;
                        case "LoopAmbientSoundPlayer":
                            var loop = Add<LoopAmbientSoundPlayer>(go);
                            loop._loopClip = clips[(string)item["loopClip"]["resolved"]]; player = loop; break;
                        default: throw new InvalidDataException("Unsupported native ambient player.");
                    }
                    player.enabled = (bool)item["enabled"];
                    player._volume = (float)item["volume"]; player._deltaVolume = (float)item["deltaVolume"];
                    player._randomPitchRange = Vec2(item["randomPitchRange"]);
                    player._fadeInDurationSec = (float)item["fadeInDurationSec"]; player._fadeOutDurationSec = (float)item["fadeOutDurationSec"];
                    // A player the contract pins in states.forced_mixer_players (exporter marker mixerPolicy "group";
                    // Bowl's metal_rigging forces Arena's own 'ArenaCommonAmb') is routed like its group instead.
                    bool groupRouted = (string)item["mixerPolicy"] == "group";
                    if (((long)item["mixerGroup"]["pathId"] != 0 && !groupRouted) || (bool)item["playOnAwake"])
                        throw new InvalidDataException("Source player mixer/start policy changed.");
                    player.ForceSetMixerGroup = !groupRouted && (bool)item["forceSetMixerGroup"]; player._playOnAwake = false;
                    player._spread = (float)item["spread"]; player._spatialBlend = (float)item["spatialBlend"];
                    player._minDistance = (float)item["minDistance"]; player._maxDistance = (float)item["maxDistance"];
                    ApplyCurve(player, "_rolloffCurve", item["rolloffCurve"]);
                    ApplyCurve(player, "_spreadCurve", item["spreadCurve"]);
                    player._useCustomSpreadCurve = (bool)item["useCustomSpreadCurve"]; player._startPlayingDelay = Vec2(item["startPlayingDelay"]);
                    components.Add((string)item["id"], player);
                }
                // Allocate every support component before wiring forward references.
                foreach (JObject item in support)
                {
                    var go = objects[(string)item["path"]]; Component component;
                    switch ((string)item["kind"])
                    {
                        case "AmbientSoundPlayerGroup": component = Add<AmbientSoundPlayerGroup>(go); break;
                        case "BezierSpline": component = Add<BezierSpline>(go); break;
                        case "AmbientSplineEmitterController": component = Add<AmbientSplineEmitterController>(go); break;
                        case "SoundAmbientZoneCalculator": component = Add<SoundAmbientZoneCalculator>(go); break;
                        case "AmbientPlayerSplineMappedEmitter": component = Add<AmbientPlayerSplineMappedEmitter>(go); break;
                        default: throw new InvalidDataException("Unsupported ambient support.");
                    }
                    ((Behaviour)component).enabled = (bool)item["enabled"];
                    components.Add((string)item["id"], component);
                }
                foreach (JObject item in support)
                {
                    var component = components[(string)item["id"]];
                    if (component is AmbientSoundPlayerGroup group)
                    {
                        // The client finds the native mixer by this group's GameObject name; the package must agree.
                        if (!GroupMixers.TryGetValue(group.name, out string expected) || (string)item["mixerName"] != expected)
                            throw new InvalidDataException("Unknown ambient mixer routing for group '" + group.name + "'.");
                        group._soundPlayers = ((JArray)item["soundPlayers"]).Select(p => (BaseAmbientSoundPlayer)components[(string)p["resolved"]]).ToList();
                    }
                    else if (component is BezierSpline spline)
                    {
                        spline.points = ((JArray)item["points"]).Select(Vec3Array).ToArray();
                        spline.modes = ((JArray)item["modes"]).Select(p => (BezierControlPointMode)(int)p).ToArray();
                        spline.loop = (bool)item["loop"];
                    }
                    else if (component is SoundAmbientZoneCalculator calculator)
                    {
                        calculator._splineEmitter = (AbstractSplineMappedEmitter)components[(string)item["splineEmitter"]["resolved"]];
                        calculator._spline = (BezierSpline)components[(string)item["spline"]["resolved"]];
                        calculator._resolution = (int)item["resolution"]; calculator._calculationRadius = (float)item["calculationRadius"];
                        calculator._blendSpeed = (float)item["blendSpeed"];
                    }
                    else if (component is AmbientPlayerSplineMappedEmitter emitter)
                    {
                        emitter._soundPlayer = (BaseAmbientSoundPlayer)components[(string)item["soundPlayer"]["resolved"]];
                        emitter._spreadRange = Vec2(item["spreadRange"]);
                    }
                }
                AssetDatabase.SaveAssets();
                Debug.Log($"[ArenaMapPort][NativeAudio] authored inactive stock graph map={ArenaPaths.Map.key} players={players.Count} banks={banks.Count} clips={clips.Count} support={support.Count}");
            }
            catch { UnityEngine.Object.DestroyImmediate(root); throw; }
        }

        // The groups start exactly the enabled players that are active under their original ancestors. Bay5 and AirPit
        // have no inactive or disabled player (that set is every player); Equator's two inactive dogs stay out of its group.
        // The exact inactive/loop/mixer pins of each map live in the extraction contract and the bundle validators.
        static void ValidateMembership(JArray players, JArray support)
        {
            var ids = players.Select(p => (string)p["id"]).ToArray();
            var groups = support.Where(s => (string)s["kind"] == "AmbientSoundPlayerGroup").ToArray();
            var grouped = groups.SelectMany(s => (JArray)s["soundPlayers"]).Select(p => (string)p["resolved"]).ToArray();
            var activePlayers = players.Where(p => (bool)p["enabled"] && (bool)p["activeInHierarchy"]).ToArray();
            var active = activePlayers.Select(p => (string)p["id"]).ToArray();
            if (groups.Length == 0 || ids.Distinct().Count() != ids.Length || grouped.Distinct().Count() != grouped.Length ||
                !new HashSet<string>(active).SetEquals(grouped))
                throw new InvalidDataException("Native audio group membership differs from active source graph.");
            if (groups.Any(g => !(bool)g["enabled"] || !(bool)g["activeInHierarchy"]) ||
                activePlayers.Any(p => (int)p["sourceAudio"]["m_Enabled"] != 1))
                throw new InvalidDataException("Native audio source activation contract changed.");
        }

        static byte[] ReadVerified(ArenaPackage package, string relative, string expectedHash)
        {
            string root = Path.GetFullPath(package.Root) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative ?? ""));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative)) throw new InvalidDataException("Audio path outside package.");
            byte[] bytes = File.ReadAllBytes(path);
            using (var sha = SHA256.Create())
                if (!string.Equals(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Audio hash mismatch: " + relative);
            return bytes;
        }
        static string SafeId(string id) => id.Replace(':', '_');
        static T Add<T>(GameObject go) where T : Component => go.AddComponent<T>() ?? throw new InvalidOperationException("Native audio script not ready: " + typeof(T).FullName);
        static ESeasonStatus ParseSeason(string name) => Enum.TryParse(name, out ESeasonStatus value) && Enum.IsDefined(typeof(ESeasonStatus), value) ? value : throw new InvalidDataException("Unknown season: " + name);
        static Vector2 Vec2(JToken p) => new Vector2((float)p[0], (float)p[1]);
        static Vector3 Vec3(JToken p) => new Vector3((float)p["x"], (float)p["y"], (float)p["z"]);
        static Vector3 Vec3Array(JToken p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);
        static void ApplyCurve(BaseAmbientSoundPlayer player, string field, JToken source)
        {
            // Serialized infinity codes are not the managed WrapMode enum. Let Unity deserialize
            // its native curve shape, then verify the round trip instead of casting numeric IDs.
            var keys = (JArray)source["keys"].DeepClone();
            foreach (JObject key in keys) key["serializedVersion"] = "3";
            var curve = new JObject { ["serializedVersion"] = "2", ["m_Curve"] = keys,
                ["m_PreInfinity"] = source["preInfinity"], ["m_PostInfinity"] = source["postInfinity"],
                ["m_RotationOrder"] = source["rotationOrder"] };
            // Native GameRefs components serialize inside a MonoBehaviour envelope. A bare field
            // object is silently ignored by FromJsonOverwrite, leaving the constructor's curve.
            var serialized = JObject.Parse(EditorJsonUtility.ToJson(player));
            var body = serialized["MonoBehaviour"] as JObject;
            if (body == null) throw new InvalidDataException("Native audio serialization envelope changed.");
            body[field] = curve;
            EditorJsonUtility.FromJsonOverwrite(serialized.ToString(), player);
            var roundTrip = JObject.Parse(EditorJsonUtility.ToJson(player))["MonoBehaviour"]?[field];
            if (roundTrip == null || (int)roundTrip["m_PreInfinity"] != (int)source["preInfinity"] ||
                (int)roundTrip["m_PostInfinity"] != (int)source["postInfinity"] ||
                (int)roundTrip["m_RotationOrder"] != (int)source["rotationOrder"] ||
                ((JArray)roundTrip["m_Curve"]).Count != ((JArray)source["keys"]).Count)
                throw new InvalidDataException("Native curve round trip changed " + field);
            for (int i = 0; i < ((JArray)source["keys"]).Count; i++)
                foreach (var property in ((JObject)source["keys"][i]).Properties())
                    if ((float)roundTrip["m_Curve"][i][property.Name] != (float)property.Value)
                        throw new InvalidDataException("Native curve key changed " + field + "/" + property.Name);
        }
        static void EnsureHierarchy(GameObject root, Dictionary<string, GameObject> objects,
            Dictionary<string, JToken> hierarchyNodes, JObject item, bool preserveStates)
        {
            var names = ((string)item["path"]).Split('/'); var transforms = (JArray)item["transformChain"];
            if (names.Length != transforms.Count || names.Any(string.IsNullOrEmpty) ||
                (!preserveStates && !(bool)item["activeInHierarchy"])) throw new InvalidDataException("Unsupported inactive/malformed audio hierarchy.");
            Transform parent = root.transform; string path = "";
            bool active = true;
            for (int i = 0; i < names.Length; i++)
            {
                path = i == 0 ? names[i] : path + "/" + names[i];
                var t = transforms[i];
                if (preserveStates && (t["activeSelf"]?.Type != JTokenType.Boolean ||
                    t["layer"]?.Type != JTokenType.Integer || (int)t["layer"] < 0 || (int)t["layer"] > 31))
                    throw new InvalidDataException("Invalid native audio hierarchy state.");
                active &= !preserveStates || (bool)t["activeSelf"];
                if (hierarchyNodes.TryGetValue(path, out var previous) && !JToken.DeepEquals(previous, t))
                    throw new InvalidDataException("Inconsistent shared native audio hierarchy: " + path);
                if (!objects.TryGetValue(path, out var go))
                {
                    go = new GameObject(names[i]); go.transform.SetParent(parent, false);
                    var q = t["rotation"];
                    go.transform.localPosition = Vec3(t["position"]); go.transform.localScale = Vec3(t["scale"]);
                    go.transform.localRotation = new Quaternion((float)q["x"], (float)q["y"], (float)q["z"], (float)q["w"]);
                    if (preserveStates) { go.layer = (int)t["layer"]; go.SetActive((bool)t["activeSelf"]); }
                    objects.Add(path, go);
                    hierarchyNodes.Add(path, t);
                }
                parent = go.transform;
            }
            if (active != (bool)item["activeInHierarchy"]) throw new InvalidDataException("Native audio hierarchy activation mismatch.");
        }
    }
}
