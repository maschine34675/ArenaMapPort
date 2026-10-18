using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// bots-stage1 (docs/progress/bots-stage1.md, "Host test 2") — bot spawn diagnostics for ported maps.
    /// The second host raid had the server generating bots, the ten markers in the scene, and still
    /// <c>Bots alive: 0</c> for 15 minutes, with no line anywhere: EFT's own AILogger is off and the
    /// wave/zone/point path fails silently (every "no" is a SimpleBotSpawnDelayModel that is retried
    /// later). These postfixes log, only while one of our maps is loaded, each step's decision:
    ///  - BotsController.ActivateBotsByWave(SpawnWave): the wave, _canSpawn, spawner present;
    ///  - BotZone.Init: markers/points per zone after the params-created markers were linked;
    ///  - BotSpawner.TryToSpawnInZoneInner: zone, count, provided points, and WHY it deferred
    ///    (profiles not loaded / zone blocked / no free space / cap / points invalid) or started;
    ///  - SpawnSystem.SelectAISpawnPoints: candidates vs. returned, plus every candidate's own
    ///    IsValidSpawn verdict, position, sides, categories.
    /// Cheap (a few lines per wave), off with [Bots] SpawnDiagnostics = false.
    ///
    /// Second tier, <c>[Bots] ActivationDiagnostics</c> (default OFF, read at plugin start): finalizers on the
    /// async activation chain, a finalizer on every method <c>BotOwner.method_10</c> calls (46 patches) and a
    /// 15-second per-bot status dump. That is what found the null holders, the 0×0×0 voxel grid and the missing
    /// patrol ways (host tests 4–11); it costs a patch per activation step and a log line per 15 s per raid, so it
    /// stays a switch for the next silent bot problem rather than a default. A new key on purpose: BepInEx
    /// persists the old <c>SpawnDiagnostics = true</c>, so changing that default would not reach existing configs.
    /// </summary>
    internal static class BotSpawnProbe
    {
        const string Tag = "[BotSpawn]";
        static ConfigEntry<bool> _enabled;
        static ConfigEntry<bool> _activation;
        static readonly FieldInfo CanSpawnField = AccessTools.Field(typeof(BotsController), "_canSpawn");

        static bool On => _enabled != null && _enabled.Value && ArenaMapRaid.IsLoaded();
        static bool Deep => On && _activation != null && _activation.Value;
        static float _lastPointDump = -999f;

        internal static void Install(Harmony harmony)
        {
            _enabled = Plugin.Instance.Settings.Bind("Bots", "SpawnDiagnostics", true,
                "bots-stage1 — log EFT's bot wave/zone/spawn-point decisions on ported maps (a few lines per wave). " +
                "Vanilla maps are never logged.");
            _activation = Plugin.Instance.Settings.Bind("Bots", "ActivationDiagnostics", false,
                "bots-stage1 — deep bot-activation diagnostics on ported maps: finalizers on the async spawn chain and on " +
                "every step of BotOwner's activation (46 Harmony patches), plus a 15-second status line per living bot " +
                "(state, position, active BigBrain layer, standby, mover). Read at game start — restart EFT after changing. " +
                "Turn on when bots register but never move.");

            if (!DeveloperOptions.Enabled) return;
            TryPatch(harmony, "BotsController.ActivateBotsByWave(SpawnWave)",
                AccessTools.Method(typeof(BotsController), nameof(BotsController.ActivateBotsByWave), new[] { typeof(SpawnWave) }),
                prefix: nameof(ActivateWavePrefix));
            TryPatch(harmony, "BotZone.Init",
                AccessTools.Method(typeof(BotZone), nameof(BotZone.Init)),
                postfix: nameof(ZoneInitPostfix));
            TryPatch(harmony, "BotSpawner.TryToSpawnInZoneInner",
                AccessTools.Method(typeof(BotSpawner), nameof(BotSpawner.TryToSpawnInZoneInner)),
                postfix: nameof(TryToSpawnInZoneInnerPostfix));
            TryPatch(harmony, "SpawnSystem.SelectAISpawnPoints",
                AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.SelectAISpawnPoints)),
                postfix: nameof(SelectAISpawnPointsPostfix));

            // tier 1 (light): one line per created bot and per registered bot
            TryPatch(harmony, "BotOwner.Create (created line)",
                AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create)),
                postfix: nameof(BotOwnerCreatePostfix));
            TryPatch(harmony, "BotSpawner.ActivateBotCallback",
                AccessTools.Method(typeof(BotSpawner), nameof(BotSpawner.ActivateBotCallback)),
                postfix: nameof(ActivateBotCallbackPostfix));

            if (!_activation.Value)
            {
                Plugin.Log.LogDebug("[Harmony] bots-stage1: deep activation diagnostics off ([Bots] ActivationDiagnostics)");
                return;
            }

            // Host test 3 (2026-09-19 20:4x): the body of every spawned scav appeared in the scene, but no bot
            // ever became a living AI (UiBridge: 0 alive, GameWorld: +1 registered player) and no log anywhere
            // — the async activation chain swallows its exception. Finalizers on each link log it.
            TryPatch(harmony, "BotCreatorClient.CreateBot",
                AccessTools.Method(typeof(BotCreatorClient), nameof(BotCreatorClient.CreateBot)),
                finalizer: nameof(CreateBotFinalizer));
            TryPatch(harmony, "BotCreatorClient.method_3",
                AccessTools.Method(typeof(BotCreatorClient), "method_3"),
                finalizer: nameof(Method3Finalizer));
            TryPatch(harmony, "BotOwner.Create",
                AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create)),
                finalizer: nameof(BotOwnerCreateFinalizer));
            TryPatch(harmony, "BotOwner.PreActivate",
                AccessTools.Method(typeof(BotOwner), nameof(BotOwner.PreActivate)),
                finalizer: nameof(PreActivateFinalizer));

            // Host test 7 (2026-09-19 21:46): bots register (ActivateBotCallback) and then never move.
            // BotOwner.method_10 — the real activation, ~45 sub-system Activate() calls — wraps everything in
            // `catch (Exception) { BotState = EBotState.ActiveFail; }` (BotOwner.cs:940-1000): one throw
            // anywhere and the bot stands at its spawn point forever, silently. A finalizer on method_10
            // itself would see nothing (the catch is inside). So: read method_10's IL, and put a finalizer
            // on every method it calls — the callee's finalizer sees the exception before the catch eats it.
            InstallActivationStepFinalizers(harmony);
            TryPatch(harmony, "BotOwner.method_10 (activation)",
                AccessTools.Method(typeof(BotOwner), "method_10"),
                postfix: nameof(ActivationPostfix));
        }

        static readonly HashSet<MethodBase> _stepTargets = new HashSet<MethodBase>();

        static void InstallActivationStepFinalizers(Harmony harmony)
        {
            var activation = AccessTools.Method(typeof(BotOwner), "method_10");
            if (activation == null)
            {
                Plugin.Log.LogWarning($"{Tag} BotOwner.method_10 not found — activation-step diagnostics not installed");
                return;
            }
            int patched = 0, failed = 0;
            var finalizer = new HarmonyMethod(AccessTools.Method(typeof(BotSpawnProbe), nameof(ActivationStepFinalizer)));
            try
            {
                foreach (var instruction in PatchProcessor.ReadMethodBody(activation))
                {
                    if (!(instruction.Value is MethodInfo callee) || callee.DeclaringType == null) continue;
                    if (callee.DeclaringType.IsGenericType || callee.IsGenericMethod || callee.IsAbstract) continue;
                    if (callee.DeclaringType.Namespace != null && callee.DeclaringType.Namespace.StartsWith("System")) continue;
                    if (callee.DeclaringType.Namespace != null && callee.DeclaringType.Namespace.StartsWith("UnityEngine")) continue;
                    if (callee.IsSpecialName) continue; // property getters (BotsGroup, BotZone, …) — not the failing work
                    if (!_stepTargets.Add(callee)) continue;
                    try
                    {
                        harmony.Patch(callee, finalizer: finalizer);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Plugin.Log.LogDebug($"{Tag} could not add step finalizer to {callee.DeclaringType.Name}.{callee.Name}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} reading BotOwner.method_10 IL failed: {e.GetType().Name}: {e.Message}");
            }
            Plugin.Log.LogDebug($"[Harmony] bots-stage1: finalizers on {patched} activation step(s) called by BotOwner.method_10 ({failed} skipped)");
        }

        static Exception ActivationStepFinalizer(Exception __exception, MethodBase __originalMethod, object __instance)
        {
            if (__exception != null && Deep)
            {
                Plugin.Log.LogError($"{Tag} activation step {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} THREW " +
                                    $"(instance {__instance?.GetType().Name}): {__exception.GetType().Name}: {__exception.Message}\n{__exception.StackTrace}");
            }
            return __exception;
        }

        static UnityEngine.Coroutine _statusDump;

        // Host test 8 (22:05): bots reach state=Active and still do not move. Once a bot is Active, dump every
        // 15 s per living bot: state, position, BigBrain's active layer, mover/standby flags — the difference
        // between "SAIN layer running but no path" and "vanilla PatrolAssault with zero patrol ways".
        static void EnsureStatusDump()
        {
            if (_statusDump != null || Plugin.Instance == null) return;
            _statusDump = Plugin.Instance.StartCoroutine(StatusDump());
        }

        static System.Collections.IEnumerator StatusDump()
        {
            var wait = new WaitForSeconds(15f);
            while (Deep)
            {
                yield return wait;
                if (!Deep) break;
                try
                {
                    // AbstractGame is not a component on GameWorld — EFT registers the game as Singleton<IBotGame>/<AbstractGame>.
                    IBotGame game = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance
                        : Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance as IBotGame : null;
                    var owners = game?.BotsController?.Bots?.BotOwners;
                    if (owners == null)
                    {
                        Plugin.Log.LogDebug($"{Tag} status: no bot controller reachable (game={(game != null)})");
                        continue;
                    }
                    var sb = new StringBuilder();
                    sb.Append($"{Tag} status: {owners.Count()} bot(s)");
                    foreach (var bot in owners)
                    {
                        if (bot == null) continue;
                        string layer;
                        try { layer = bot.Brain?.ActiveLayerName(); } catch (Exception e) { layer = "(" + e.GetType().Name + ")"; }
                        string standBy;
                        try { standBy = bot.StandBy?.StandByType.ToString(); } catch (Exception e) { standBy = "(" + e.GetType().Name + ")"; }
                        string mover;
                        try { mover = bot.Mover == null ? "null" : $"moving={bot.Mover.IsMoving} sprint={bot.Mover.Sprinting} pause={bot.Mover.Pause}"; }
                        catch (Exception e) { mover = "(" + e.GetType().Name + ")"; }
                        string patrol;
                        try
                        {
                            var pd = bot.PatrollingData;
                            var target = pd?.CurPatrolPoint?.TargetPoint;
                            patrol = pd == null ? "null" :
                                $"patrol={pd.Status} chooser={pd.PointChooser?.GetType().Name ?? "null"} way={pd.Way?.name ?? "null"} " +
                                $"target={(target == null ? "null" : target.position.ToString("F1"))}";
                        }
                        catch (Exception e) { patrol = "patrol=(" + e.GetType().Name + ")"; }
                        string group;
                        try { group = $"group={bot.BotsGroup?.MembersCount ?? -1} boss={bot.Boss?.IamBoss} follower={bot.BotFollower?.HaveBoss}"; }
                        catch (Exception e) { group = "group=(" + e.GetType().Name + ")"; }
                        sb.Append($"\n    '{bot.name}' {bot.Profile?.Info?.Settings?.Role} state={bot.BotState} alive={bot.HealthController?.IsAlive} " +
                                  $"pos={bot.Position:F1} layer={layer ?? "null"} standBy={standBy} peace={bot.Memory?.IsPeace} {mover} {patrol} {group}");
                    }
                    Plugin.Log.LogDebug(sb.ToString());
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"{Tag} status dump failed: {e.GetType().Name}: {e.Message}");
                }
            }
            _statusDump = null;
        }

        static void ActivationPostfix(BotOwner __instance)
        {
            if (!Deep) return;
            EnsureStatusDump();
            string layer = null;
            try { layer = __instance?.Brain?.ActiveLayerName(); } catch (Exception e) { layer = "(" + e.GetType().Name + ")"; }
            Plugin.Log.LogDebug($"{Tag} BotOwner.method_10 done: '{__instance?.name}' state={__instance?.BotState} " +
                               $"pos={__instance?.Position:F1} activeLayer={layer ?? "null"} zone='{__instance?.BotsGroup?.BotZone?.name}' " +
                               $"patrolWays={__instance?.BotsGroup?.BotZone?.PatrolWays?.Length ?? -1}");
        }

        static Exception CreateBotFinalizer(Exception __exception, Profile profile) =>
            LogFinalizer("BotCreatorClient.CreateBot", __exception, profile?.Info?.Nickname);

        static Exception Method3Finalizer(Exception __exception, BotOwner bot) =>
            LogFinalizer("BotCreatorClient.method_3 (PreActivate wrapper)", __exception, bot?.name);

        static Exception BotOwnerCreateFinalizer(Exception __exception, Player player) =>
            LogFinalizer("BotOwner.Create", __exception, player?.Profile?.Info?.Nickname);

        static Exception PreActivateFinalizer(Exception __exception, BotOwner __instance) =>
            LogFinalizer("BotOwner.PreActivate", __exception, __instance?.name);

        static Exception LogFinalizer(string where, Exception e, string who)
        {
            if (e != null && On)
            {
                Plugin.Log.LogError($"{Tag} {where} THREW for '{who}': {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
            return e; // never swallow — same behaviour as before, just visible
        }

        static void BotOwnerCreatePostfix(BotOwner __result, AICorePoint corePointId)
        {
            if (!On) return;
            Plugin.Log.LogDebug($"{Tag} BotOwner.Create -> '{__result?.name}' startCorePoint={(corePointId == null ? "NULL" : corePointId.Id.ToString())}");
        }

        static void ActivateBotCallbackPostfix(BotOwner bot, BotCreationData data)
        {
            if (!On) return;
            Plugin.Log.LogDebug($"{Tag} ActivateBotCallback: bot='{bot?.name}' role={bot?.Profile?.Info?.Settings?.Role} spawnStopped={data?.SpawnStopped} — bot registered with BotsController");
        }

        static void TryPatch(Harmony harmony, string label, MethodBase target, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                if (target == null)
                {
                    Plugin.Log.LogWarning($"{Tag} {label}: target not found — diagnostic not installed");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(BotSpawnProbe), prefix)) : null,
                    postfix: postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(BotSpawnProbe), postfix)) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(AccessTools.Method(typeof(BotSpawnProbe), finalizer)) : null);
                Plugin.Log.LogDebug($"[Harmony] patched {label} (bots-stage1 spawn diagnostics)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} {label}: patch failed — {e.GetType().Name}: {e.Message}");
            }
        }

        // BotsController.ActivateBotsByWave(SpawnWave) — async, so a prefix (runs synchronously first).
        static void ActivateWavePrefix(BotsController __instance, SpawnWave wave)
        {
            if (!On) return;
            bool canSpawn = CanSpawnField != null && CanSpawnField.GetValue(__instance) is bool b && b;
            Plugin.Log.LogDebug($"{Tag} wave fired: zone='{wave?.SpawnAreaName}' count={wave?.BotsCount} side={wave?.Side} " +
                $"type={wave?.WildSpawnType} difficulty={wave?.Difficulty} time={wave?.Time:0.0}s withCheckMinMax={wave?.WithCheckMinMax} " +
                $"| _canSpawn={canSpawn} spawner={(__instance.BotSpawner != null)}");
        }

        static void ZoneInitPostfix(BotZone __instance)
        {
            if (!On) return;
            int onMesh = 0;
            var markers = __instance.SpawnPointMarkers;
            if (markers != null)
            {
                foreach (var m in markers)
                {
                    if (m != null && BotZone.IsOnNavMesh(m.transform.position)) onMesh++;
                }
            }
            Plugin.Log.LogDebug($"{Tag} BotZone.Init '{__instance.name}': markers={markers?.Count ?? -1} onNavMesh={onMesh} " +
                $"points={__instance.SpawnPoints?.Length ?? -1} maxPersons={__instance.MaxPersons} patrolWays={__instance.PatrolWays?.Length ?? -1} " +
                $"snipe={__instance.SnipeZone} canSpawnBoss={__instance.CanSpawnBoss}");
        }

        static void TryToSpawnInZoneInnerPostfix(BotSpawner __instance, BotZone botZone, BotCreationData data, int count,
            bool withCheckMinMax, bool newWave, List<ISpawnPoint> pointsToSpawn, bool forcedSpawn, SimpleBotSpawnDelayModel __result)
        {
            if (!On) return;
            var sb = new StringBuilder();
            sb.Append($"{Tag} TryToSpawnInZoneInner zone='{botZone?.name}' count={count} newWave={newWave} withCheckMinMax={withCheckMinMax} forced={forcedSpawn} ");
            sb.Append($"providedPoints={(pointsToSpawn == null ? "null" : pointsToSpawn.Count.ToString())} -> {(__result == null ? "STARTED (or nothing to do)" : "DEFERRED")} ");
            try
            {
                sb.Append($"| profilesLoaded={__instance.IsProfilesLoaded} spawnStopped={data?.SpawnStopped} ");
                sb.Append($"maxBots={__instance.MaxBots} allBotsWithLoaded={__instance.AllBotsWithLoaded} ");
                if (botZone != null)
                {
                    sb.Append($"zoneFreeSpace={botZone.HaveFreeSpace(count)} zonePoints={botZone.SpawnPoints?.Length ?? -1} ");
                    var leave = __instance.BotGame?.BotsController?.ZonesLeaveController;
                    if (leave != null && data != null)
                    {
                        sb.Append($"canAtZoneByType={data.CanAtZoneByType(botZone, leave)} ");
                    }
                }
                if (data?.Profiles != null && data.Profiles.Count > 0)
                {
                    var p = data.Profiles[0];
                    sb.Append($"profiles={data.Profiles.Count} role={p?.Info?.Settings?.Role} side={p?.Info?.Side} ");
                }
                if (pointsToSpawn != null && data != null)
                {
                    int ok = 0;
                    foreach (var sp in pointsToSpawn)
                    {
                        if (sp != null && __instance.BotGame?.BotsController != null)
                        {
                            // same check TryToSpawnInZoneInner applies to provided points
                            var sys = AccessTools.Field(typeof(BotSpawner), "_spawnSystem")?.GetValue(__instance) as ISpawnSystem;
                            if (sys != null && sys.IsValidSpawn(sp, data, Time.time)) ok++;
                        }
                    }
                    sb.Append($"providedValid={ok}/{pointsToSpawn.Count} ");
                }
            }
            catch (Exception e)
            {
                sb.Append($"(probe detail failed: {e.GetType().Name}: {e.Message})");
            }
            Plugin.Log.LogDebug(sb.ToString());
        }

        static void SelectAISpawnPointsPostfix(SpawnSystem __instance, BotCreationData profileData, BotZone zone, int maxCount, float time,
            List<ISpawnPoint> __result)
        {
            if (!On) return;
            var sb = new StringBuilder();
            sb.Append($"{Tag} SelectAISpawnPoints zone='{zone?.name}' requested={maxCount} returned={__result?.Count ?? -1} candidates={zone?.SpawnPoints?.Length ?? -1} side={profileData?.Side}");
            try
            {
                var points = zone?.SpawnPoints;
                if (points != null && Time.time - _lastPointDump > 30f) // retries come every 3 s — dump the verdicts at most twice a minute
                {
                    _lastPointDump = Time.time;
                    int shown = 0;
                    foreach (var sp in points)
                    {
                        if (sp == null || shown++ >= 12) continue;
                        bool valid = __instance.IsValidSpawn(sp, profileData, time);
                        sb.Append($"\n    '{sp.Name}' pos={sp.Position:F1} sides={sp.Sides} categories={sp.Categories} delay={sp.DelayToCanSpawnSec:0} " +
                                  $"nextBorn={sp.NextBornTime:0.0} now={time:0.0} valid={valid}");
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append($" (probe detail failed: {e.GetType().Name}: {e.Message})");
            }
            Plugin.Log.LogDebug(sb.ToString());
        }
    }
}
