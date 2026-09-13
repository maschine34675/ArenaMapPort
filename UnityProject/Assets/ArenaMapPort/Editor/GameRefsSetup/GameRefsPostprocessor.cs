// ArenaMapPort WP2 — LEAD OVERRIDE O2:
// "PluginImporter compatibility: Editor + StandaloneWindows64 from the start (no Player is ever
// built)." This AssetPostprocessor configures every DLL under
// Assets/ArenaMapPort/Editor/GameRefs/ (synced by tools/sync-game-refs.ps1) the moment Unity first
// imports it, so the setting is correct from the very first import — no separate manual step, no
// reliance on whatever Unity's own default happens to be for a freshly-dropped plugin DLL.
//
// Deliberately narrow: Editor + StandaloneWindows64 only, every other platform explicitly off.
// This project only ever builds AssetBundles targeted at StandaloneWindows64 (§B.WP2.8) and never
// a Player, but the bundle build pipeline still needs these Editor-only Assembly-CSharp-derived
// MonoBehaviour types to serialize correctly into a StandaloneWindows64-targeted bundle (§G.S1's
// GameRefs-round-trip check) — hence both platforms, not Editor-only.
using System.IO;
using UnityEditor;

namespace ArenaMapPort.Editor.GameRefsSetup
{
    internal class GameRefsPostprocessor : AssetPostprocessor
    {
        private const string GameRefsFolder = "Assets/ArenaMapPort/Editor/GameRefs/";

        private void OnPreprocessAsset()
        {
            if (!assetPath.StartsWith(GameRefsFolder)) return;
            if (Path.GetExtension(assetPath).ToLowerInvariant() != ".dll") return;

            PluginImporter importer = assetImporter as PluginImporter;
            if (importer == null) return;

            // NOTE (2026-09-13, see docs/progress/wp2.md "S1 crash diagnosis"): the S1 pass this
            // comment dates from isolation-tested a reproducible native Editor SIGSEGV
            // (MonoScriptInfoScraper::ScanForSourceGeneratedMonoScriptInfo during domain reload)
            // against BOTH "Editor + StandaloneWindows64" and "Editor-only" for this flag — the
            // crash occurred either way (now FIXED separately, s1-finish task item 1, by removing
            // the type the crash was choking on via tools/gamerefs-fixup — see sync-game-refs.ps1).
            //
            // s1-finish EXPERIMENT (this pass, reverted): with the SIGSEGV fixed, a NEW, separate
            // symptom appeared — `AddComponent<LevelSettings>()` (and every other §D EFT type)
            // permanently returns null with Unity logging "Script X has not finished compilation
            // yet" for the rest of the batchmode process's life. Tried Editor-ONLY compatibility
            // here to test whether the dual-platform flag was the cause (never itself tested
            // against THIS symptom — WP2's run 13 only tested it against the SIGSEGV) — result:
            // IDENTICAL failure under Editor-only too (confirmed, docs/progress/s1-finish.md), so
            // this flag is not the cause either. Reverted to O2's literal "Editor +
            // StandaloneWindows64 from the start" since it's evidence-neutral and matches the
            // lead's original instruction. This AddComponent issue remains UNRESOLVED as of
            // s1-finish (see docs/progress/s1-finish.md "Second, separate blocker" for the full
            // diagnostic trail and every mitigation tried, none successful) — BuildBundles.cs's
            // EditorApplication.update-tick retry around AuthorScriptsScene.Build is a cheap
            // best-effort mechanism kept for forward-compatibility, not a confirmed fix.
            bool alreadyConfigured =
                importer.GetCompatibleWithEditor()
                && importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)
                && !importer.GetCompatibleWithAnyPlatform();
            if (alreadyConfigured) return;

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);

            // Belt-and-suspenders: explicitly disable every other platform Unity knows about here,
            // so a stale .meta from a prior Unity version can't leave a platform silently enabled.
            foreach (BuildTarget bt in System.Enum.GetValues(typeof(BuildTarget)))
            {
                if (bt == BuildTarget.StandaloneWindows64) continue;
                if (!System.Enum.IsDefined(typeof(BuildTarget), bt)) continue;
                try { importer.SetCompatibleWithPlatform(bt, false); }
                catch { /* obsolete/unsupported BuildTarget values throw - ignore */ }
            }

            importer.SaveAndReimport();
        }
    }
}
