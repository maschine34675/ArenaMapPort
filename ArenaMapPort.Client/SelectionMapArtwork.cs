using System;
using System.IO;
using BepInEx.Bootstrap;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Displays Arena's original region artwork only for ArenaMode's current menu selection.
    /// Ordinary PMC/Scav selection and installations without ArenaMode keep EFT's artwork.
    /// Its geographic 800px square matches EFT; the surrounding Arena texture padding
    /// must not enter the displayed sprite or native markers/transit paths would shift.
    /// Owns only the screen Image's sprite while open. Marker data and UI geometry stay
    /// native. See docs/progress/selection-map.md for source and host evidence.
    /// </summary>
    internal static class SelectionMapArtwork
    {
        private const string Tag = "[SelectionMap]";
        private static Image _image;
        private static Sprite _original;
        private static Sprite _sprite;
        private static Texture2D _texture;
        private static bool _loadAttempted;
        private static bool _warned;
        private static Func<RaidSettings, bool> _isArenaSelection;

        internal static void Install(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(ArenaModeMapPositionIntegration.PluginGuid, out var arenaMode))
                return;
            try
            {
                // Optional integration, resolved from the registered plugin rather than a global
                // selection flag: IsSelectedFor also checks the exact flow's settings and PMC side.
                var selection = arenaMode.Instance.GetType().Assembly.GetType("ArenaMode.Session.ArenaMenuSelection");
                var isSelectedFor = selection == null ? null :
                    AccessTools.DeclaredMethod(selection, "IsSelectedFor", new[] { typeof(RaidSettings) });
                if (isSelectedFor == null || !isSelectedFor.IsStatic || isSelectedFor.ReturnType != typeof(bool))
                    throw new MissingMethodException("ArenaMode menu selection IsSelectedFor(RaidSettings)");
                _isArenaSelection = (Func<RaidSettings, bool>)Delegate.CreateDelegate(
                    typeof(Func<RaidSettings, bool>), isSelectedFor);
                var show = AccessTools.DeclaredMethod(typeof(MatchMakerSelectionLocationScreen), "ShowInternal");
                var close = AccessTools.DeclaredMethod(typeof(MatchMakerSelectionLocationScreen), "Close");
                if (show == null || close == null)
                    throw new MissingMethodException("Location selection ShowInternal/Close");
                harmony.Patch(show, prefix: new HarmonyMethod(typeof(SelectionMapArtwork), nameof(Restore)),
                    postfix: new HarmonyMethod(typeof(SelectionMapArtwork), nameof(Show)));
                harmony.Patch(close, prefix: new HarmonyMethod(typeof(SelectionMapArtwork), nameof(Restore)));
            }
            catch (Exception e) { Warn(e); }
        }

        private static void Show(MatchMakerSelectionLocationScreen __instance, RaidSettings raidSettings)
        {
            try
            {
                // Restore runs unconditionally before native ShowInternal, including re-entry
                // into PMC/Scav. Do not load or touch the image unless this exact flow is Arena.
                if (_isArenaSelection?.Invoke(raidSettings) != true) return;
                var container = __instance._locationButtonsContainer;
                var image = container?.parent?.GetComponent<Image>();
                var original = image == null ? null : image.sprite;
                // Bind through EFT's own marker container, never by a global object-name search.
                if (original == null || original.name != "region_map_external" ||
                    original.rect.width != 800 || original.rect.height != 800 ||
                    image.overrideSprite != original)
                    throw new InvalidOperationException("Unrecognized location-map image; keeping its original artwork");
                if (!EnsureArtwork()) return;
                _image = image;
                _original = original;
                image.sprite = _sprite;
                if (image.overrideSprite != _sprite)
                    throw new InvalidOperationException("Another UI override controls the location-map sprite");
                Plugin.Log.LogDebug($"{Tag} Arena region artwork active; native anchors and paths preserved.");
            }
            catch (Exception e) { Restore(); Warn(e); }
        }

        private static bool EnsureArtwork()
        {
            if (_sprite != null) return true;
            if (_loadAttempted) return false;
            _loadAttempted = true;
            try
            {
                string folder = Path.GetDirectoryName(typeof(SelectionMapArtwork).Assembly.Location);
                string path = Path.Combine(folder, "map-selection", "region-map.png");
                _texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "ArenaMapPort_RegionMapTexture",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (!ImageConversion.LoadImage(_texture, File.ReadAllBytes(path), true) ||
                    _texture.width != 1080 || _texture.height != 1080)
                    throw new InvalidDataException("Unsupported region-map.png dimensions or encoding");
                _sprite = Sprite.Create(_texture, new Rect(140, 280, 800, 800),
                    new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect);
                _sprite.name = "ArenaMapPort_RegionMap";
                return true;
            }
            catch (Exception e)
            {
                if (_texture != null) UnityEngine.Object.Destroy(_texture);
                _texture = null;
                Warn(e);
                return false;
            }
        }

        private static void Restore()
        {
            if (_image != null && _image.sprite == _sprite) _image.sprite = _original;
            _image = null;
            _original = null;
        }

        internal static void Dispose()
        {
            Restore();
            if (_sprite != null) UnityEngine.Object.Destroy(_sprite);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _sprite = null;
            _texture = null;
            _loadAttempted = false;
            _isArenaSelection = null;
        }

        private static void Warn(Exception e)
        {
            if (_warned) return;
            _warned = true;
            Plugin.Log.LogWarning($"{Tag} Arena selection artwork unavailable; using the original map: {e.Message}");
        }
    }
}
