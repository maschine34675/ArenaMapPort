using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 10b — loading-screen art for ported maps (every map in
    /// <see cref="ArenaMapRegistry.All"/> that has art staged: Equator, Bay5 and AirPit).
    ///
    /// What vanilla does: EFT's own "Deploying in progress (NN%)" deploy screen
    /// (<c>EFT.UI.Matchmaker.MatchmakerTimeHasCome.Show</c>, decompile
    /// <c>EFT.UI.Matchmaker/MatchmakerTimeHasCome.cs:209</c>) is shown BEFORE any raid scene/bundle
    /// loads and stays up through the whole load (<c>TarkovApplication.LocalGameMatching</c>); it
    /// already receives <c>raidSettings.SelectedLocation</c>/<c>LocationId</c> at that exact call, but
    /// paints no location-specific image — <c>LocationSettings.Location.Preview</c> is a declared field
    /// nothing in this path ever reads, and <c>tools/server/make_base_json.py</c> never populates it.
    ///
    /// What the ported maps lack: no Arena-specific art anywhere in that deploy screen — every
    /// ArenaMapPort raid loads behind whatever EFT's own generic default background is. (Distinct
    /// from PLAN-TWO-MAPS item 10's <c>Banners[]</c>/<c>MatchmakerBannersPanel</c> work: that is a
    /// server-side, small-carousel mechanism on the SAME screen; this is a client-side, plugin-owned,
    /// full-screen background layered underneath it. The two coexist.)
    ///
    /// What this repair does: shows one of the raiding map's own Arena splash images full-screen for the
    /// duration of the deploy screen, as the FIRST child of EFT's own <c>MatchmakerTimeHasCome</c> screen
    /// (Image + <see cref="AspectRatioFitter"/>, envelope/"cover" fit, no raycast target), so the screen's own
    /// children — caption, location name, banner panel, player model, "Loading map... (NN%)" line and BACK
    /// button — still draw on top of it (host 2026-09-27: a top-level overlay canvas hid all of them). Only the
    /// sceneLoaded failsafe below, which has no screen to attach to, uses a plugin-owned
    /// <c>DontDestroyOnLoad</c> overlay canvas instead. The art is hidden once the raid becomes visible:
    ///  - SHOW (primary): Harmony postfix on <c>MatchmakerTimeHasCome.Show(IEftSession, RaidSettings,
    ///    MatchmakerPlayersController)</c> — resolves the map purely from
    ///    <c>raidSettings.LocationId</c> against <see cref="ArenaMapRegistry.All"/>'s own
    ///    <c>Key</c>, since no scene is loaded yet at this point (<see cref="ArenaMapRaid.CurrentMap"/>
    ///    is not usable here).
    ///  - SHOW (failsafe): a plain <c>SceneManager.sceneLoaded</c> subscription (this plugin's own
    ///    established pattern — see <see cref="ArenaMapRaid"/>'s doc comment for the list of other
    ///    subscribers) — if the debug-start flow (<c>maschine-ArenaMapDebug.dll</c>) ever turns out to
    ///    skip straight past the deploy screen, showing on the raid's own Scripts scene load still
    ///    covers whatever load time remains, idempotent against the primary hook already having shown it.
    ///  - HIDE (primary): the deploy screen's own controller <c>OnClose</c> event
    ///    (<c>ScreenManager.ScreenController.Closed</c> setter, decompile <c>EFT.UI.Screens/ScreenManager.cs:75</c>),
    ///    subscribed by reflection in the SHOW postfix. EFT closes that controller when the next root
    ///    screen (FinalCountdown/BattleUI) replaces it, which is the moment the raid becomes visible.
    ///  - HIDE (late fallback): Harmony postfix on
    ///    <c>EFT.UI.EftBattleUIScreen.RaidBattleUIScreenController.ShowRaidStartInfo()</c> — fires once
    ///    per raid, but about 14 s after the raid timer starts (host 2026-09-27: too late as the only hide).
    ///  - HIDE (failsafe #1): Harmony prefix on <c>TarkovApplication.ComebackToMainMenu()</c> —
    ///    unconditional, fires on every abort/error/return-to-menu path in that file; a plain no-op if
    ///    the overlay is not currently visible.
    ///  - HIDE (failsafe #2): a plain <c>SceneManager.sceneUnloaded</c> subscription on any of our
    ///    maps' own Scripts scenes — belt-and-braces against a future EFT update silently renaming one
    ///    of the two Harmony targets above (this plugin's own log-and-skip <c>HarmonyPatches.Verify</c>
    ///    would otherwise leave the overlay stuck showing over a live main menu forever instead of just
    ///    never showing).
    ///
    /// Textures are loaded lazily from this plugin's own deployed folder
    /// (<c>Path.Combine(PluginDir, "loadscreens", "arena_&lt;key&gt;_&lt;n&gt;.jpg")</c>, same
    /// <c>PluginDir</c> convention as <c>ShaderRebind.cs</c>/<c>BundleHost.cs</c>/
    /// <c>NavMeshInstaller.cs</c>; staged there by <c>tools/extract/extract_loadscreens.py</c>) via
    /// <c>File.ReadAllBytes</c> + <c>ImageConversion.LoadImage</c> — plugin-local files, not a
    /// UnityWebRequest fetch — each image decoded once and cached for the process; a raid load picks at
    /// random among whichever indices exist on disk (five per map: Arena's loading screen plus its four
    /// main-menu backgrounds), never the one shown last time. Every entry
    /// point is wrapped in try/catch (never allowed to throw into EFT code) and every obfuscated-free
    /// member name here was verified against the decompile and is installed through
    /// <see cref="HarmonyPatches.Verify"/> (log-and-skip); a vanilla (non-Arena) raid's
    /// <c>LocationId</c> never matches any <see cref="ArenaMapDescriptor.Key"/>, so this repair is a
    /// complete no-op for every other map.
    ///
    /// Verified 2026-09-27 on SPT-GPU-2 (Equator + Bay5, debug start and normal menu flow): see
    /// docs/progress/two-maps-item-10b-loadscreens.md. AirPit's five images are staged by the same
    /// extractor (docs/progress/airpit-release.md, item 9) and still await a host raid.
    /// </summary>
    internal static class LoadingScreenArt
    {
        const string Tag = "[ArenaMapPort][LoadingScreen]";
        const string OverlayName = "ArenaMapPort_LoadingScreenArt";

        static ConfigEntry<bool> _enabled;
        static GameObject _root;
        static Image _image;
        static AspectRatioFitter _fitter;
        static bool _visible;
        static string _visibleForKey;
        // The art as first child of EFT's own deploy screen (normal path), so the screen's text, progress
        // line and BACK button draw on top of it; destroyed on hide (and with the menu UI anyway).
        static GameObject _screenArt;
        static bool _loggedScreenChildren;

        // Staged image paths per map (probed once), and one Sprite per decoded image (texture + sprite built
        // on first use, then reused for every later raid — review 2026-09-27: no Sprite per Show()).
        static readonly Dictionary<string, List<string>> _pathCache = new Dictionary<string, List<string>>();
        static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        static readonly Dictionary<string, string> _lastShownPath = new Dictionary<string, string>();
        static readonly System.Random _rng = new System.Random();

        static string PluginDir => Path.GetDirectoryName(typeof(LoadingScreenArt).Assembly.Location) ?? ".";
        static string LoadscreensDir => Path.Combine(PluginDir, "loadscreens");

        internal static void Install(Harmony harmony)
        {
            _enabled = Plugin.Instance.Settings.Bind(
                "LoadingScreen", "ShowArenaArt", true,
                "Shows one of the ported map's own Arena splash images full-screen while an ArenaMapPort " +
                "raid deploys/loads. Takes effect at the next raid load. false restores EFT's default " +
                "deploy screen background (no image swap; vanilla maps are never affected either way).");

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            // Resolved by AccessTools.TypeByName (string, reflection-only), not typeof(...): these
            // three declaring types all ultimately derive from Odin's SerializedMonoBehaviour
            // (EFT.UI.Screens.UIScreen -> ... -> InputNodeAbstract), which lives in Sirenix.Serialization
            // -- an assembly this csproj does not (and should not) reference. A compile-time typeof()
            // on any of them fails the build with CS0012; a runtime type-name lookup does not, because
            // the game process already has Sirenix.Serialization loaded. This is the FIRST menu/UI
            // target this plugin patches, and the same reasoning covers every future one.
            var matchmakerTimeHasCome = AccessTools.TypeByName("EFT.UI.Matchmaker.MatchmakerTimeHasCome");
            var raidBattleUIScreenController = AccessTools.TypeByName(
                "EFT.UI.EftBattleUIScreen+RaidBattleUIScreenController");
            var tarkovApplication = AccessTools.TypeByName("EFT.TarkovApplication");

            if (matchmakerTimeHasCome == null)
            {
                Plugin.Log.LogError($"{Tag} type not found: EFT.UI.Matchmaker.MatchmakerTimeHasCome — SHOW hook disabled.");
            }
            else
            {
                var show = AccessTools.Method(matchmakerTimeHasCome, "Show",
                    new[] { typeof(IEftSession), typeof(RaidSettings), typeof(MatchmakerPlayersController) });
                if (HarmonyPatches.Verify(show, matchmakerTimeHasCome,
                        "Show(IEftSession, RaidSettings, MatchmakerPlayersController)"))
                {
                    var postfix = AccessTools.Method(typeof(LoadingScreenArt), nameof(ShowPostfix));
                    harmony.Patch(show, postfix: new HarmonyMethod(postfix));
                }
            }

            if (raidBattleUIScreenController == null)
            {
                Plugin.Log.LogError($"{Tag} type not found: EFT.UI.EftBattleUIScreen+RaidBattleUIScreenController — HIDE hook disabled.");
            }
            else
            {
                var showRaidStartInfo = AccessTools.Method(raidBattleUIScreenController, "ShowRaidStartInfo");
                if (HarmonyPatches.Verify(showRaidStartInfo, raidBattleUIScreenController, "ShowRaidStartInfo"))
                {
                    var postfix = AccessTools.Method(typeof(LoadingScreenArt), nameof(HideFromHookPostfix));
                    harmony.Patch(showRaidStartInfo, postfix: new HarmonyMethod(postfix));
                }
            }

            if (tarkovApplication == null)
            {
                Plugin.Log.LogError($"{Tag} type not found: EFT.TarkovApplication — ComebackToMainMenu failsafe hide disabled.");
            }
            else
            {
                var comeback = AccessTools.Method(tarkovApplication, "ComebackToMainMenu");
                if (HarmonyPatches.Verify(comeback, tarkovApplication, "ComebackToMainMenu"))
                {
                    var prefix = AccessTools.Method(typeof(LoadingScreenArt), nameof(ComebackToMainMenuPrefix));
                    harmony.Patch(comeback, prefix: new HarmonyMethod(prefix));
                }
            }

            Plugin.Log.LogDebug($"{Tag} installed (ShowArenaArt={_enabled.Value})");
        }

        // ---- Harmony hooks ---------------------------------------------------------------

        static void ShowPostfix(object __instance, RaidSettings raidSettings)
        {
            try
            {
                var locationId = raidSettings?.LocationId;
                if (string.IsNullOrEmpty(locationId))
                {
                    return;
                }

                foreach (var map in ArenaMapRegistry.All)
                {
                    if (map.Key == locationId)
                    {
                        Show(map, (__instance as Component)?.transform);
                        if (_visible)
                        {
                            HideWhenScreenCloses(__instance);
                        }
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} ShowPostfix threw, ignoring: {e}");
            }
        }

        // Subscribes to the deploy screen controller's OnClose event (a public event on
        // ScreenManager<EEftScreenType>.ScreenController<,>, reached by reflection for the same Sirenix
        // CS0012 reason as the type lookups in Install). The controller is created fresh per raid
        // (TarkovApplication.ShowTimeHasComeScreen), and the handler unsubscribes itself.
        static void HideWhenScreenCloses(object screen)
        {
            var controller = screen == null ? null : Traverse.Create(screen).Field("ScreenController").GetValue();
            var onClose = controller?.GetType().GetEvent("OnClose");
            if (onClose == null)
            {
                Plugin.Log.LogDebug($"{Tag} deploy screen controller has no OnClose event; hiding at ShowRaidStartInfo instead");
                return;
            }

            Action handler = null;
            handler = () =>
            {
                try
                {
                    onClose.RemoveEventHandler(controller, handler);
                    Hide("deploy screen closed");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"{Tag} OnClose handler threw, ignoring: {e}");
                }
            };
            onClose.AddEventHandler(controller, handler);
        }

        static void HideFromHookPostfix()
        {
            try
            {
                Hide("ShowRaidStartInfo");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} HideFromHookPostfix threw, ignoring: {e}");
            }
        }

        static void ComebackToMainMenuPrefix()
        {
            try
            {
                Hide("ComebackToMainMenu");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} ComebackToMainMenuPrefix threw, ignoring: {e}");
            }
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                // Failsafe only: the primary ShowPostfix hook above already covers the normal menu
                // flow before any scene exists. This catches a debug/instant-start path that skips
                // straight to loading a map's Scripts scene without ever calling
                // MatchmakerTimeHasCome.Show — idempotent no-op if already showing this map.
                var map = ArenaMapRaid.MapForScriptsScene(scene.name);
                if (map != null)
                {
                    Show(map);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} OnSceneLoaded threw, ignoring: {e}");
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            try
            {
                // Failsafe #2: independent of both Harmony hide hooks above ever being renamed/
                // disabled — a raid's own Scripts scene unloading (raid end, abort, or restart) must
                // never leave the overlay stuck over a live main menu.
                if (ArenaMapRaid.MapForScriptsScene(scene.name) != null)
                {
                    Hide("sceneUnloaded:" + scene.name);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} OnSceneUnloaded threw, ignoring: {e}");
            }
        }

        // ---- Show/Hide (idempotent) -------------------------------------------------------

        static void Show(ArenaMapDescriptor map, Transform deployScreen = null)
        {
            if (_enabled == null || !_enabled.Value)
            {
                return;
            }

            if (_visible && _visibleForKey == map.Key)
            {
                return; // already showing this exact map's art — no-op, no duplicate log line
            }

            var sprite = PickSprite(map.Key, out var imagePath);
            if (sprite == null)
            {
                return; // no art staged for this map (extractor not run) — silently do nothing
            }

            string layer;
            if (deployScreen != null)
            {
                AttachToDeployScreen(deployScreen, sprite);
                layer = "behind the deploy screen's own UI";
            }
            else
            {
                // Failsafe path (sceneLoaded without the deploy-screen hook): plugin-owned top-level overlay.
                EnsureOverlay();
                _image.sprite = sprite;
                _fitter.aspectRatio = sprite.rect.width / sprite.rect.height;
                _root.SetActive(true);
                layer = "overlay";
            }
            _visible = true;
            _visibleForKey = map.Key;
            Plugin.Log.LogDebug($"{Tag} show {map.Key} ({(int)sprite.rect.width}x{(int)sprite.rect.height}, " +
                               $"{Path.GetFileName(imagePath)}, {layer})");
        }

        // Host 2026-09-27: a top-level overlay canvas hid EFT's "Loading map... (NN%)" line, the location name
        // and the BACK button. As the screen's first child the art replaces only what the screen draws behind
        // its own children.
        static void AttachToDeployScreen(Transform screen, Sprite sprite)
        {
            if (DeveloperOptions.Enabled && !_loggedScreenChildren)
            {
                _loggedScreenChildren = true;
                var names = new List<string>();
                for (var i = 0; i < screen.childCount; i++)
                {
                    var child = screen.GetChild(i);
                    names.Add(child.gameObject.activeSelf ? child.name : child.name + "(off)");
                }
                var rt = screen as RectTransform;
                Plugin.Log.LogDebug($"{Tag} deploy screen '{screen.name}' rect={(rt != null ? rt.rect.size.ToString() : "?")} " +
                                   $"children: {string.Join(", ", names)}");
            }

            if (_screenArt != null)
            {
                UnityEngine.Object.Destroy(_screenArt);
            }
            _screenArt = new GameObject(OverlayName);
            _screenArt.transform.SetParent(screen, worldPositionStays: false);
            _screenArt.transform.SetAsFirstSibling();
            var rect = _screenArt.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = _screenArt.AddComponent<Image>();
            image.type = Image.Type.Simple;
            image.raycastTarget = false; // never swallow clicks meant for BACK or the banner toggles
            image.sprite = sprite;
            var fitter = _screenArt.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = sprite.rect.width / sprite.rect.height;
        }

        static void Hide(string reason)
        {
            if (!_visible)
            {
                return; // already hidden — cheap no-op, safe to call unconditionally
            }

            if (_root != null)
            {
                _root.SetActive(false);
            }
            if (_screenArt != null)
            {
                UnityEngine.Object.Destroy(_screenArt);
            }
            _screenArt = null;
            Plugin.Log.LogDebug($"{Tag} hide ({reason})");
            _visible = false;
            _visibleForKey = null;
        }

        // ---- Overlay construction ----------------------------------------------------------

        static void EnsureOverlay()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject(OverlayName);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.SetActive(false);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Empirically modest: above the base menu Canvas (Unity sorts by Canvas.sortingOrder
            // first, sibling order only within one Canvas), below anything using a dramatically
            // higher order (tooltips/popups). Not derived from MatchmakerTimeHasCome's own internal
            // sortingOrder — that screen's prefab is streamed from a runtime asset bundle, not present
            // in any classic .assets file this session could inspect statically. Revisit if a host
            // test shows this overlay hiding text/UI it should sit behind.
            canvas.sortingOrder = 100;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var imageGo = new GameObject("Image");
            imageGo.transform.SetParent(_root.transform, worldPositionStays: false);
            var rect = imageGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _image = imageGo.AddComponent<Image>();
            _image.type = Image.Type.Simple;
            _image.raycastTarget = false;
            _image.preserveAspect = false; // AspectRatioFitter owns the crop/cover behavior instead

            _fitter = imageGo.AddComponent<AspectRatioFitter>();
            _fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; // Unity's own "cover" fit
        }

        /// <summary>Picks one of the map's staged images at random (never the one shown last time when
        /// there is more than one) and returns its cached Sprite, decoding it on first use.</summary>
        static Sprite PickSprite(string mapKey, out string path)
        {
            path = null;
            var paths = GetStagedPaths(mapKey);
            if (paths.Count == 0)
            {
                return null;
            }

            _lastShownPath.TryGetValue(mapKey, out var last);
            var candidates = paths.Count > 1 ? paths.FindAll(p => p != last) : paths;
            path = candidates[_rng.Next(candidates.Count)];
            _lastShownPath[mapKey] = path;

            if (_spriteCache.TryGetValue(path, out var cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                // markNonReadable: the pixels are only ever drawn, never read back — frees the CPU copy.
                if (!ImageConversion.LoadImage(tex, bytes, true))
                {
                    Plugin.Log.LogError($"{Tag} failed to decode {path} (LoadImage returned false)");
                    return null;
                }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                _spriteCache[path] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} failed to read {path}: {e}");
                return null;
            }
        }

        /// <summary><c>&lt;plugin&gt;/loadscreens/&lt;mapKey&gt;_1.jpg, _2.jpg, ...</c> up to the first gap, probed once
        /// per map and cached (tools/extract/extract_loadscreens.py writes them).</summary>
        static List<string> GetStagedPaths(string mapKey)
        {
            if (_pathCache.TryGetValue(mapKey, out var cached))
            {
                return cached;
            }

            var list = new List<string>();
            for (var index = 1; ; index++)
            {
                var path = Path.Combine(LoadscreensDir, $"{mapKey}_{index}.jpg");
                if (!File.Exists(path))
                {
                    break;
                }
                list.Add(path);
            }

            if (list.Count == 0)
            {
                Plugin.Log.LogDebug($"{Tag} no loadscreen art staged for {mapKey} under {LoadscreensDir} " +
                                    "(run tools/extract/extract_loadscreens.py) — showing EFT's default background");
            }

            _pathCache[mapKey] = list;
            return list;
        }
    }
}
