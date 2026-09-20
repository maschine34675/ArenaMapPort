using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArenaMapPort.Shared;
using CommonAssets.Scripts.Game;
using EFT.Interactive;
using HarmonyLib;
using JsonType;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// exfils-stage2 (docs/progress/exfils-stage2.md) — exfiltration points authored at runtime.
    ///
    /// EFT builds its exits from scene objects: <c>ExfiltrationController.InitAllExfiltrationPoints</c>
    /// (CommonAssets.Scripts.Game/ExfiltrationController.cs:222-262) collects every <see cref="ExfiltrationPoint"/>
    /// via <c>LocationScene.GetAllObjects&lt;ExfiltrationPoint&gt;()</c>, matches each one to the server's
    /// <c>exits[]</c> by exact <c>Settings.Name</c>, and only then <c>LoadSettings</c> (timing, chance,
    /// EntryPoints → <c>EligibleEntryPoints</c>, lowercased and compared with <c>Profile.Info.EntryPoint</c>, which
    /// is our spawn point's Infiltration). A point without a matching name is set NotPresent; a server exit
    /// without a scene point simply never exists. Our scenes ship none, so this prefix authors one GameObject per
    /// <see cref="ArenaMapDescriptor.Exits"/> entry — BoxCollider first (<c>ExfiltrationPoint.Awake</c>,
    /// ExfiltrationPoint.cs:167-173, dereferences it), then the component, then <c>Settings.Name</c> (the match loop
    /// reads it BEFORE LoadSettings, and <c>ExitTriggerSettings.Load</c> never copies the name) — and registers
    /// them with a loaded <c>LocationScene</c>'s type container (<c>_typeToContainer</c>, filled once in its Awake;
    /// <c>Register&lt;T&gt;</c> would throw on a second registration, so the container entry is replaced). Rebuilt
    /// on every call, never cached across raids (ArenaMapRaid.cs's own rule).
    ///
    /// Not a Harmony patch on the generic <c>GetAllObjects&lt;T&gt;</c>: for reference types Mono shares one body
    /// across instantiations, so a postfix typed for ExfiltrationPoint would run for every T.
    ///
    /// Trigger → raid end: <c>ExfiltrationPoint.OnTriggerEnter → Proceed → OnStartExtraction</c> →
    /// <c>EndByExitTrigerScenario</c> counts ExfiltrationTime down → <c>BaseLocalGame.Stop(profileId, Survived|Runner,
    /// exitName)</c>. SPT's raid-end handler passes the exit name through without validating it.
    /// </summary>
    internal static class ExfilRepairs
    {
        const string Tag = "[ArenaMapPort][Exfil]";
        const float SampleRadius = 6f;
        static readonly List<ExfiltrationPoint> _synthetic = new List<ExfiltrationPoint>();
        static readonly FieldInfo ContainerField = AccessTools.Field(typeof(LocationScene), "_typeToContainer");

        internal static void Install(Harmony harmony)
        {
            var init = AccessTools.Method(typeof(ExfiltrationController), nameof(ExfiltrationController.InitAllExfiltrationPoints));
            if (init == null || ContainerField == null)
            {
                Plugin.Log.LogError($"{Tag} target missing (InitAllExfiltrationPoints={init != null} LocationScene._typeToContainer={ContainerField != null}) " +
                                    "— no exfiltration points on ported maps this session (raids end by timer only)");
                return;
            }
            harmony.Patch(init,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ExfilRepairs), nameof(InitPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ExfilRepairs), nameof(InitPostfix))));
            Plugin.Log.LogInfo("[ArenaMapPort][Harmony] patched ExfiltrationController.InitAllExfiltrationPoints (exfils-stage2: runtime-authored exits)");
        }

        // ExfiltrationController.InitAllExfiltrationPoints(MongoID locationId, BackendExitTriggerSettings[] settings, ...)
        static void InitPrefix(BackendExitTriggerSettings[] settings)
        {
            _synthetic.Clear();
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || map.Exits == null || map.Exits.Length == 0)
            {
                return;
            }
            try
            {
                Build(map, settings);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} authoring exits for {map.Key} failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        static void Build(ArenaMapDescriptor map, BackendExitTriggerSettings[] settings)
        {
            var serverNames = new HashSet<string>(StringComparer.Ordinal);
            if (settings != null)
            {
                foreach (var s in settings)
                {
                    if (!string.IsNullOrEmpty(s?.Name)) serverNames.Add(s.Name);
                }
            }
            var scene = LocationScene.LoadedScenes.FirstOrDefault(s => s != null);
            if (scene == null)
            {
                Plugin.Log.LogWarning($"{Tag} no LocationScene is loaded — exits cannot be registered (the map's Scripts scene should carry one)");
                return;
            }

            foreach (var exit in map.Exits)
            {
                if (serverNames.Count > 0 && !serverNames.Contains(exit.Name))
                {
                    Plugin.Log.LogWarning($"{Tag} the server's exits carry no '{exit.Name}' — skipped; regenerate base.json from " +
                                          $"tools/maps/{map.Key.Replace("arena_", "")}.exits.json and restart the server");
                    continue;
                }
                var authored = new Vector3(exit.X, exit.Y, exit.Z);
                Vector3 floor = authored;
                string basis = $"authored position kept — nothing on the NavMesh within {SampleRadius:0} m, the box may be unreachable";
                if (NavMesh.SamplePosition(authored, out var hit, SampleRadius, NavMesh.AllAreas))
                {
                    floor = hit.position;
                    basis = $"NavMesh-sampled, {Vector3.Distance(hit.position, authored):F1} m from authored";
                }

                var go = new GameObject(exit.Name);
                go.layer = LayerMask.NameToLayer("Triggers");
                // the box spans 0.5 m below the floor point to SizeY - 0.5 m above it
                go.transform.position = floor + Vector3.up * (exit.SizeY * 0.5f - 0.5f);
                go.transform.rotation = Quaternion.Euler(0f, exit.RotationY, 0f);
                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.center = Vector3.zero;
                box.size = new Vector3(exit.SizeX, exit.SizeY, exit.SizeZ);
                var point = go.AddComponent<ExfiltrationPoint>(); // Awake: layer Triggers, reads the BoxCollider
                point.Settings.Name = exit.Name;                  // matched against settings[].Name before LoadSettings
                _synthetic.Add(point);
                Plugin.Log.LogInfo($"{Tag} authored exit '{exit.Name}': box {box.size} at floor {floor:F1} ({basis})");
            }

            foreach (var name in serverNames)
            {
                if (!map.Exits.Any(e => e.Name == name))
                {
                    Plugin.Log.LogInfo($"{Tag} the server ships exit '{name}' without a client position — it has no scene object and stays NotPresent");
                }
            }

            if (_synthetic.Count > 0)
            {
                RegisterWithScene(scene, _synthetic);
            }
        }

        static void RegisterWithScene(LocationScene scene, List<ExfiltrationPoint> points)
        {
            var containers = ContainerField.GetValue(scene) as Dictionary<Type, Array>;
            if (containers == null)
            {
                Plugin.Log.LogWarning($"{Tag} LocationScene._typeToContainer has an unexpected shape — exits authored but not discoverable");
                return;
            }
            containers.TryGetValue(typeof(ExfiltrationPoint), out var existing);
            var merged = (existing as ExfiltrationPoint[] ?? Array.Empty<ExfiltrationPoint>())
                .Where(p => p != null)
                .Concat(points)
                .Distinct()
                .ToArray();
            containers[typeof(ExfiltrationPoint)] = merged;
            Plugin.Log.LogInfo($"{Tag} registered {points.Count} exit(s) with LocationScene '{scene.gameObject.scene.name}' " +
                               $"({merged.Length} ExfiltrationPoint(s) in its container now)");
        }

        static void InitPostfix()
        {
            if (_synthetic.Count == 0)
            {
                return;
            }
            foreach (var point in _synthetic)
            {
                if (point == null) continue;
                string eligible = string.Join(",", point.EligibleEntryPoints ?? Array.Empty<string>());
                var line = $"{Tag} exit '{point.Settings?.Name}' after init: status={point.Status} eligibleEntryPoints=[{eligible}] " +
                           $"chance={point.Settings?.Chance:0} time={point.Settings?.ExfiltrationTime:0}s";
                if (point.Status == EExfiltrationStatus.NotPresent)
                {
                    Plugin.Log.LogWarning(line + " — NotPresent: the name matched no server exit or its chance roll failed");
                }
                else
                {
                    Plugin.Log.LogInfo(line);
                }
            }
        }
    }
}
