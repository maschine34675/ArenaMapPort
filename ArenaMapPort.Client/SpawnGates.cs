using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using ArenaMapPort.Shared;
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
    /// opens the gates: a booth leaf without an Arena pose slides into its frame pocket over <c>SpawnGateSlideSeconds</c> (none
    /// on the shipped maps since 2026-10-23), every part without a pose then switches off exactly as the raid-start opening
    /// would have (the parts WITH a pose drive to Arena's open pose, see below), and the carvers go with them, so the NavMesh
    /// is whole again and bots re-plan through their own layers. The carvers are created only once the ported NavMesh is
    /// installed (<see cref="NavMeshInstaller.Installed"/>) and a NavMesh path probe across every gate logs whether the
    /// carve took (Debug when sealed, Warning when a gate contradicts the expectation). A hold that nobody releases ends
    /// after <c>SpawnGateMaxHoldSeconds</c> (counted from the hold, or from the raid timer for a hold made before it) with
    /// a Warning, so a mode bug cannot lock the player in. Everything resets on the owned Scripts scene's unload.
    ///
    /// Held gates (descriptor <c>HeldGates</c>, 2026-10-17; AirPit's passport control, the gate of its original north
    /// team box): their parts are registered through <see cref="OfferHeld"/> and stay closed at raid start in EVERY
    /// mode, get their carvers as soon as the NavMesh is installed (no hold needed), and open only with a Release that
    /// follows a hold, so a plain raid keeps the pocket behind them closed for players and bots alike (the 2026-10-12
    /// north-limit decision, now without cutting the bake). <see cref="IsHeld"/> does not reflect them;
    /// <see cref="ClosedParts"/> counts them. Which opened barriers a hold may close at all is the descriptor's
    /// <c>StartGates</c> list (AirPit: only the bus doors; shop shells, portal fences and blockout panels stay open).
    ///
    /// Parked parts (descriptor <c>DoorPoses</c>, 2026-10-22, docs/progress/door-poses.md; Bowl): Arena does not hide the
    /// BTR, the ambulance, the army gate's leaves or the respawn gate's leaf at the opening, it animates them to an END
    /// pose. A part with poses is therefore never switched off: <see cref="NotePoses"/> resolves its nodes and measures its
    /// closed colliders (BoothDoorRepairs, before anything moves), <see cref="Park"/> snaps the nodes to the open pose at
    /// raid start, <see cref="Hold"/> puts them back in the closed pose (the part stays active, the hold carvers seal it as
    /// for any other part) and <see cref="Release"/> DRIVES them there over the clip's seconds
    /// (<c>SpawnGateDrive</c>; false snaps), each part finishing on its own clip: its hold carvers go, its park carvers
    /// come. A park carver is a carving box over a collider that Arena MOVED (a collider outside every pose node, such as
    /// the army gate's frame, is left alone: its bounds span the doorway the leaves opened) and stays for the raid, so bots
    /// cannot path through a body that now stands in the way (the BTR's nose overlaps the port's mesh by 0.6 m). A node
    /// whose pose does not match the scene, or is not found, logs one Warning and the whole part falls back to the
    /// switch-off.
    ///
    /// DotAnimator poses (descriptor <c>DoorPoses</c> of source dotanimator / borrowed, 2026-10-23; Equator's ten booth leaves,
    /// Bay5's three roller gates and both depot gates, AirPit's passport leaves, lamps and front bus doors) run through the same
    /// machine with four additions. A pose has its own ease
    /// (smoothstep, linear, DOTween InOutQuint or a stored Hermite curve, <see cref="DoorPoseModel.EaseOf"/>) and turns its node
    /// by a quaternion slerp instead of a component-wise euler lerp; a pose row with part "" moves the barrier ROOT (Bay5's
    /// rollers), which is then the registered part; every authored duration is multiplied by <c>SpawnGateDriveScale</c> (default
    /// 0.5, Bowl included); and a park carver is made only for a moved collider that occupies the walking band
    /// (<see cref="DoorPoseModel.OccupiesWalkingBand"/>), so a roller slab lifted 3.09 m into the lintel carves nothing.
    ///
    /// AirPit (2026-10-24, docs/progress/door-poses.md "Runtime 3") adds three more. (1) A part NAME may repeat among a
    /// barrier's children (the 16 passport leaves reuse six names): <see cref="RowsForPart"/> / <see cref="NotePoses"/> take, for
    /// every node of a part, the one row whose closed position is the node's own local position
    /// (<see cref="DoorPoseModel.SelectRowsForNode"/>, 0.05 m); a part none of whose rows matches logs one Warning and falls back
    /// to the switch-off like any part with poses that do not fit. (2) A SWITCH row (<see cref="ArenaDoorPose.IsSwitch"/>: Arena's
    /// SetActive element, the passport lamps RED / GREEN flipping RedFog off and GreenFog on) sets a node's active state instead of
    /// moving it: closed state while the part is closed (<see cref="NotePoses"/> checks that the scene agrees, else the part falls
    /// back), open state when the part is parked or its drive ENDS, nothing in between
    /// (<see cref="DoorPoseModel.SwitchStateAt"/>); it moves no collider, so it is never carved. A part with only switches takes as
    /// long as the longest drive of the other parts under its barrier root (<see cref="DoorPoseModel.SwitchPartDriveSeconds"/>), so the
    /// lamp flips when the last leaf stands open. (3) A held gate's parts with rows open exactly as the start gates do, on a Release
    /// that follows a hold (<see cref="OfferHeld"/> registers and keeps them closed, the hold's release drives them); without a hold
    /// the held gate stays closed as before.
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
    /// <c>SpawnGateSlideSeconds</c>, <c>SpawnGateDrive</c>, <c>SpawnGateDriveScale</c>, <c>SpawnGateMaxHoldSeconds</c>.
    ///
    /// Host test: docs/progress/spawn-gates.md (GPU-2, 2026-10-17).
    /// </summary>
    public static class SpawnGates
    {
        const string Tag = "[SpawnGates]";
        const float ProbeFirstDelay = 0.5f, ProbeSecondDelay = 2.5f, PollSeconds = 0.25f;
        const int LayerDefault = 0, LayerLowPoly = 18;

        sealed class Runner : MonoBehaviour { }

        struct Pose
        {
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
        }

        // One transform Arena's clip moves (a pose of the descriptor, resolved below the part) and the poses it moves between.
        sealed class PoseNode
        {
            internal Transform Node;
            internal float[] ClosedPosition, ClosedEuler, OpenPosition, OpenEuler;
            internal string Ease;               // ArenaDoorPose.Ease*: smoothstep (Bowl), linear, inOutQuint, curve
            internal float[] Curve;             // the Hermite keys of a "curve" ease, null otherwise
            internal bool Slerp;                // dotanimator / borrowed: quaternion slerp between the two rotations below
            internal bool WriteRotation;        // false: the pose only slides the node, its scene rotation stays untouched
            internal Quaternion ClosedRotation, OpenRotation;
            internal bool Switch;               // a SetActive element (AirPit's lamps): sets the node's active state, moves nothing
            internal bool ClosedActive, OpenActive; // the node's active state while closed (the scene's) and once open
        }

        // A gate part with Arena's animated end pose (descriptor DoorPoses): it is never switched off. Parked = its nodes
        // stand at the open pose, Driving = a release is moving them there, neither = closed. Carved = the park carvers were
        // attempted for this parking (Carvers may still be empty when no collider qualified).
        sealed class PosedPart
        {
            internal Transform Part;
            internal PoseNode[] Nodes;
            internal string[] NodePaths;
            internal string Clip;
            internal float Seconds;             // Arena's own duration (the longest of the nodes), unscaled
            internal float DriveSeconds;        // Seconds * SpawnGateDriveScale, fixed when a release starts to drive the part
            internal int Switches;              // how many of the nodes are switches (SetActive), the rest moves
            internal bool SwitchOnly;           // every node is a switch: nothing to carve, finishes with the barrier's last drive
            internal float FloorY;
            internal bool HasClosedBounds;
            internal Bounds ClosedBounds;
            internal bool Parked, Driving, Carved;
            internal readonly List<GameObject> Carvers = new List<GameObject>();
            internal readonly List<Bounds> Boxes = new List<Bounds>();
        }

        static ConfigEntry<bool> _enabled, _testHold, _carve, _drive;
        static ConfigEntry<float> _testHoldSeconds, _slideSeconds, _maxHoldSeconds, _driveScale;
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
        // The hold carvers of each part, so a part that finishes its drive early loses only its own.
        static readonly Dictionary<Transform, List<GameObject>> _carversOf = new Dictionary<Transform, List<GameObject>>();
        // Gate parts with Arena's end pose (descriptor DoorPoses), by part; parts whose pose data failed to apply are
        // remembered in _poseFallback (one Warning each) and switch off like any other part.
        static readonly Dictionary<Transform, PosedPart> _posed = new Dictionary<Transform, PosedPart>();
        static readonly HashSet<Transform> _poseFallback = new HashSet<Transform>();
        // Held gates (descriptor HeldGates): closed at raid start in every mode, carved from the NavMesh install on,
        // opened only by a Release that follows a hold. _carved remembers the parts that already have carvers, so a
        // later Hold adds the start gates' carvers without touching the held gate's.
        static readonly HashSet<Transform> _heldGateParts = new HashSet<Transform>();
        static readonly HashSet<Transform> _carved = new HashSet<Transform>();
        static bool _heldGatesOpened;
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

        /// <summary>True while a held gate (descriptor HeldGates) stands closed: registered held-gate parts that no
        /// Release has opened yet. Independent of <see cref="IsHeld"/>, which reflects only a hold.</summary>
        internal static bool HeldGatesClosed => _installed && _heldGateParts.Count > 0 &&
                                                SpawnGateModel.HeldGateStaysClosed(_enabled.Value, _carve.Value, _heldGatesOpened);

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
            _heldGatesOpened = false;
            bool cancelledOpening = false;
            if (_opening && _runner != null && _openCoroutine != null)
            {
                _runner.StopCoroutine(_openCoroutine);
                _openCoroutine = null;
                _opening = false;
                cancelledOpening = true;
            }
            int reclosed = 0, unparked = 0;
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
                // A parked or driving part goes back to its closed pose and loses its park carvers; the hold carvers below
                // seal it like any other closed part.
                if (_posed.TryGetValue(part, out var posed) && (posed.Parked || posed.Driving))
                {
                    Reclose(posed);
                    unparked++;
                }
            }
            Plugin.Log.LogDebug($"{Tag} hold requested ({reason ?? "no reason"}): {_parts.Count} gate part(s) registered, " +
                                $"{reclosed} re-closed{(unparked > 0 ? $", {unparked} parked part(s) back in the closed pose" : "")}" +
                                $"{(cancelledOpening ? ", an opening in progress cancelled" : "")}, " +
                                $"navmesh {(_navMeshReady ? "installed" : "not installed yet")}");
            if (_parts.Count > 0)
            {
                EnsureRunner();
                if (_navMeshReady) CreateCarvers("hold after navmesh install");
            }
            return true;
        }

        /// <summary>Open the gates now: slide a booth leaf that has no Arena pose, switch every part without a pose off, remove
        /// the carvers; a part with Arena's end pose (descriptor DoorPoses) drives there over Arena's duration times
        /// SpawnGateDriveScale instead and stays. A held gate (descriptor HeldGates)
        /// opens with this release only when a hold was outstanding; without one it stays closed (Hold followed by
        /// Release opens it in any raid).</summary>
        public static void Release(string reason)
        {
            if (!_installed) return;
            bool held = IsHeld;
            _released = true;
            _requested = false;
            if (!held || _parts.Count == 0)
            {
                Plugin.Log.LogDebug($"{Tag} release ({reason ?? "no reason"}): nothing held" +
                                    (HeldGatesClosed ? $"; {_heldGateParts.Count} held-gate part(s) stay closed (a held gate opens only with a hold)" : ""));
                return;
            }
            _heldGatesOpened = true;
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
                "(SpawnGates.Hold / Release); a map's held gates (AirPit's passport control) stay closed until that release " +
                "in every mode. Off: every gate opens at raid start, the held gates too. Next raid.");
            _carve = s.Bind("Spawns", "SpawnGateCarve", true,
                "While the gates are held closed, cut the NavMesh at every closed gate part with a carving obstacle, so bots " +
                "inside stay inside instead of pressing against or clipping through a leaf the bake does not know; also cut " +
                "the NavMesh at every collider Arena moved out of the way and that stands in the walking band (Bowl's parked " +
                "BTR, ambulance, army gate leaves and respawn gate, Equator's slid booth leaves, Bay5's swung depot leaves; a " +
                "roller slab lifted above head height is not cut; AirPit's swung bus doors and passport leaves are). A map's held gates (AirPit's passport control) depend on " +
                "it: off opens them at raid start. Next hold / next raid.");
            _slideSeconds = s.Bind("Spawns", "SpawnGateSlideSeconds", 1.0f, new ConfigDescription(
                "How long a sliding respawn-booth leaf WITHOUT an Arena pose takes to slide into its frame pocket when the " +
                "gates release; 0 opens instantly. (Since 2026-10-23 every Equator booth leaf has Arena's own pose and drives " +
                "over Arena's duration times Spawns/SpawnGateDriveScale instead, so this applies to no leaf of the shipped maps.) " +
                "Other gates without a pose switch off without a motion. Next release.", new AcceptableValueRange<float>(0f, 5f)));
            _drive = s.Bind("Spawns", "SpawnGateDrive", true,
                "Parts with Arena's animated end pose (Bowl's BTR, ambulance, army gate leaves and respawn gate; Equator's " +
                "booth doors; Bay5's roller and depot gates; AirPit's bus doors and passport leaves) drive there over Arena's duration times Spawns/SpawnGateDriveScale " +
                "when the gates release; false snaps them to the open pose at once. Next release.");
            _driveScale = s.Bind("Spawns", "SpawnGateDriveScale", DoorPoseModel.DefaultDriveScale, new ConfigDescription(
                "Multiplies the authored drive durations of every map, Bowl included; 1 = Arena's own timing (Equator's booth " +
                "doors 5 to 7 s, Bay5's rollers 4 s and depot gates 7 s, AirPit's passport leaves 1.7 s and bus doors 3 s, Bowl's vehicles and gates 7.7 to 13.5 s), 0.5 (default) " +
                "= half of it. Spawns/SpawnGateDrive = false still snaps. Next release.",
                new AcceptableValueRange<float>(DoorPoseModel.MinDriveScale, DoorPoseModel.MaxDriveScale)));
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
            Register(part);
            if (!IsHeld) return false;
            EnsureRunner();
            return true;
        }

        /// <summary>A held gate's part (descriptor HeldGates): registered like every other part, but it stays closed in
        /// every mode until a Release that follows a hold. True means keep it active (carvers come with the NavMesh
        /// install); false (feature off, or already released this raid) means the caller switches it off like an
        /// opened barrier.</summary>
        internal static bool OfferHeld(Transform part)
        {
            if (part == null) return false;
            Register(part);
            _heldGateParts.Add(part);
            if (!SpawnGateModel.HeldGateStaysClosed(_enabled.Value, _carve.Value, _heldGatesOpened)) return false;
            EnsureRunner();
            return true;
        }

        /// <summary>Once per raid-start opening, after every held part was offered: carves the held gates at once when
        /// the NavMesh is already installed (otherwise the install does it).</summary>
        internal static void SealHeldGates(string why)
        {
            if (_navMeshReady && HeldGatesClosed) CreateCarvers(why);
        }

        static void Register(Transform part)
        {
            if (_parts.Contains(part)) return;
            _parts.Add(part);
            _closedPose[part] = new Pose { LocalPosition = part.localPosition, LocalRotation = part.localRotation };
            // A part with Arena's end pose is parked before CreateCarvers ever sees it closed: the closed colliders that
            // NotePoses measured are what the probe after a release reads.
            if (_posed.TryGetValue(part, out var posed) && posed.HasClosedBounds && !_bounds.ContainsKey(part))
                _bounds[part] = posed.ClosedBounds;
        }

        // ---- Arena's end poses (descriptor DoorPoses, docs/progress/door-poses.md) -------------------------------------

        /// <summary>True when <see cref="NotePoses"/> resolved poses for the part and it has not fallen back to the switch-off
        /// since: BoothDoorRepairs asks it after NotePoses on a barrier ROOT (a pose row with part "") before it offers or
        /// parks the root, so a root whose data failed to apply is hidden child by child like any barrier without poses.</summary>
        internal static bool HasPoses(Transform part) => part != null && _posed.ContainsKey(part);

        /// <summary>BoothDoorRepairs, for a part whose NAME repeats among its siblings (AirPit's passport leaves: six names for 16
        /// leaves), BEFORE <see cref="NotePoses"/>: of the rows that carry the part's name, the ones that belong to THIS part. For
        /// every node of the part (the part itself, or a path below it) the row whose closed position is the node's own local
        /// position (<see cref="DoorPoseModel.SelectRowsForNode"/>, 0.05 m) is taken; a node with a single row takes it as it is.
        /// An empty result means the part's name has rows but none is its own.</summary>
        internal static ArenaDoorPose[] RowsForPart(Transform part, ArenaDoorPose[] rows)
        {
            if (part == null || rows == null || rows.Length < 2) return rows ?? new ArenaDoorPose[0];
            return DoorPoseModel.SelectRowsForPart(rows, nodePath =>
            {
                var node = string.IsNullOrEmpty(nodePath) ? part : part.Find(nodePath);
                if (node == null) return null; // NotePoses reports the missing node
                var local = node.localPosition;
                return new[] { local.x, local.y, local.z };
            });
        }

        /// <summary>How many of the part's nodes are switches (SetActive elements), 0 for a part without poses or without
        /// switches. BoothDoorRepairs adds it to its opening summary.</summary>
        internal static int SwitchCountOf(Transform part) =>
            part != null && _posed.TryGetValue(part, out var posed) ? posed.Switches : 0;

        /// <summary>BoothDoorRepairs, for a gate part the descriptor has poses for, BEFORE the part is offered and before
        /// anything moves: resolves the animated nodes below the part, checks each against the scene's own closed pose and
        /// measures the closed movement colliders (floor line, probe bounds). Idempotent. A part that cannot be resolved
        /// logs one Warning and counts as pose-less afterwards (<see cref="Park"/> returns false, it switches off). The rows
        /// are narrowed to this part's own first (<see cref="RowsForPart"/>; a no-op for a name that does not repeat), and a
        /// part none of whose rows matches its closed position falls back like any other mismatch. A SWITCH row is checked
        /// against the node's active state instead of its transform: the scene must agree with the row's closed state.</summary>
        internal static void NotePoses(Transform part, ArenaDoorPose[] poses)
        {
            if (part == null || poses == null || poses.Length == 0) return;
            if (_posed.ContainsKey(part) || _poseFallback.Contains(part)) return;
            try
            {
                int candidates = poses.Length;
                poses = RowsForPart(part, poses);
                if (poses.Length == 0)
                {
                    var here = part.localPosition;
                    PoseFallback(part, $"none of the {candidates} row(s) for this name has its closed position (local {here.x:F3}, {here.y:F3}, {here.z:F3})");
                    return;
                }
                var nodes = new PoseNode[poses.Length];
                var paths = new List<string>(poses.Length); // the transform rows only: a switch node moves no collider
                int switches = 0;
                float seconds = 0f;
                for (int i = 0; i < poses.Length; i++)
                {
                    var pose = poses[i];
                    if (!IsTriple(pose.ClosedPosition) || !IsTriple(pose.ClosedEuler) || !IsTriple(pose.OpenPosition) || !IsTriple(pose.OpenEuler))
                    {
                        PoseFallback(part, "a pose of the descriptor is not three floats");
                        return;
                    }
                    if (!DoorPoseModel.EaseIsUsable(pose.Ease, pose.Curve, out string easeProblem))
                    {
                        PoseFallback(part, easeProblem);
                        return;
                    }
                    string path = pose.Node ?? "";
                    var node = path.Length == 0 ? part : part.Find(path);
                    if (node == null)
                    {
                        PoseFallback(part, $"node '{path}' not found below it");
                        return;
                    }
                    if (pose.IsSwitch)
                    {
                        // a SetActive element: the scene has to stand in the row's closed state (the data's identity check for a
                        // switch, it has no transform to compare), and the part itself is never a switch (it would switch itself off)
                        if (path.Length == 0 || !pose.OpenActive.HasValue)
                        {
                            PoseFallback(part, path.Length == 0 ? "a switch row names the part itself" : $"switch node '{path}' has no open state");
                            return;
                        }
                        bool active = node.gameObject.activeSelf;
                        if (active != pose.ClosedActive.Value)
                        {
                            PoseFallback(part, $"switch node '{path}' is {(active ? "active" : "inactive")} in the scene, the data's closed state is " +
                                               $"{(pose.ClosedActive.Value ? "active" : "inactive")}");
                            return;
                        }
                        nodes[i] = new PoseNode
                        {
                            Node = node, Switch = true, ClosedActive = pose.ClosedActive.Value, OpenActive = pose.OpenActive.Value,
                            ClosedPosition = pose.ClosedPosition, ClosedEuler = pose.ClosedEuler,
                            OpenPosition = pose.OpenPosition, OpenEuler = pose.OpenEuler,
                            Ease = pose.Ease, Curve = pose.Curve,
                        };
                        switches++;
                        seconds = Mathf.Max(seconds, pose.Seconds);
                        continue;
                    }
                    float positionError = Vector3.Distance(node.localPosition, ToVector(pose.ClosedPosition));
                    float angleError = Quaternion.Angle(node.localRotation, Quaternion.Euler(ToVector(pose.ClosedEuler)));
                    if (!DoorPoseModel.PoseMatchesScene(positionError, angleError))
                    {
                        PoseFallback(part, $"node '{path}' stands {positionError:F2} m / {angleError:F1} deg off the data's closed pose");
                        return;
                    }
                    bool slerp = DoorPoseModel.UsesSlerp(pose.Source);
                    nodes[i] = new PoseNode
                    {
                        Node = node,
                        ClosedPosition = pose.ClosedPosition, ClosedEuler = pose.ClosedEuler,
                        OpenPosition = pose.OpenPosition, OpenEuler = pose.OpenEuler,
                        Ease = pose.Ease, Curve = pose.Curve, Slerp = slerp,
                        // a clip's raw angles are always written (Bowl, as before); a DotAnimator node that only slides keeps its scene rotation
                        WriteRotation = !slerp || DoorPoseModel.RotationMoves(pose.ClosedEuler, pose.OpenEuler),
                        ClosedRotation = Quaternion.Euler(ToVector(pose.ClosedEuler)),
                        OpenRotation = Quaternion.Euler(ToVector(pose.OpenEuler)),
                    };
                    paths.Add(path);
                    seconds = Mathf.Max(seconds, pose.Seconds);
                }
                var posed = new PosedPart
                {
                    Part = part, Nodes = nodes, NodePaths = paths.ToArray(), Clip = poses[0].Clip, Seconds = seconds,
                    Switches = switches, SwitchOnly = switches == nodes.Length,
                };
                MeasureClosed(posed);
                _posed[part] = posed;
            }
            catch (Exception e)
            {
                PoseFallback(part, e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The part is to open now and Arena animates it: snap its nodes to the open pose and keep it active.
        /// True = parked (the caller must not switch it off); false = no usable poses, the caller switches it off as
        /// before. Idempotent over the two raid-start passes; the park carvers follow with the NavMesh install
        /// (<see cref="SealParked"/>, <see cref="OnNavMeshInstalled"/>).</summary>
        internal static bool Park(Transform part)
        {
            if (part == null || !_posed.TryGetValue(part, out var posed)) return false;
            if (posed.Parked || posed.Driving) return true;
            try
            {
                ApplyPose(posed, 1f);
                posed.Parked = true;
                Physics.SyncTransforms(); // the colliders follow the transforms now, not at the next physics step
                Plugin.Log.LogDebug($"{Tag} park: '{part.name}' snapped to the open pose ({posed.Nodes.Length} node(s)" +
                                    $"{(posed.Switches > 0 ? $", {posed.Switches} switch(es)" : "")}, clip {posed.Clip}); " +
                                    $"park carvers follow {(_navMeshReady ? "now" : "the navmesh install")}");
                return true;
            }
            catch (Exception e)
            {
                _posed.Remove(part);
                PoseFallback(part, e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Once per raid-start opening pass, after every part was offered: carves the parked bodies at once when
        /// the NavMesh is already installed (otherwise the install does it).</summary>
        internal static void SealParked(string why)
        {
            try
            {
                if (_navMeshReady) CreateParkCarvers(why);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} park carvers failed ({e.GetType().Name}: {e.Message}); the parked bodies are not carved");
            }
        }

        static void PoseFallback(Transform part, string why)
        {
            if (!_poseFallback.Add(part)) return;
            Plugin.Log.LogWarning($"{Tag} door pose of '{part.name}' not applied ({why}); the part switches off as before");
        }

        static bool IsTriple(float[] v) => v != null && v.Length == 3;

        static Vector3 ToVector(float[] v) => new Vector3(v[0], v[1], v[2]);

        static bool IsParkedOrDriving(Transform part) =>
            _posed.TryGetValue(part, out var posed) && (posed.Parked || posed.Driving);

        // progress is the elapsed fraction of the drive, 0 = Arena's closed pose, 1 = the END pose of its open animation; each
        // node maps it through its own ease (exact at both ends, so 0 and 1 are the data bit for bit). Positions lerp; a Mecanim
        // clip's euler angles lerp component-wise like Unity's euler clips (the wheels keep their extra turns), a DotAnimator
        // node turns by a quaternion slerp between the two rotations (and keeps its scene rotation when the pose only slides it).
        // A switch node (SetActive element) moves nothing: it takes its closed state at progress 0 and its open state at 1, and
        // keeps its state in between (DoorPoseModel.SwitchStateAt), so a lamp flips when the drive ends.
        static void ApplyPose(PosedPart posed, float progress)
        {
            foreach (var n in posed.Nodes)
            {
                if (n.Node == null) continue;
                float k = DoorPoseModel.EaseOf(n.Ease, n.Curve, progress);
                if (n.Switch)
                {
                    var state = DoorPoseModel.SwitchStateAt(k, n.ClosedActive, n.OpenActive);
                    if (state.HasValue && n.Node.gameObject.activeSelf != state.Value) n.Node.gameObject.SetActive(state.Value);
                    continue;
                }
                DoorPoseModel.Lerp3(n.ClosedPosition, n.OpenPosition, k, out float px, out float py, out float pz);
                n.Node.localPosition = new Vector3(px, py, pz);
                if (!n.WriteRotation) continue;
                if (n.Slerp)
                {
                    n.Node.localRotation = k <= 0f ? n.ClosedRotation : k >= 1f ? n.OpenRotation : Quaternion.Slerp(n.ClosedRotation, n.OpenRotation, k);
                }
                else
                {
                    DoorPoseModel.Lerp3(n.ClosedEuler, n.OpenEuler, k, out float ex, out float ey, out float ez);
                    n.Node.localEulerAngles = new Vector3(ex, ey, ez);
                }
            }
        }

        // Hold: back to the closed pose, park carvers gone (CreateCarvers seals the closed part again).
        static void Reclose(PosedPart posed)
        {
            DestroyParkCarvers(posed);
            posed.Parked = false;
            posed.Driving = false;
            posed.Carved = false;
            ApplyPose(posed, 0f);
        }

        static void DestroyParkCarvers(PosedPart posed)
        {
            foreach (var go in posed.Carvers)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
            posed.Carvers.Clear();
            posed.Boxes.Clear();
        }

        // The movement colliders of a gate part: what a player collides with (layers 0 and 18), enabled and active.
        static bool IsMovementCollider(Collider collider)
        {
            int layer = collider.gameObject.layer;
            return !collider.isTrigger && collider.enabled && collider.gameObject.activeInHierarchy &&
                   (layer == LayerDefault || layer == LayerLowPoly);
        }

        // The path of a transform below a root, "" for the root itself (the form DoorPoseModel.IsMoved reads).
        static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root; p = p.parent) sb.Insert(0, '/').Insert(0, p.name);
            return sb.ToString();
        }

        // The closed movement colliders of a part, taken while it stands at the scene's pose: the lowest point is its floor
        // line (a parked collider must rise above it to count), the union is what the probe after a release crosses.
        static void MeasureClosed(PosedPart posed)
        {
            Physics.SyncTransforms();
            float floor = float.MaxValue;
            foreach (var collider in posed.Part.GetComponentsInChildren<Collider>(true))
            {
                if (!IsMovementCollider(collider)) continue;
                var b = collider.bounds;
                if (posed.HasClosedBounds) posed.ClosedBounds.Encapsulate(b);
                else { posed.ClosedBounds = b; posed.HasClosedBounds = true; }
                floor = Mathf.Min(floor, b.min.y);
            }
            posed.FloorY = posed.HasClosedBounds ? floor : posed.Part.position.y;
        }

        // Park carvers: a carving box over every movement collider that Arena MOVED (DoorPoseModel.IsMoved) and that occupies
        // the walking band (DoorPoseModel.OccupiesWalkingBand: a roller slab lifted into the lintel does not), once the part
        // stands at its open pose and the NavMesh is installed. They stay for the raid. Hold removes them (Reclose), a
        // release creates them when each part finishes its drive.
        static void CreateParkCarvers(string why)
        {
            if (!_navMeshReady || !_carve.Value) return;
            List<PosedPart> todo = null;
            foreach (var posed in _posed.Values)
            {
                if (posed.Parked && !posed.Carved && posed.Part != null)
                {
                    if (todo == null) todo = new List<PosedPart>();
                    todo.Add(posed);
                }
            }
            if (todo == null) return;
            EnsureRunner();
            EnsureBaseline(); // before a park carver changes the mesh: which doorways were disconnected to begin with
            Physics.SyncTransforms();
            foreach (var posed in todo)
            {
                int made = 0, unmoved = 0, low = 0, high = 0;
                posed.Carved = true; // before the loop: a failure midway must not make a later pass add duplicate carvers
                if (posed.SwitchOnly)
                {
                    // AirPit's lamps: no body moved, nothing to carve (the switches were set by Park / the end of the drive)
                    Plugin.Log.LogDebug($"{Tag} parked '{posed.Part.name}': {posed.Switches} switch(es) in the open state, no collider to carve ({why})");
                    continue;
                }
                foreach (var collider in posed.Part.GetComponentsInChildren<Collider>(true))
                {
                    if (!IsMovementCollider(collider)) continue;
                    if (!DoorPoseModel.IsMoved(RelativePath(posed.Part, collider.transform), posed.NodePaths)) { unmoved++; continue; }
                    var b = collider.bounds;
                    if (!DoorPoseModel.ParkedBodyStands(b.max.y, posed.FloorY)) { low++; continue; }
                    if (!DoorPoseModel.OccupiesWalkingBand(b.min.y, b.max.y, posed.FloorY)) { high++; continue; }
                    posed.Carvers.Add(MakeCarver("Park carver " + collider.name, b, out var box));
                    posed.Boxes.Add(box);
                    made++;
                }
                Plugin.Log.LogDebug($"{Tag} parked '{posed.Part.name}': {posed.Nodes.Length} node(s) at the open pose (clip {posed.Clip}), " +
                                    $"{made} park carver(s) ({why})" +
                                    (posed.Switches > 0 ? $"; {posed.Switches} switch(es) in the open state" : "") +
                                    (unmoved > 0 ? $"; {unmoved} collider(s) Arena does not move stay uncarved" : "") +
                                    (low > 0 ? $"; {low} collider(s) lower than {DoorPoseModel.ParkedBodyMinHeight:F1} m above the floor line skipped" : "") +
                                    (high > 0 ? $"; {high} collider(s) above the {DoorPoseModel.WalkingBandHeight:F1} m walking band skipped" : ""));
            }
        }

        // One carving NavMeshObstacle box over a collider's world bounds (SpawnGateModel.CarverBox), parented to the host.
        static GameObject MakeCarver(string name, Bounds b, out Bounds box)
        {
            SpawnGateModel.CarverBox(b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z,
                out float cx, out float cy, out float cz, out float sx, out float sy, out float sz);
            var go = new GameObject(name);
            go.transform.SetParent(_host.transform, false);
            go.transform.position = new Vector3(cx, cy, cz);
            var obstacle = go.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = Vector3.zero;
            obstacle.size = new Vector3(sx, sy, sz);
            obstacle.carveOnlyStationary = true;
            obstacle.carvingTimeToStationary = 0f;
            obstacle.carving = true;
            box = new Bounds(new Vector3(cx, cy, cz), new Vector3(sx, sy, sz));
            return go;
        }

        // Baseline per part, probed before the first carve: false = the doorway is disconnected on the map's own NavMesh.
        static void EnsureBaseline()
        {
            foreach (var pair in _bounds)
            {
                if (!_baseline.ContainsKey(pair.Key))
                    _baseline[pair.Key] = ProbePath(pair.Value, out _) == NavMeshPathStatus.PathComplete;
            }
        }

        // Does the probe segment across a gate's closed bounds run through a parked body (a park carver)?
        static bool ParkedBodyOnProbe(Bounds bounds)
        {
            ProbeSegment(bounds, out var a, out var b);
            foreach (var posed in _posed.Values)
            {
                foreach (var box in posed.Boxes)
                {
                    if (DoorPoseModel.SegmentTouchesBox(a.x, a.z, b.x, b.z, box.min.x, box.min.z, box.max.x, box.max.z)) return true;
                }
            }
            return false;
        }

        /// <summary>One line for BoothDoorRepairs' spawn-openings log, held-gate variant.</summary>
        internal static string DescribeHeldGate() =>
            HeldGatesClosed ? $"closed until Release ({_heldGateParts.Count} held-gate part(s), carved from the navmesh install on)"
                            : "opened at raid start (SpawnGates or SpawnGateCarve off, or already released this raid)";

        /// <summary>One line for BoothDoorRepairs' spawn-openings log.</summary>
        internal static string Describe() =>
            IsHeld ? $"held closed ({_parts.Count} part(s) registered; " +
                     $"{(_requested ? "requested by a mode plugin" : "developer test hold " + _testHoldSeconds.Value.ToString("0") + " s")})"
                   : "not held";

        static void OnNavMeshInstalled()
        {
            _navMeshReady = true;
            if ((IsHeld || HeldGatesClosed) && _parts.Count > 0) CreateCarvers("navmesh installed");
            SealParked("navmesh installed"); // parts parked at raid start (no hold): their bodies now stand in the way
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

        // Incremental: the held gate's carvers come with the NavMesh install, a later Hold adds the start gates' ones.
        static void CreateCarvers(string why)
        {
            if (!_carve.Value) return;
            EnsureRunner();
            Physics.SyncTransforms(); // a part a hold just put back in its closed pose: the bounds read below follow it
            int colliders = 0;
            // Pass 1: the movement colliders' world bounds (only an active part has them) and, before anything is cut,
            // the baseline: is the doorway connected on the map's own NavMesh at all? Equator's door (3) leads into a
            // compartment that Arena fences off, so its probe is Partial regardless of the gate; such doors are left out
            // of the sealed/open verdicts instead of raising false warnings.
            var movement = new List<(Transform part, Collider collider)>();
            foreach (var part in _parts)
            {
                if (part == null || _carved.Contains(part)) continue;
                if (IsParkedOrDriving(part)) continue; // a part at its open pose is carved by CreateParkCarvers
                foreach (var collider in part.GetComponentsInChildren<Collider>(true))
                {
                    if (!IsMovementCollider(collider)) continue;
                    var b = collider.bounds;
                    if (_bounds.TryGetValue(part, out var known)) { known.Encapsulate(b); _bounds[part] = known; }
                    else _bounds[part] = b;
                    movement.Add((part, collider));
                }
            }
            EnsureBaseline();
            // Pass 2: one carver box per movement collider.
            foreach (var (part, collider) in movement)
            {
                var go = MakeCarver("Carver " + part.name, collider.bounds, out _);
                _carvers.Add(go);
                _carved.Add(part);
                if (!_carversOf.TryGetValue(part, out var own)) _carversOf[part] = own = new List<GameObject>();
                own.Add(go);
                colliders++;
            }
            int disconnected = 0;
            foreach (var pair in _baseline) if (!pair.Value) disconnected++;
            Plugin.Log.LogDebug($"{Tag} {why}: {colliders} carver(s) over {colliders} movement collider(s) of {_parts.Count} gate part(s) " +
                                $"({_carvers.Count} carver(s) in place, {_heldGateParts.Count} held-gate part(s))" +
                                (disconnected > 0 ? $"; {disconnected} doorway(s) already disconnected on the map's own navmesh (not judged)" : ""));
            if (colliders == 0)
            {
                if (_carvers.Count == 0)
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
            _carved.Clear();
            _carversOf.Clear();
        }

        // The hold carvers of one part (a part that finished its drive); returns how many were removed.
        static int DestroyHoldCarversOf(Transform part)
        {
            if (part == null || !_carversOf.TryGetValue(part, out var own)) return 0;
            foreach (var go in own)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                _carvers.Remove(go);
            }
            int removed = own.Count;
            _carversOf.Remove(part);
            _carved.Remove(part);
            return removed;
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
            int skipped = 0, disconnected = 0, occupied = 0, parkedOpen = 0;
            // Verdicts per enclosure (SpawnGateModel.EnclosureContradicts): the parts under one barrier root or door
            // assembly. AirPit's bus pocket had an authored-open inner row behind the closed outer row, so three of its
            // six doorways stayed passable while the pocket was sealed; a per-part verdict warned on them (GPU-2 2026-10-17).
            // Since 2026-10-24 that row has no pose rows and is not registered; the verdict stays per enclosure (a doorway
            // that stays passable behind a sealed one lies inside the box).
            var enclosures = new Dictionary<string, (int probed, int sealedDoorways)>();
            foreach (var part in _parts)
            {
                if (part == null) continue;
                // Parts without a movement collider (a gate's LOD meshes, ballistic twins) have no doorway to probe.
                if (!_bounds.TryGetValue(part, out var bounds)) { skipped++; continue; }
                // A part at its open pose is no closed gate (Register keeps the CLOSED bounds of a posed part, so a parked bus
                // assembly has a doorway here): the sealed probe does not judge it, whatever the park carvers did to the path.
                if (!DoorPoseModel.ProbeJudgesPart(expectSealed, IsParkedOrDriving(part))) { parkedOpen++; continue; }
                if (_baseline.TryGetValue(part, out bool connected) && !connected) { disconnected++; continue; }
                // Arena parks the BTR, the ambulance and the swung leaves at the doorway they open; a probe segment that
                // runs through such a body (a park carver) says nothing about the gate, so it is not judged "open".
                if (!expectSealed && ParkedBodyOnProbe(bounds))
                {
                    occupied++;
                    sb.Append($"\n  '{part.name}' at {bounds.center:F1}: the probe segment runs through a parked body, not judged");
                    continue;
                }
                var status = ProbePath(bounds, out string detail);
                if (status == null)
                {
                    inconclusive++;
                    sb.Append($"\n  '{part.name}' at {bounds.center:F1}: {detail}");
                    continue;
                }
                bool sealedNow = SpawnGateModel.Sealed((int)status.Value);
                probed++;
                string enclosure = part.parent != null ? part.parent.name : part.name;
                enclosures.TryGetValue(enclosure, out var tally);
                enclosures[enclosure] = (tally.probed + 1, tally.sealedDoorways + (sealedNow ? 1 : 0));
                sb.Append($"\n  '{part.name}' at {bounds.center:F1}: {detail}");
            }
            foreach (var pair in enclosures)
            {
                if (SpawnGateModel.EnclosureContradicts(expectSealed, pair.Value.probed, pair.Value.sealedDoorways))
                    contradicted = true;
                else if (expectSealed && pair.Value.sealedDoorways < pair.Value.probed)
                    sb.Append($"\n  '{pair.Key}': {pair.Value.probed - pair.Value.sealedDoorways} passable doorway(s) inside the sealed enclosure (authored-open leaves behind the closed row)");
            }
            if (disconnected > 0) sb.Append($"\n  {disconnected} doorway(s) disconnected on the map's own navmesh not judged");
            if (occupied > 0) sb.Append($"\n  {occupied} doorway(s) occupied by a parked body not judged");
            if (parkedOpen > 0) sb.Append($"\n  {parkedOpen} part(s) parked at the open pose, not a closed gate, not judged");
            if (skipped > 0) sb.Append($"\n  {skipped} part(s) without a movement collider not probed");
            return sb.ToString();
        }

        // The two probe points across a gate's thin axis, 1.5 m either side of the centre of its (closed) bounds.
        static void ProbeSegment(Bounds bounds, out Vector3 a, out Vector3 b)
        {
            var axis = SpawnGateModel.ThinAxis(bounds.size.x, bounds.size.z) == 0 ? Vector3.right : Vector3.forward;
            a = bounds.center - axis * SpawnGateModel.ProbeReach;
            b = bounds.center + axis * SpawnGateModel.ProbeReach;
        }

        // One NavMesh path across a gate's thin axis; null when a probe point has no NavMesh within reach.
        static NavMeshPathStatus? ProbePath(Bounds bounds, out string detail)
        {
            ProbeSegment(bounds, out var a, out var b);
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

        // The release: a booth leaf without a pose slides for SpawnGateSlideSeconds, then every part without Arena's end pose
        // switches off exactly as the raid-start opening would have; a part WITH poses drives from the closed to the open pose
        // over Arena's seconds times SpawnGateDriveScale (SpawnGateDrive false: at once) and stays active. One coroutine moves
        // them all, each part finishing on its own duration: its hold carvers go and its park carvers come, so a doorway opens
        // for bots as soon as its own gate has finished. The parts are re-posed by transform every frame, their colliders stay static ones: a
        // scratch Unity 2022.3.43 model (62k static colliders, 19 nodes / 70 colliders, 200 raycasts a frame, EFT's Script
        // simulation mode) measured +0.2 ms a frame, and a kinematic Rigidbody on the node roots measured +0.1 to +0.2 ms
        // MORE, so no body is added (docs/progress/door-poses.md, "Cost of the drive"). The `drive frames` line is the host
        // check of that model.
        static IEnumerator Open(string reason)
        {
            float seconds = Mathf.Max(0f, _slideSeconds.Value);
            bool drive = _drive.Value;
            float scale = _driveScale.Value;
            var sliders = new List<(Transform part, Vector3 from, Vector3 to)>();
            var drivers = new List<PosedPart>();
            foreach (var part in _parts)
            {
                if (part == null || !part.gameObject.activeSelf) continue;
                if (_posed.TryGetValue(part, out var posed))
                {
                    if (!posed.Parked)
                    {
                        posed.Driving = true;
                        posed.DriveSeconds = DoorPoseModel.ScaledSeconds(posed.Seconds, scale);
                        drivers.Add(posed);
                    }
                    continue;
                }
                int dir = SpawnGateModel.SlideDirection(part.name);
                if (dir != 0 && seconds > 0f)
                {
                    var from = part.localPosition;
                    sliders.Add((part, from, from + new Vector3(dir * SpawnGateModel.SlideDistance, 0f, 0f)));
                }
            }
            // A part with only switches (AirPit's lamps RED / GREEN) takes as long as the longest drive of the other parts under its
            // barrier root: the lamp flips when the last leaf stands open, not at the first frame of the release.
            foreach (var posed in drivers)
            {
                if (!posed.SwitchOnly || posed.Part == null) continue;
                var siblings = new List<float>();
                foreach (var other in drivers)
                {
                    if (other != posed && !other.SwitchOnly && other.Part != null && other.Part.parent == posed.Part.parent) siblings.Add(other.DriveSeconds);
                }
                posed.DriveSeconds = DoorPoseModel.SwitchPartDriveSeconds(siblings);
            }
            float t0 = Time.time;
            bool slideDone = false;
            int off = 0, removed = 0, driven = 0, switched = 0, driveFrames = 0;
            float driveEnd = 0f, driveSum = 0f, driveMax = 0f;
            while (true)
            {
                float elapsed = Time.time - t0;
                if (elapsed > 0f && drivers.Count > 0)
                {
                    // The whole frame while a part is still being posed (the first pass of the loop belongs to the frame that
                    // released): the host evidence for what re-posing static colliders every frame costs (see the log below).
                    float frame = Time.unscaledDeltaTime;
                    driveFrames++;
                    driveSum += frame;
                    if (frame > driveMax) driveMax = frame;
                }
                if (!slideDone)
                {
                    if (sliders.Count > 0 && elapsed < seconds)
                    {
                        float k = Mathf.Clamp01(elapsed / seconds);
                        foreach (var s in sliders)
                        {
                            if (s.part != null) s.part.localPosition = Vector3.Lerp(s.from, s.to, k);
                        }
                    }
                    else
                    {
                        foreach (var part in _parts)
                        {
                            if (part != null && part.gameObject.activeSelf && !_posed.ContainsKey(part))
                            {
                                part.gameObject.SetActive(false);
                                removed += DestroyHoldCarversOf(part);
                                off++;
                            }
                        }
                        slideDone = true;
                    }
                }
                bool finished = false;
                for (int i = drivers.Count - 1; i >= 0; i--)
                {
                    var posed = drivers[i];
                    if (posed.Part == null) { drivers.RemoveAt(i); continue; }
                    if (!drive || DoorPoseModel.Finished(elapsed, posed.DriveSeconds))
                    {
                        ApplyPose(posed, 1f);
                        posed.Driving = false;
                        posed.Parked = true;
                        removed += DestroyHoldCarversOf(posed.Part);
                        driven++;
                        switched += posed.Switches;
                        driveEnd = elapsed;
                        drivers.RemoveAt(i);
                        finished = true;
                    }
                    else
                    {
                        ApplyPose(posed, DoorPoseModel.Progress(elapsed, posed.DriveSeconds));
                    }
                }
                if (finished) SealParked("released"); // guarded: a carver failure must not leave the coroutine (and _opening) stuck
                if (slideDone && drivers.Count == 0) break;
                yield return null;
            }
            removed += _carvers.Count; // whatever else stood (a carver of a part that was already off)
            DestroyCarvers();
            _opening = false;
            _openCoroutine = null;
            int parkCarvers = 0;
            foreach (var posed in _posed.Values) parkCarvers += posed.Carvers.Count;
            Plugin.Log.LogDebug($"{Tag} released ({reason}): {sliders.Count} leaf(s) slid {SpawnGateModel.SlideDistance:F2} m in " +
                                $"{seconds:F1} s, {off} gate part(s) switched off, {removed} carver(s) removed" +
                                (driven > 0 ? $", {(drive ? "drove" : "snapped")} {driven} part(s) to the open pose in {driveEnd:F1} s (scale {scale:F2}), " +
                                              $"{parkCarvers} park carver(s) in place" : "") +
                                (switched > 0 ? $", {switched} switch(es) set to the open state" : ""));
            if (driveFrames > 0)
            {
                Plugin.Log.LogDebug($"{Tag} drive frames: {driveFrames} frame(s) in {driveEnd:F1} s, mean {driveSum / driveFrames * 1000f:F1} ms, " +
                                    $"max {driveMax * 1000f:F1} ms (the whole frame, not the poses alone; Physics.autoSyncTransforms {Physics.autoSyncTransforms})");
            }
            if (removed > 0 && _runner != null) _runner.StartCoroutine(Probe(expectSealed: false));
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (ArenaMapRaid.MapForScriptsScene(scene.name) == null) return;
            DestroyCarvers();
            foreach (var posed in _posed.Values) DestroyParkCarvers(posed);
            _posed.Clear();
            _poseFallback.Clear();
            if (_host != null) UnityEngine.Object.Destroy(_host);
            _host = null;
            _runner = null;
            _openCoroutine = null;
            _parts.Clear();
            _closedPose.Clear();
            _bounds.Clear();
            _baseline.Clear();
            _heldGateParts.Clear();
            _carved.Clear();
            _heldGatesOpened = false;
            _requested = false;
            _released = false;
            _navMeshReady = false;
            _opening = false;
            _startedAt = -1f;
            _heldSince = -1f;
        }
    }
}
