using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// What Arena does: a team round starts with both teams locked in their spawn enclosures (Equator's respawn booths
    /// behind sliding doors, Bay5's warehouse and depot gates, AirPit's bus-door row); Arena's gate script opens them
    /// when the preparation time is over. What the ported scene lacks: that script (an unreadable MonoBehaviour), so
    /// <see cref="BoothDoorRepairs"/> deactivates every gate part once at raid start and the enclosures are always open.
    ///
    /// What this does: a per-raid gate state that a mode plugin (the user's ArenaMode) can drive. Every gate part the
    /// raid-start opening would switch off is registered here first, with its closed pose. <see cref="Hold"/> keeps the
    /// parts closed (active) instead of letting the opening remove them, or re-closes them when the opening already ran,
    /// and seals each closed part for bots with a carving <c>NavMeshObstacle</c> box over its movement collider (the
    /// ported NavMesh is baked without the gates, so a closed leaf alone is a physical wall that bot path planning does
    /// not see: SAIN-driven bots would press against it and vanilla-mover bots would clip through). <see cref="Release"/>
    /// opens the gates: Equator's sliding leaves slide into their frame pockets over <c>SpawnGateSlideSeconds</c>, every
    /// part then switches off exactly as the raid-start opening would have, and the carvers go with them, so the NavMesh
    /// is whole again and bots re-plan through their own layers. The carvers are created only once the ported NavMesh is
    /// installed (<see cref="NavMeshInstaller.Installed"/>) and a NavMesh path probe across every gate logs whether the
    /// carve took (Debug when sealed, Warning when a gate contradicts the expectation). A hold that nobody releases ends
    /// after <c>SpawnGateMaxHoldSeconds</c> (counted from the hold, or from the raid timer for a hold made before it) with
    /// a Warning, so a mode bug cannot lock the player in. Everything resets on the owned Scripts scene's unload.
    ///
    /// Contract for a mode plugin (reflection, like ArenaMapPort's own ArenaModeMapPositionIntegration does the other
    /// way round): type <c>ArenaMapPort.Client.SpawnGates</c>, all members on the Unity main thread.
    /// <c>public static bool Hold(string reason)</c>: in the menu (applies to the next owned raid; call Release on every
    /// cancel path so a stale request cannot reach a later raid), before the raid-start opening, or at any time after it
    /// (the parts come back in their closed pose, an opening in progress is cancelled, carvers are rebuilt). Returns false
    /// only when the feature is switched off. <c>public static void Release(string reason)</c>: opens the gates;
    /// <see cref="IsHeld"/> turns false at once while the slide and the switch-off take <c>SpawnGateSlideSeconds</c>.
    /// <c>public static bool IsHeld</c>, <c>public static int ClosedParts</c> (registered parts of this raid; 0 means
    /// the map has no gates or the opening has not run yet). A caller that checks NavMesh connectivity out of a booth
    /// must expect a Partial path while the carvers stand. Developer keys: <c>Spawns/SpawnGateHold</c> plus
    /// <c>SpawnGateHoldSeconds</c> hold the gates without a caller (host testing), <c>SpawnGateCarve</c>,
    /// <c>SpawnGateSlideSeconds</c>, <c>SpawnGateMaxHoldSeconds</c>.
    ///
    /// Host test: docs/progress/spawn-gates.md (GPU-2, 2026-10-17).
    /// </summary>
    public static class SpawnGates
    {
        const string Tag = "[ArenaMapPort][SpawnGates]";
        const float ProbeFirstDelay = 0.5f, ProbeSecondDelay = 2.5f, PollSeconds = 0.25f;
        const int LayerDefault = 0, LayerLowPoly = 18;

        sealed class Runner : MonoBehaviour { }

        struct Pose
        {
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
        }

        static ConfigEntry<bool> _enabled, _testHold, _carve;
        static ConfigEntry<float> _testHoldSeconds, _slideSeconds, _maxHoldSeconds;
        static bool _installed;

        // Per-raid state. _requested survives from a menu-time Hold() into the next owned raid and is cleared by that
        // raid's Scripts-scene unload (stateless resolution of the scene name, see ArenaMapRaid's own doc comment).
        static bool _requested, _released, _navMeshReady, _opening;
        static readonly List<Transform> _parts = new List<Transform>();
        static readonly Dictionary<Transform, Pose> _closedPose = new Dictionary<Transform, Pose>();
        // World bounds of each part's movement colliders, taken while the part is active (an inactive part has none);
        // the probe after a release reads them.
        static readonly Dictionary<Transform, Bounds> _bounds = new Dictionary<Transform, Bounds>();
        // Baseline per part, probed before the first carve: false = the doorway is disconnected on the map's own NavMesh.
        static readonly Dictionary<Transform, bool> _baseline = new Dictionary<Transform, bool>();
        static readonly List<GameObject> _carvers = new List<GameObject>();
        static GameObject _host;
        static Runner _runner;
        static Coroutine _openCoroutine;
        static float _startedAt = -1f, _heldSince = -1f;

        /// <summary>True while the gates of the current (or next) owned raid are to stay closed and have not been
        /// released yet.</summary>
        public static bool IsHeld => _installed && !_released &&
                                     SpawnGateModel.ShouldHold(_enabled.Value, _requested, _testHold.Value);

        /// <summary>Number of gate parts registered in this raid (0 outside an owned raid, before the raid-start
        /// opening ran, or on a map without gates).</summary>
        public static int ClosedParts => _parts.Count;

        /// <summary>Keep the spawn gates closed until <see cref="Release"/> (or the safety limit); after the raid-start
        /// opening, re-close them. Returns false when the feature is switched off, in which case the gates open at raid
        /// start as usual.</summary>
        public static bool Hold(string reason)
        {
            if (!_installed || !_enabled.Value)
            {
                Plugin.Log.LogDebug($"{Tag} hold requested ({reason ?? "no reason"}) but the feature is off; gates open as usual");
                return false;
            }
            _requested = true;
            _released = false;
            _heldSince = Time.time;
            bool cancelledOpening = false;
            if (_opening && _runner != null && _openCoroutine != null)
            {
                _runner.StopCoroutine(_openCoroutine);
                _openCoroutine = null;
                _opening = false;
                cancelledOpening = true;
            }
            int reclosed = 0;
            foreach (var part in _parts)
            {
                if (part == null) continue;
                if (_closedPose.TryGetValue(part, out var pose))
                {
                    part.localPosition = pose.LocalPosition;
                    part.localRotation = pose.LocalRotation;
                }
                if (!part.gameObject.activeSelf)
                {
                    part.gameObject.SetActive(true);
                    reclosed++;
                }
            }
            Plugin.Log.LogDebug($"{Tag} hold requested ({reason ?? "no reason"}): {_parts.Count} gate part(s) registered, " +
                                $"{reclosed} re-closed{(cancelledOpening ? ", an opening in progress cancelled" : "")}, " +
                                $"navmesh {(_navMeshReady ? "installed" : "not installed yet")}");
            if (_parts.Count > 0)
            {
                EnsureRunner();
                if (_navMeshReady) CreateCarvers("hold after navmesh install");
            }
            return true;
        }

        /// <summary>Open the gates now: slide Equator's leaves, switch every part off, remove the carvers.</summary>
        public static void Release(string reason)
        {
            if (!_installed) return;
            bool held = IsHeld;
            _released = true;
            _requested = false;
            if (!held || _parts.Count == 0)
            {
                Plugin.Log.LogDebug($"{Tag} release ({reason ?? "no reason"}): nothing held");
                return;
            }
            EnsureRunner();
            if (!_opening)
            {
                _opening = true;
                _openCoroutine = _runner.StartCoroutine(Open(reason ?? "no reason"));
            }
        }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            var s = Plugin.Instance.Settings;
            _enabled = s.Bind("Spawns", "SpawnGates", true,
                "Lets a mode plugin (ArenaMode) keep the spawn enclosures closed at raid start and open them later " +
                "(SpawnGates.Hold / Release). Off: the gates always open at raid start. Next raid.");
            _carve = s.Bind("Spawns", "SpawnGateCarve", true,
                "While the gates are held closed, cut the NavMesh at every closed gate part with a carving obstacle, so bots " +
                "inside stay inside instead of pressing against or clipping through a leaf the bake does not know. Next hold.");
            _slideSeconds = s.Bind("Spawns", "SpawnGateSlideSeconds", 1.0f, new ConfigDescription(
                "How long Equator's sliding leaves take to slide into their frame pockets when the gates release; 0 opens " +
                "instantly. Other gates switch off without a motion. Next release.", new AcceptableValueRange<float>(0f, 5f)));
            _maxHoldSeconds = s.Bind("Spawns", "SpawnGateMaxHoldSeconds", 300f, new ConfigDescription(
                "Safety limit: a hold nobody releases opens the gates this many seconds after the hold (or after the raid " +
                "timer starts, for a hold made before it), with a warning. Live.", new AcceptableValueRange<float>(10f, 3600f)));
            _testHold = s.Bind("Spawns", "SpawnGateHold", false,
                "Developer test: hold the gates closed at raid start without a mode plugin asking for it, and release " +
                "them SpawnGateHoldSeconds after the raid timer starts. Next raid.");
            _testHoldSeconds = s.Bind("Spawns", "SpawnGateHoldSeconds", 30f, new ConfigDescription(
                "Developer test: seconds after the raid timer starts until the test hold releases the gates. Live.",
                new AcceptableValueRange<float>(1f, 3600f)));
            NavMeshInstaller.Installed += OnNavMeshInstalled;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        /// <summary>BoothDoorRepairs offers every gate part it would switch off, on both passes of the raid-start
        /// opening (idempotent). The part is registered with its closed pose either way; true means it stays closed now,
        /// false means the caller switches it off as before (a later Hold can still re-close it).</summary>
        internal static bool Offer(Transform part)
        {
            if (part == null) return false;
            if (!_parts.Contains(part))
            {
                _parts.Add(part);
                _closedPose[part] = new Pose { LocalPosition = part.localPosition, LocalRotation = part.localRotation };
            }
            if (!IsHeld) return false;
            EnsureRunner();
            return true;
        }

        /// <summary>One line for BoothDoorRepairs' spawn-openings log.</summary>
        internal static string Describe() =>
            IsHeld ? $"held closed ({_parts.Count} part(s) registered; " +
                     $"{(_requested ? "requested by a mode plugin" : "developer test hold " + _testHoldSeconds.Value.ToString("0") + " s")})"
                   : "not held";

        static void OnNavMeshInstalled()
        {
            _navMeshReady = true;
            if (IsHeld && _parts.Count > 0) CreateCarvers("navmesh installed");
        }

        static void EnsureRunner()
        {
            if (_host != null) return;
            _host = new GameObject("ArenaMapPort SpawnGates");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _runner = _host.AddComponent<Runner>();
            _runner.StartCoroutine(Watch());
        }

        // Timed release: the developer test hold after SpawnGateHoldSeconds, every hold after SpawnGateMaxHoldSeconds.
        // Both count from the raid timer's start, the safety limit from the hold itself when that came later.
        static IEnumerator Watch()
        {
            var wait = new WaitForSeconds(PollSeconds);
            while (true)
            {
                yield return wait;
                if (!IsHeld || _parts.Count == 0) continue;
                if (_startedAt < 0f)
                {
                    if (Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started)
                        _startedAt = Time.time;
                    continue;
                }
                float sinceStart = Time.time - _startedAt;
                float sinceHold = Time.time - Mathf.Max(_startedAt, _heldSince);
                if (!_requested && _testHold.Value && SpawnGateModel.AutoReleaseDue(sinceStart, _testHoldSeconds.Value))
                {
                    Release($"developer test hold, {sinceStart:F1} s after the raid timer started");
                }
                else if (SpawnGateModel.AutoReleaseDue(sinceHold, _maxHoldSeconds.Value))
                {
                    Plugin.Log.LogWarning($"{Tag} hold not released {sinceHold:F0} s after it began; opening the gates " +
                                          $"(safety limit SpawnGateMaxHoldSeconds {_maxHoldSeconds.Value:F0})");
                    Release("safety limit");
                }
            }
        }

        static void CreateCarvers(string why)
        {
            if (_carvers.Count > 0 || !_carve.Value) return;
            int colliders = 0;
            // Pass 1: the movement colliders' world bounds (only an active part has them) and, before anything is cut,
            // the baseline: is the doorway connected on the map's own NavMesh at all? Equator's door (3) leads into a
            // compartment that Arena fences off, so its probe is Partial regardless of the gate; such doors are left out
            // of the sealed/open verdicts instead of raising false warnings.
            var movement = new List<(Transform part, Collider collider)>();
            foreach (var part in _parts)
            {
                if (part == null) continue;
                foreach (var collider in part.GetComponentsInChildren<Collider>(true))
                {
                    int layer = collider.gameObject.layer;
                    if (collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                        (layer != LayerDefault && layer != LayerLowPoly)) continue;
                    var b = collider.bounds;
                    if (_bounds.TryGetValue(part, out var known)) { known.Encapsulate(b); _bounds[part] = known; }
                    else _bounds[part] = b;
                    movement.Add((part, collider));
                }
            }
            if (_baseline.Count == 0)
            {
                foreach (var pair in _bounds)
                {
                    _baseline[pair.Key] = ProbePath(pair.Value, out _) == NavMeshPathStatus.PathComplete;
                }
            }
            // Pass 2: one carver box per movement collider.
            foreach (var (part, collider) in movement)
            {
                {
                    var b = collider.bounds;
                    SpawnGateModel.CarverBox(b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z,
                        out float cx, out float cy, out float cz, out float sx, out float sy, out float sz);
                    var go = new GameObject("Carver " + part.name);
                    go.transform.SetParent(_host.transform, false);
                    go.transform.position = new Vector3(cx, cy, cz);
                    var obstacle = go.AddComponent<NavMeshObstacle>();
                    obstacle.shape = NavMeshObstacleShape.Box;
                    obstacle.center = Vector3.zero;
                    obstacle.size = new Vector3(sx, sy, sz);
                    obstacle.carveOnlyStationary = true;
                    obstacle.carvingTimeToStationary = 0f;
                    obstacle.carving = true;
                    _carvers.Add(go);
                    colliders++;
                }
            }
            int disconnected = 0;
            foreach (var pair in _baseline) if (!pair.Value) disconnected++;
            Plugin.Log.LogDebug($"{Tag} {why}: {_carvers.Count} carver(s) over {colliders} movement collider(s) of {_parts.Count} gate part(s)" +
                                (disconnected > 0 ? $"; {disconnected} doorway(s) already disconnected on the map's own navmesh (not judged)" : ""));
            if (_carvers.Count == 0)
            {
                Plugin.Log.LogWarning($"{Tag} no movement collider under the held gate parts; bots are not sealed in");
                return;
            }
            _runner.StartCoroutine(Probe(expectSealed: true));
        }

        static void DestroyCarvers()
        {
            foreach (var go in _carvers)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            _carvers.Clear();
        }

        // NavMesh path across every gate, 0.5 s after the carvers changed and once more at 2.5 s when the first probe
        // disagrees with the expectation. The verdict is the host evidence that carving works on the ported NavMeshData.
        static IEnumerator Probe(bool expectSealed)
        {
            yield return new WaitForSeconds(ProbeFirstDelay);
            string report = ProbeOnce(expectSealed, out bool contradicted, out int probed, out int inconclusive);
            if (contradicted)
            {
                yield return new WaitForSeconds(ProbeSecondDelay - ProbeFirstDelay);
                report = ProbeOnce(expectSealed, out contradicted, out probed, out inconclusive);
            }
            string state = expectSealed ? "sealed" : "open";
            if (contradicted)
            {
                Plugin.Log.LogWarning($"{Tag} navmesh probe: a gate is NOT {state} " +
                                      $"({(expectSealed ? "bots can path through a closed gate" : "bots still see it closed")}){report}");
            }
            else if (probed == 0)
            {
                Plugin.Log.LogDebug($"{Tag} navmesh probe: no gate could be probed ({inconclusive} inconclusive){report}");
            }
            else
            {
                Plugin.Log.LogDebug($"{Tag} navmesh probe: every probed gate {state} ({probed} probed" +
                                    $"{(inconclusive > 0 ? $", {inconclusive} inconclusive" : "")}){report}");
            }
        }

        static string ProbeOnce(bool expectSealed, out bool contradicted, out int probed, out int inconclusive)
        {
            var sb = new StringBuilder();
            contradicted = false;
            probed = 0;
            inconclusive = 0;
            int skipped = 0, disconnected = 0;
            foreach (var part in _parts)
            {
                if (part == null) continue;
                // Parts without a movement collider (a gate's LOD meshes, ballistic twins) have no doorway to probe.
                if (!_bounds.TryGetValue(part, out var bounds)) { skipped++; continue; }
                if (_baseline.TryGetValue(part, out bool connected) && !connected) { disconnected++; continue; }
                var status = ProbePath(bounds, out string detail);
                if (status == null)
                {
                    inconclusive++;
                    sb.Append($"\n  '{part.name}' at {bounds.center:F1}: {detail}");
                    continue;
                }
                bool sealedNow = SpawnGateModel.Sealed((int)status.Value);
                probed++;
                if (sealedNow != expectSealed) contradicted = true;
                sb.Append($"\n  '{part.name}' at {bounds.center:F1}: {detail}");
            }
            if (disconnected > 0) sb.Append($"\n  {disconnected} doorway(s) disconnected on the map's own navmesh not judged");
            if (skipped > 0) sb.Append($"\n  {skipped} part(s) without a movement collider not probed");
            return sb.ToString();
        }

        // One NavMesh path across a gate's thin axis; null when a probe point has no NavMesh within reach.
        static NavMeshPathStatus? ProbePath(Bounds bounds, out string detail)
        {
            var axis = SpawnGateModel.ThinAxis(bounds.size.x, bounds.size.z) == 0 ? Vector3.right : Vector3.forward;
            var a = bounds.center - axis * SpawnGateModel.ProbeReach;
            var b = bounds.center + axis * SpawnGateModel.ProbeReach;
            if (!NavMesh.SamplePosition(a, out var ha, SpawnGateModel.ProbeSampleRadius, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(b, out var hb, SpawnGateModel.ProbeSampleRadius, NavMesh.AllAreas))
            {
                detail = $"no navmesh within {SpawnGateModel.ProbeSampleRadius:F1} m of a probe point";
                return null;
            }
            var path = new NavMeshPath();
            NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path);
            detail = $"{ha.position:F1} -> {hb.position:F1} = {path.status}" +
                     (path.status == NavMeshPathStatus.PathComplete ? $", {path.corners.Length} corners" : "");
            return path.status;
        }

        static IEnumerator Open(string reason)
        {
            float seconds = Mathf.Max(0f, _slideSeconds.Value);
            var sliders = new List<(Transform part, Vector3 from, Vector3 to)>();
            foreach (var part in _parts)
            {
                if (part == null || !part.gameObject.activeSelf) continue;
                int dir = SpawnGateModel.SlideDirection(part.name);
                if (dir != 0 && seconds > 0f)
                {
                    var from = part.localPosition;
                    sliders.Add((part, from, from + new Vector3(dir * SpawnGateModel.SlideDistance, 0f, 0f)));
                }
            }
            float t0 = Time.time;
            while (sliders.Count > 0 && Time.time - t0 < seconds)
            {
                float k = Mathf.Clamp01((Time.time - t0) / seconds);
                foreach (var s in sliders)
                {
                    if (s.part != null) s.part.localPosition = Vector3.Lerp(s.from, s.to, k);
                }
                yield return null;
            }
            int off = 0;
            foreach (var part in _parts)
            {
                if (part != null && part.gameObject.activeSelf)
                {
                    part.gameObject.SetActive(false);
                    off++;
                }
            }
            int carvers = _carvers.Count;
            DestroyCarvers();
            _opening = false;
            _openCoroutine = null;
            Plugin.Log.LogDebug($"{Tag} released ({reason}): {sliders.Count} leaf(s) slid {SpawnGateModel.SlideDistance:F2} m in " +
                                $"{seconds:F1} s, {off} gate part(s) switched off, {carvers} carver(s) removed");
            if (carvers > 0 && _runner != null) _runner.StartCoroutine(Probe(expectSealed: false));
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null) return;
            DestroyCarvers();
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
            _runner = null;
            _openCoroutine = null;
            _parts.Clear();
            _closedPose.Clear();
            _bounds.Clear();
            _baseline.Clear();
            _requested = false;
            _released = false;
            _navMeshReady = false;
            _opening = false;
            _startedAt = -1f;
            _heldSince = -1f;
        }
    }
}
