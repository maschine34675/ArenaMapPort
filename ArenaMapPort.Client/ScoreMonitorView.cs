#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The Unity side of <see cref="ScoreMonitors"/> (the only file of the feature that touches Unity, UGUI or TextMeshPro; docs/progress/score-monitors.md).
    ///
    /// What Arena does: each <c>Arena_Home_theater</c> TV prop carries an <c>ArenaMonitorHolder</c>, a world-space canvas (1930 x 1163 canvas pixels at scale 0.00054, at
    /// prop-local (0, 0, 0.369285), rotated (-0.5, 0.5, 0.5, -0.5): readable from the prop's +X side) that Arena's systems fill from a template with a mode's timer, score
    /// or alive count. What the ported scene lacks: the canvas and everything on it (the extractor walks Transforms only, not RectTransforms), so the TV is a dark grey quad.
    /// What this does: when a mode plugin (or the developer demo) claims the monitors, it finds the ACTIVE TV props the descriptor lists (ArenaMapDescriptor.ScoreMonitors)
    /// that have a face (<see cref="ScoreMonitorLayout.FaceOf"/>), parents one world-space canvas to each at Arena's mount pose and builds the face of Arena's own template
    /// on it from <see cref="ScoreMonitorLayout"/>: Images (solid fills and Arena's sprites from the plugin's <c>monitors</c> folder, loaded from PNG) and TextMeshPro
    /// texts in EFT's Bender Normal SDF font; the content comes from the Unity-free snapshot of <see cref="ScoreMonitorHub"/> through <see cref="ScoreMonitorFrames"/>.
    /// No camera, no render texture, no collider, nothing in the periodic path but a flag read; a raid nobody claims costs one volatile int compare per frame.
    ///
    /// Why the lift. At Arena's mount pose the canvas plane lies 0.0071 prop-local units BEHIND the TV mesh's opaque screen quad (screen quad at x = +0.00710, bezel front at
    /// +0.00864, canvas plane at 0; 21 mm at prop scale 3, 140 mm on Bowl's 19.68 boards), so a canvas at the pose is hidden: how Arena shows it through the glass is not
    /// recoverable (its IL2CPP metadata is encrypted). The surface is therefore lifted toward the reader (prop-local +X) by <c>Monitors/ScreenLift</c> prop-local units
    /// (0.0100: 9 mm over the quad at scale 3, 4 mm over the bezel front, inside the ring so nothing overlaps it). Values below 0.0071 are the negative control of the host
    /// check (the screen stays dark); the working window is 0.0075 to 0.03.
    ///
    /// Cost model. A claim resolves the font once per raid (a negative result is cached), loads the sprites its faces need (one PNG per frame; 10 to 40 ms each for the
    /// 1920 x 1080 ones), then builds at most <c>BuildPerFrame</c> monitors per frame inside a time budget. Afterwards only a changed snapshot or a new clock second touches
    /// a UI object, and only the elements whose text, colour or visibility really changed. The props are looked up by hierarchy path (<see cref="BoothDoorRepairs.FindAllByPath"/>:
    /// 44 to 82 scene roots per map) while monitors are still pending, twice a second until 20 s have passed since the claim AND since the last owned scene finished loading (a claim may precede the content scenes); no scene-wide search exists anywhere else. Everything that is
    /// built dies with the raid: the canvases are children of the raid's props, the sprites and textures are destroyed when the owned Scripts scene unloads. Every Unity
    /// object is Unity-null checked before use (a content scene may unload before the Scripts scene) and nothing throws into EFT's load chain: Tick is one try/catch, the
    /// scene handlers are guarded, there is no Harmony patch (the generic-class trap of AGENTS.md does not apply).
    ///
    /// Logging: Debug for the claim, the font, each sprite, the build and the removal; one Warning for a real degradation (sprites missing or unreadable, no font, props not
    /// found 20 s after the claim and the last owned scene load, an exception in the tick); nothing at Info. Developer keys (Settings, never public): <c>Monitors/Enabled</c>, <c>Monitors/ScreenLift</c> (live),
    /// <c>Monitors/Demo</c>.
    ///
    /// Host checks: docs/progress/score-monitors.md.
    /// </summary>
    internal static class ScoreMonitorView
    {
        const string Tag = "[Monitors]";
        const int BuildPerFrame = 3, SpritesPerFrame = 1;
        const float BuildBudgetMs = 6f, PendingTimeoutSeconds = 20f, RetrySeconds = 0.5f, DemoStepSeconds = 0.1f, DemoRefusedSeconds = 5f;
        const string DemoOwner = "developer-demo";
        const float LiftDefault = 0.0100f, LiftMin = 0f, LiftMax = 0.03f;
        const float DarkToggle = 0.16f;   // the plain square of an Off toggle when its sprite is missing

        enum DemoMode { Off, Cycle }

        // One column of alive toggles (a side of an ALIVE face): a box in the canvas and, per member, a cell with the Off picture and the On picture over it.
        sealed class ToggleColumn
        {
            internal RectTransform Box;
            internal float Width, Height;                              // the box in canvas pixels
            internal readonly List<Image> Off = new List<Image>();
            internal readonly List<Image> On = new List<Image>();
            internal int Count = -1;                                   // cells laid out (-1 = not yet)
            internal int Lit = -2;                                     // cells shown lit (-2 = not yet)
        }

        sealed class Monitor
        {
            internal ArenaScoreMonitor Row;
            internal ScoreMonitorFace Face;
            internal GameObject Root;
            internal Image BlockA, BlockB;
            internal TextMeshProUGUI Minutes, Colon, Seconds, ScoreA, ScoreB;
            internal readonly ToggleColumn[] Columns = new ToggleColumn[2];
            // what the objects show now, so that an unchanged value touches nothing
            internal string ShownMinutes, ShownSeconds, ShownScoreA, ShownScoreB;
            internal int ShownColorA = int.MinValue, ShownColorB = int.MinValue;
            internal bool Dead;
        }

        static ConfigEntry<bool> _enabled;
        static ConfigEntry<float> _lift;
        static ConfigEntry<DemoMode> _demo;
        static volatile bool _enabledFlag;
        static bool _installed;

        // Raid facts, written on the main thread by the scene events and read by the hub from any thread.
        static volatile bool _raidLoaded;
        static volatile int _usable;
        static ArenaMapDescriptor _map;
        static ArenaScoreMonitor[] _usableRows;

        // The claim being served.
        static readonly List<Monitor> _built = new List<Monitor>();
        static readonly List<ArenaScoreMonitor> _pending = new List<ArenaScoreMonitor>();
        static ArenaMapDescriptor _builtFor;      // the map the built/pending monitors belong to
        static ScoreMonitorSnapshot _snap;
        static int _claim, _failedClaim, _seenVersion = -1, _clockSecond = int.MinValue, _timerCount;
        static float _claimedAt, _nextRetry, _liftApplied = float.NaN;
        static float _sceneActivityAt;   // realtime of the last owned scene that finished loading: the props' 20 s clock starts after the last one
        static bool _failed, _pendingWarned, _presentDirty;

        // Assets, per raid.
        static TMP_FontAsset _font;
        static bool _fontTried;
        static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        static readonly List<Texture2D> _textures = new List<Texture2D>();
        static readonly HashSet<string> _missing = new HashSet<string>();
        static Queue<string> _spriteQueue;
        static bool _assetsReady, _missingWarned;
        static string _folder;

        // The developer demo.
        static int _demoClaim;
        static float _nextDemo, _demoStart;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                var s = Plugin.Instance.Settings;
                _enabled = s.Bind("Monitors", "Enabled", true,
                    "Lets a mode plugin (ArenaMode) show its score, time and alive counts on the active TV props of the map (ScoreMonitors.Claim). Off: a claim is refused. Next claim.");
                _lift = s.Bind("Monitors", "ScreenLift", LiftDefault, new ConfigDescription(
                    "How far in front of Arena's mount pose each monitor surface floats, in units of its TV prop (towards the reader). Arena's canvas plane lies 0.0071 behind the " +
                    "TV's opaque screen quad, so below that the TV stays dark (the negative control of the host check); 0.0075 to 0.03 works, 0.0100 clears the quad by 9 mm at " +
                    "prop scale 3 and stays inside the bezel ring. Live.", new AcceptableValueRange<float>(LiftMin, LiftMax)));
                _demo = s.Bind("Monitors", "Demo", DemoMode.Off,
                    "Developer test: show a scripted 70 s cycle (score digit widths, teams of 5 and 16, a countdown and a count-up, the result) on the monitors of an owned raid " +
                    "without a mode plugin; the claim is owned by 'developer-demo' and a plugin's claim takes it over. Cycle starts it, Off stops it. Live.");
                _enabledFlag = _enabled.Value;
                _enabled.SettingChanged += (sender, args) => _enabledFlag = _enabled.Value;
                ScoreMonitors.Hub = new ScoreMonitorHub(() => _raidLoaded, () => _usable, () => _enabledFlag);
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} the score monitors could not be installed ({e.GetType().Name}: {e.Message}); the TVs stay dark");
            }
        }

        // ---- the raid ---------------------------------------------------------------------------------------------------------------------

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                // The Scripts scene loads first and the content scenes (the TV props) follow, tens of seconds before the raid starts: a claim made in between (the developer
                // demo claims at once) must not run its 20 s "props not found" clock while scenes are still arriving. Every owned scene load restarts that clock.
                if (ArenaMapRaid.IsOwnedScene(scene.name)) _sceneActivityAt = Time.realtimeSinceStartup;
                var map = ArenaMapRaid.MapForScriptsScene(scene.name);
                if (map == null) return;
                _map = map;
                _usableRows = ScoreMonitorModel.UsableRows(map.ScoreMonitors);
                _usable = _usableRows.Length;
                _raidLoaded = true;
                Plugin.Log.LogDebug($"{Tag} {map.Key}: {_usable} of {map.ScoreMonitors.Length} monitor row(s) can show a face");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} reading the monitors of the map failed ({e.GetType().Name}: {e.Message}); the TVs stay dark");
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            try
            {
                if (ArenaMapRaid.MapForScriptsScene(scene.name) == null) return;
                _raidLoaded = false;   // first: from here on the hub refuses claims
                _usable = 0;
                // The canvases are children of this raid's props and die with their scenes, but a content scene may unload after the sprites below are gone: destroying them
                // now (deferred to the end of the frame) leaves no frame with a missing sprite (an Image without one draws a white quad).
                Teardown("raid ended", destroy: true);
                DisposeAssets();
                ScoreMonitors.Hub.ResetForNewRaid();
                _map = null;
                _usableRows = null;
                _demoClaim = 0;
                _seenVersion = -1;
                _failedClaim = 0;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} the end of the raid could not be handled cleanly ({e.GetType().Name}: {e.Message})");
            }
        }

        // ---- the tick -----------------------------------------------------------------------------------------------------------------------

        /// <summary>Plugin.LateUpdate. Never throws; one flag test per frame while nothing runs.</summary>
        internal static void Tick()
        {
            if (!_installed || (!_raidLoaded && _claim == 0 && _demoClaim == 0)) return;   // vanilla maps, menus, the hideout: nothing else runs
            try
            {
                var hub = ScoreMonitors.Hub;
                TickDemo(hub);

                if (hub.HasLog)   // claim changes and refusals the setters queued (they never log themselves: any thread may call them)
                {
                    var lines = hub.DrainLog();
                    if (lines != null) foreach (var line in lines) Plugin.Log.LogDebug($"{Tag} {line}");
                }

                int version = hub.Version;   // one volatile read when nothing happens
                if (version != _seenVersion)
                {
                    _seenVersion = version;
                    _snap = hub.Snapshot();
                    if (_snap.Claim == 0) { if (_claim != 0) Teardown("released", destroy: true); }
                    else if (_snap.Claim != _claim && _snap.Claim != _failedClaim) BeginClaim(_snap);
                    _presentDirty = true;
                }
                if (_claim == 0 || _failed) return;
                Advance();
            }
            catch (Exception e)
            {
                _failed = true;
                _failedClaim = _claim != 0 ? _claim : _snap.Claim;
                Plugin.Log.LogWarning($"{Tag} the score monitors stopped for this claim ({e.GetType().Name}: {e.Message})");
                try { Teardown("failure", destroy: true); } catch { /* the Warning above is the record */ }
            }
        }

        static void Advance()
        {
            if (!_assetsReady)
            {
                PrepareAssets();
                if (!_assetsReady) return;
            }
            if (_pending.Count > 0) BuildSome();
            if (_built.Count == 0) return;
            AdjustLift();
            int second = _timerCount > 0 ? ScoreMonitorModel.ClockSecond(_snap.ClockMode, _snap.ClockSeconds, Time.time) : -1;
            if (_presentDirty || (_timerCount > 0 && second != _clockSecond))
            {
                _presentDirty = false;
                _clockSecond = second;
                Present();
            }
        }

        // ---- claim / teardown ---------------------------------------------------------------------------------------------------------

        static void BeginClaim(in ScoreMonitorSnapshot snap)
        {
            // A plugin taking the monitors over from the demo keeps what the demo built (same map, nothing torn down in between).
            bool keep = _builtFor != null && ReferenceEquals(_builtFor, _map) && (_built.Count > 0 || _pending.Count > 0);
            if (!keep)
            {
                Teardown("new claim", destroy: true);
                _builtFor = _map;
                if (_usableRows != null) _pending.AddRange(_usableRows);
            }
            _claim = snap.Claim;
            _failed = false;
            _pendingWarned = false;
            _claimedAt = Time.realtimeSinceStartup;
            _nextRetry = 0f;
            _assetsReady = false;
            _spriteQueue = null;
            _clockSecond = int.MinValue;
            Plugin.Log.LogDebug($"{Tag} claim {snap.Claim} by '{snap.Owner}': {_built.Count} built, {_pending.Count} to build on {(_map != null ? _map.Key : "?")}");
        }

        static void Teardown(string why, bool destroy)
        {
            int built = _built.Count;
            if (destroy)
            {
                foreach (var m in _built) if (m.Root != null) UnityEngine.Object.Destroy(m.Root);
            }
            _built.Clear();
            _pending.Clear();
            _timerCount = 0;
            _builtFor = null;
            ScoreMonitors.Hub.BuiltCount = 0;
            if (_claim != 0 && built > 0) Plugin.Log.LogDebug($"{Tag} {built} monitor(s) removed ({why})");
            _claim = 0;
            _failed = false;
        }

        // ---- assets (per raid) --------------------------------------------------------------------------------------------------------

        // Font once, then one sprite per frame until the faces of the pending rows have what they draw. Never throws out of a single load: a missing or damaged
        // sprite is recorded, and its elements are left out (the faces degrade to plain colour blocks and text).
        static void PrepareAssets()
        {
            if (!_fontTried) ResolveFont();
            if (_spriteQueue == null)
            {
                _spriteQueue = new Queue<string>();
                var wanted = new List<string>();
                var faces = new HashSet<ScoreMonitorFace>();
                foreach (var row in _pending) faces.Add(ScoreMonitorLayout.FaceOf(row.ArenaType));
                foreach (var face in faces)
                    foreach (var name in ScoreMonitorLayout.SpritesOf(face))
                        if (!wanted.Contains(name)) wanted.Add(name);
                foreach (var name in wanted)
                    if (!_sprites.ContainsKey(name) && !_missing.Contains(name)) _spriteQueue.Enqueue(name);
            }
            for (int i = 0; i < SpritesPerFrame && _spriteQueue.Count > 0; i++) LoadSprite(_spriteQueue.Dequeue());
            if (_spriteQueue.Count > 0) return;
            _assetsReady = true;
            if (_missing.Count > 0 && !_missingWarned)
            {
                _missingWarned = true;
                var names = new List<string>(_missing);
                names.Sort(StringComparer.Ordinal);
                Plugin.Log.LogWarning($"{Tag} {names.Count} monitor sprite(s) are missing or unreadable in '{MonitorsFolder()}' ({string.Join(", ", names.ToArray())}); " +
                                      "the faces draw as plain colour blocks and text without them");
            }
        }

        static string MonitorsFolder()
        {
            if (_folder == null) _folder = Path.Combine(Path.GetDirectoryName(typeof(ScoreMonitorView).Assembly.Location) ?? "", "monitors");
            return _folder;
        }

        static void LoadSprite(string name)
        {
            Texture2D texture = null;
            try
            {
                if (!ScoreMonitorLayout.TryGetSprite(name, out ScoreMonitorSprite info)) throw new InvalidDataException("not in the sprite table of the layout");
                string path = Path.Combine(MonitorsFolder(), name + ".png");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                {
                    name = "ArenaMapPort_Monitor_" + name,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true)) throw new InvalidDataException("the PNG could not be decoded");
                if (texture.width != info.Width || texture.height != info.Height)
                    throw new InvalidDataException($"the PNG is {texture.width} x {texture.height}, the layout expects {info.Width} x {info.Height}");
                float b = info.Border;
                var sprite = Sprite.Create(texture, new Rect(0f, 0f, info.Width, info.Height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
                sprite.name = "ArenaMapPort_Monitor_" + name;
                _textures.Add(texture);
                _sprites[name] = sprite;
                Plugin.Log.LogDebug($"{Tag} sprite {name} {info.Width} x {info.Height} loaded in {watch.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception e)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                _missing.Add(name);
                Plugin.Log.LogDebug($"{Tag} sprite {name} unavailable ({e.GetType().Name}: {e.Message})");
            }
        }

        // EFT keeps its TMP fonts in Resources/UI/Fonts (LocalizationManager.CacheFonts loads them with the same call, so they are in memory already). The text face of the
        // UI, and of Arena's templates, is "Jovanny Lemonad - Bender Normal SDF"; the entries named "DON'T USE!" are style and fallback sources and the "Overlay" ones draw over
        // everything, so neither is ever taken. A negative result is cached for the raid: one listing, one Warning.
        static void ResolveFont()
        {
            _fontTried = true;
            TMP_FontAsset normal = null, anyBender = null, any = null;
            try
            {
                foreach (var candidate in Resources.LoadAll<TMP_FontAsset>("UI/Fonts"))
                {
                    if (candidate == null) continue;
                    string n = candidate.name;
                    if (n.IndexOf("DON'T USE", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (any == null) any = candidate;
                    if (n.IndexOf("Bender", StringComparison.OrdinalIgnoreCase) >= 0 && anyBender == null) anyBender = candidate;
                    if (n.IndexOf("Bender Normal", StringComparison.OrdinalIgnoreCase) >= 0 && normal == null) normal = candidate;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{Tag} the UI fonts could not be listed ({e.GetType().Name}: {e.Message})");
            }
            _font = normal ?? anyBender ?? any ?? TMP_Settings.defaultFontAsset;
            if (_font == null) Plugin.Log.LogWarning($"{Tag} no TextMeshPro font is available; the monitors draw without text");
            else Plugin.Log.LogDebug($"{Tag} font '{_font.name}'");
        }

        static void DisposeAssets()
        {
            foreach (var sprite in _sprites.Values) if (sprite != null) UnityEngine.Object.Destroy(sprite);
            foreach (var texture in _textures) if (texture != null) UnityEngine.Object.Destroy(texture);
            _sprites.Clear();
            _textures.Clear();
            _missing.Clear();
            _spriteQueue = null;
            _assetsReady = false;
            _missingWarned = false;
            _font = null;
            _fontTried = false;
        }

        // ---- building -----------------------------------------------------------------------------------------------------------------

        static void BuildSome()
        {
            float real = Time.realtimeSinceStartup;
            if (real < _nextRetry) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int made = 0, skipped = 0;
            bool missing = false;
            for (int i = _pending.Count - 1; i >= 0 && made < BuildPerFrame; i--)
            {
                var row = _pending[i];
                if (!TryFindProp(row, out Transform prop)) { missing = true; continue; }
                _pending.RemoveAt(i);
                if (!prop.gameObject.activeInHierarchy) { skipped++; continue; }   // something switched an active prop off: dark, never switched on by us
                _built.Add(Build(row, prop));
                made++;
                if (clock.Elapsed.TotalMilliseconds > BuildBudgetMs) break;
            }
            if (made > 0 || skipped > 0)
            {
                ScoreMonitors.Hub.BuiltCount = _built.Count;
                _presentDirty = true;
                Plugin.Log.LogDebug($"{Tag} built {made} monitor(s) in {clock.Elapsed.TotalMilliseconds:F1} ms ({_built.Count} total{(skipped > 0 ? $", {skipped} inactive skipped" : "")}, {_pending.Count} pending)");
            }
            if (_pending.Count > 0 && missing)
            {
                _nextRetry = real + RetrySeconds;
                if (!_pendingWarned && real - _claimedAt > PendingTimeoutSeconds && real - _sceneActivityAt > PendingTimeoutSeconds)
                {
                    _pendingWarned = true;
                    var names = new List<string>();
                    foreach (var row in _pending) names.Add(row.Scene + ":" + row.Path);
                    Plugin.Log.LogWarning($"{Tag} {_pending.Count} score monitor prop(s) were not found in the loaded scenes after {PendingTimeoutSeconds:F0} s: {string.Join("; ", names.ToArray())}");
                    _pending.Clear();
                }
            }
        }

        // The prop of a row: the objects at its path whose mount point is within the descriptor's tolerance of the row's world position, in the row's scene.
        static bool TryFindProp(ArenaScoreMonitor row, out Transform prop)
        {
            prop = null;
            foreach (var t in BoothDoorRepairs.FindAllByPath(row.Path))
            {
                if (t == null || t.gameObject.scene.name != row.Scene) continue;
                var world = t.TransformPoint(new Vector3(ArenaScoreMonitorMount.Position[0], ArenaScoreMonitorMount.Position[1], ArenaScoreMonitorMount.Position[2]));
                if (row.AppliesTo(world.x, world.y, world.z)) { prop = t; return true; }
            }
            return false;
        }

        static Vector3 MountPosition(float lift) =>
            new Vector3(ArenaScoreMonitorMount.Position[0] + lift, ArenaScoreMonitorMount.Position[1], ArenaScoreMonitorMount.Position[2]);

        static Color ToColor(ScoreMonitorColor c) => new Color(c.R, c.G, c.B, c.A);

        static Monitor Build(ArenaScoreMonitor row, Transform prop)
        {
            var face = ScoreMonitorLayout.FaceOf(row.ArenaType);
            var m = new Monitor { Row = row, Face = face };
            var root = new GameObject("ArenaMapPort ScoreMonitor " + ScoreMonitorLayout.FaceName(face) + " " + row.Path, typeof(RectTransform));
            root.layer = 0;   // the camera's Default layer, where ArenaMode's boundary grid renders in a raid too
            m.Root = root;
            var rt = (RectTransform)root.transform;
            rt.SetParent(prop, false);
            float lift = _lift.Value;
            _liftApplied = lift;
            // Arena's mount pose, lifted toward the reader in prop-local units (the canvas faces the prop's -X, the reader stands on +X).
            rt.localPosition = MountPosition(lift);
            rt.localRotation = new Quaternion(ArenaScoreMonitorMount.Rotation[0], ArenaScoreMonitorMount.Rotation[1], ArenaScoreMonitorMount.Rotation[2], ArenaScoreMonitorMount.Rotation[3]);
            rt.localScale = new Vector3(ArenaScoreMonitorMount.Scale, ArenaScoreMonitorMount.Scale, ArenaScoreMonitorMount.Scale);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(ArenaScoreMonitorMount.PivotX, ArenaScoreMonitorMount.PivotY);
            rt.sizeDelta = new Vector2(ArenaScoreMonitorMount.Width, ArenaScoreMonitorMount.Height);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.referencePixelsPerUnit = 100f;   // the sliced vignette's border scales with it; the sprites are 100 pixels per unit

            try
            {
                var byRole = new RectTransform[Enum.GetValues(typeof(ScoreMonitorRole)).Length];
                foreach (var e in ScoreMonitorLayout.ElementsOf(face))
                {
                    RectTransform parent = e.Parent == ScoreMonitorRole.None ? rt : byRole[(int)e.Parent];
                    if (parent == null) continue;   // the parent was left out (its sprite is missing): so is this
                    RectTransform made = CreateElement(m, e, parent);
                    if (made != null) byRole[(int)e.Role] = made;
                }
            }
            catch
            {
                UnityEngine.Object.Destroy(root);   // a half-built canvas must not stay on the TV; the tick reports the failure
                throw;
            }
            if (face == ScoreMonitorFace.Timer) _timerCount++;
            return m;
        }

        static RectTransform CreateElement(Monitor m, ScoreMonitorElement e, Transform parent)
        {
            Sprite sprite = null;
            if (e.Kind == ScoreMonitorElementKind.Sprite && !_sprites.TryGetValue(e.Sprite, out sprite)) return null;
            if (e.Kind == ScoreMonitorElementKind.Text && _font == null) return null;

            var go = new GameObject(e.Role.ToString(), typeof(RectTransform));
            go.layer = 0;
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = new Vector2(e.MinX, e.MinY);
            r.anchorMax = new Vector2(e.MaxX, e.MaxY);
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
            r.pivot = new Vector2(0.5f, 0.5f);

            switch (e.Kind)
            {
                case ScoreMonitorElementKind.Fill:
                case ScoreMonitorElementKind.Sprite:
                {
                    var image = go.AddComponent<Image>();
                    image.raycastTarget = false;
                    image.color = ToColor(e.Tint);
                    if (sprite != null)
                    {
                        image.sprite = sprite;
                        image.type = e.Sliced ? Image.Type.Sliced : Image.Type.Simple;
                        image.preserveAspect = e.PreserveAspect;
                    }
                    if (e.Role == ScoreMonitorRole.TeamBlockA) m.BlockA = image;
                    else if (e.Role == ScoreMonitorRole.TeamBlockB) m.BlockB = image;
                    break;
                }
                case ScoreMonitorElementKind.Text:
                {
                    var text = go.AddComponent<TextMeshProUGUI>();
                    text.font = _font;
                    // Arena's TextMeshPro settings: auto-size between 0.2 and 580 inside the rect, middle line, its alignment, spacings and overflow. Wrapping is OFF (Arena's is
                    // on): a number must never break into two lines, auto-size then shrinks it until it fits one.
                    text.enableAutoSizing = true;
                    text.fontSizeMin = ScoreMonitorLayout.TextMinSize;
                    text.fontSizeMax = ScoreMonitorLayout.TextMaxSize;
                    text.fontSize = ScoreMonitorLayout.TextMaxSize;
                    text.alignment = e.Align == ScoreMonitorAlign.Left ? TextAlignmentOptions.Left : e.Align == ScoreMonitorAlign.Right ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
                    text.enableWordWrapping = false;
                    text.overflowMode = e.Ellipsis ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
                    text.characterSpacing = e.CharacterSpacing;
                    text.lineSpacing = e.LineSpacing;
                    text.richText = false;
                    text.raycastTarget = false;
                    text.color = ToColor(e.Tint);
                    switch (e.Role)
                    {
                        case ScoreMonitorRole.Minutes: m.Minutes = text; text.text = "00"; break;
                        case ScoreMonitorRole.Colon: m.Colon = text; text.text = ":"; break;
                        case ScoreMonitorRole.Seconds: m.Seconds = text; text.text = "00"; break;
                        case ScoreMonitorRole.ScoreA: m.ScoreA = text; text.text = "0"; break;
                        case ScoreMonitorRole.ScoreB: m.ScoreB = text; text.text = "0"; break;
                    }
                    break;
                }
                case ScoreMonitorElementKind.Toggles:
                {
                    int side = e.Role == ScoreMonitorRole.TogglesA ? 0 : 1;
                    ScoreMonitorLayout.TryResolve(m.Face, e.Role, out ScoreMonitorRect box);
                    m.Columns[side] = new ToggleColumn { Box = r, Width = box.Width, Height = box.Height };
                    break;
                }
            }
            return r;
        }

        // ---- presenting ---------------------------------------------------------------------------------------------------------------

        static void AdjustLift()
        {
            float lift = _lift.Value;
            if (lift == _liftApplied) return;
            _liftApplied = lift;
            foreach (var m in _built) if (m.Root != null) m.Root.transform.localPosition = MountPosition(lift);
        }

        static void Present()
        {
            var frame = ScoreMonitorFrames.Build(_snap, _clockSecond);
            bool dead = false;
            foreach (var m in _built)
            {
                if (m.Root == null) { m.Dead = true; dead = true; continue; }   // a content scene unloaded before the Scripts scene: nothing to draw on
                Apply(m, frame);
            }
            if (dead)
            {
                int before = _built.Count;
                foreach (var m in _built) if (m.Dead && m.Face == ScoreMonitorFace.Timer) _timerCount--;
                _built.RemoveAll(m => m.Dead);
                ScoreMonitors.Hub.BuiltCount = _built.Count;
                Plugin.Log.LogDebug($"{Tag} {before - _built.Count} monitor(s) dropped: their scene is gone");
            }
        }

        static void Apply(Monitor m, in ScoreMonitorFrame f)
        {
            switch (m.Face)
            {
                case ScoreMonitorFace.Timer:
                    SetText(m.Minutes, ref m.ShownMinutes, f.Minutes);
                    SetText(m.Seconds, ref m.ShownSeconds, f.Seconds);
                    break;
                case ScoreMonitorFace.Score:
                    SetText(m.ScoreA, ref m.ShownScoreA, f.ScoreA);
                    SetText(m.ScoreB, ref m.ShownScoreB, f.ScoreB);
                    SetBlocks(m, f);
                    break;
                case ScoreMonitorFace.Alive:
                    SetBlocks(m, f);
                    SetToggles(m.Columns[0], f.TeamSize, f.LitA);
                    SetToggles(m.Columns[1], f.TeamSize, f.LitB);
                    break;
            }
        }

        static void SetText(TextMeshProUGUI text, ref string shown, string value)
        {
            if (text == null || string.Equals(shown, value, StringComparison.Ordinal)) return;
            shown = value;
            text.text = value;
        }

        static void SetBlocks(Monitor m, in ScoreMonitorFrame f)
        {
            if (m.BlockA != null && f.ColorA != m.ShownColorA) { m.ShownColorA = f.ColorA; m.BlockA.color = ToColor(ScoreMonitorLayout.TeamColor(f.ColorA, true)); }
            if (m.BlockB != null && f.ColorB != m.ShownColorB) { m.ShownColorB = f.ColorB; m.BlockB.color = ToColor(ScoreMonitorLayout.TeamColor(f.ColorB, false)); }
        }

        static void SetToggles(ToggleColumn col, int count, int lit)
        {
            if (col == null || col.Box == null) return;
            if (count != col.Count) { LayoutToggles(col, count); col.Lit = -2; }
            if (lit == col.Lit) return;
            col.Lit = lit;
            for (int i = 0; i < col.Count; i++)
            {
                var on = col.On[i];
                if (on != null) on.enabled = i < lit;
            }
        }

        // Cells for `count` members, centred top-first in the box: Arena's 200 canvas pixels, shrunk to fit (ScoreMonitorLayout.ToggleSize). Cells beyond the count are hidden.
        static void LayoutToggles(ToggleColumn col, int count)
        {
            _sprites.TryGetValue(ScoreMonitorLayout.SpriteToggleOff, out Sprite offSprite);
            _sprites.TryGetValue(ScoreMonitorLayout.SpriteToggleOn, out Sprite onSprite);
            float cell = ScoreMonitorLayout.ToggleSize(count, col.Width, col.Height);
            while (col.Off.Count < count)
            {
                col.Off.Add(CreateToggleImage("Off", col.Box, offSprite, offSprite != null ? Color.white : new Color(DarkToggle, DarkToggle, DarkToggle, 1f)));
                col.On.Add(CreateToggleImage("On", col.Off[col.Off.Count - 1].rectTransform, onSprite, Color.white));
            }
            for (int i = 0; i < col.Off.Count; i++)
            {
                var off = col.Off[i];
                if (off == null) continue;
                bool used = i < count;
                if (off.gameObject.activeSelf != used) off.gameObject.SetActive(used);
                if (!used) continue;
                var r = off.rectTransform;
                r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
                r.pivot = new Vector2(0.5f, 0.5f);
                r.sizeDelta = new Vector2(cell, cell);
                r.anchoredPosition = new Vector2(0f, ScoreMonitorLayout.ToggleCenterOffset(i, count, cell));
            }
            col.Count = count;
        }

        static Image CreateToggleImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 0;
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            image.color = color;
            if (sprite != null)
            {
                image.sprite = sprite;
                image.preserveAspect = true;
            }
            return image;
        }

        // ---- developer demo -----------------------------------------------------------------------------------------------------------

        static void TickDemo(ScoreMonitorHub hub)
        {
            if (!_raidLoaded || _demo == null || _demo.Value == DemoMode.Off)
            {
                if (_demoClaim != 0) { hub.Release(_demoClaim); _demoClaim = 0; }
                return;
            }
            float now = Time.time;
            if (now < _nextDemo) return;
            _nextDemo = now + DemoStepSeconds;
            if (_demoClaim != 0 && hub.Snapshot().Claim != _demoClaim) _demoClaim = 0;   // a plugin took the monitors over, or the claim ended
            if (_demoClaim == 0)
            {
                if (hub.IsClaimed) return;   // a plugin holds them: the demo waits
                _demoClaim = hub.Claim(DemoOwner, true);
                if (_demoClaim == 0) { _nextDemo = now + DemoRefusedSeconds; return; }   // refused (disabled, nothing usable): not every tenth of a second
                _demoStart = now;
            }
            var step = ScoreMonitorDemo.Cycle(now - _demoStart, _demoStart);
            hub.SetSides(_demoClaim, step.ColorA, step.ColorB, step.TeamSize);
            hub.SetScore(_demoClaim, step.ScoreA, step.ScoreB, step.AliveA, step.AliveB);
            hub.SetClock(_demoClaim, step.ClockMode, step.ClockSeconds);
        }
    }
}
