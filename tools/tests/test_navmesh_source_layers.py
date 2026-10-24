"""Execute the Editor's source filter without loading Unity or rebuilding bundles."""
import re
from pathlib import Path
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
BAKE = REPO / "UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs"
PATHS = REPO / "UnityProject/Assets/ArenaMapPort/Editor/Common/ArenaPaths.cs"


def extract_function(source, signature):
    start = source.index(signature)
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        if end >= len(source):
            raise ValueError("Unterminated " + signature)
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def filter_source(source, paths_source):
    # PLAN-TWO-MAPS item 4 (2026-09-25): the layer rule (13 Triggers / 29 LevelBorder) lives in
    # NonPhysicalLayerReason and applies to collider sources only; RoundOpenedReason (names, opened
    # barriers, door-pose movers) applies to sources AND to NavMeshModifier/Volume markups. The harness mirrors
    # the collider loop's `NonPhysicalLayerReason(t) ?? RoundOpenedReason(t, openedBarriers, movedSubtrees)`.
    # PLAN-AIRPIT item 8 added the bake-only exclusion, the descriptor layer mask and the explicit-bounds check;
    # all three are pure (no Unity types), so the harness runs the production text.
    # 2026-10-23 (door poses): the barrier resolver (rows of tools/maps/<stem>.doors.json -> moved subtrees), the
    # all-instances finder and the 'at' match run as production text too, on a stand-in hierarchy (Transform with
    # children/Find/position, GameObject, Vector3, Debug below); ArenaDoorRow is compiled from ArenaPaths.cs.
    functions = "\n".join(extract_function(source, sig) for sig in
                          ("static string NonPhysicalLayerReason(", "static string RoundOpenedReason(",
                           "static string BakeOnlyExclusionReason(", "static int SourceLayerMask(",
                           "static bool UsesExplicitBakeBounds(", "static bool DoorRowAppliesTo(",
                           "static string FormatDoorPosition(", "static List<Transform> FindAllInstances(",
                           "static List<Transform> ChildrenNamed(", "static bool ClosedPositionMatches(", "static List<Transform> PartsOfRow(",
                           "static List<Transform> ResolveOpenedBarriers(", "static List<Transform> ResolveHierarchyPaths("))
    arrays = []
    for name in ("ExcludedNamePrefixes", "ExcludedLeafNames"):
        matches = re.findall(r"static readonly string\[\]\s+" + name + r"\s*=\s*\{[^}]*\};", source)
        if len(matches) != 1:
            raise ValueError(f"Expected one {name} declaration")
        arrays.append(matches[0])
    tolerance = re.findall(r"const float DoorAtTolerance\s*=\s*0\.05f;", source)
    if len(tolerance) != 1:
        raise ValueError("Expected one DoorAtTolerance = 0.05f declaration")
    closed_tolerance = re.findall(r"const float DoorClosedPositionTolerance\s*=\s*0\.05f;", source)
    if len(closed_tolerance) != 1:
        raise ValueError("Expected one DoorClosedPositionTolerance = 0.05f declaration")
    row_class = extract_function(paths_source, "public sealed class ArenaDoorRow")
    return "\n".join(arrays) + "\n" + tolerance[0] + "\n" + closed_tolerance[0] + "\n" + row_class + "\n" + functions


HARNESS = r"""
using System;
using System.Collections.Generic;

// Stand-ins for the UnityEngine types the extracted production text touches. Transform keeps its children in creation
// order and finds like Unity does (Find takes the first child of a name per path segment, inactive objects included).
struct Vector3
{
    public float x, y, z;
    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
}

static class Debug
{
    public static List<string> Logs = new List<string>();
    public static List<string> Warnings = new List<string>();
    public static void Log(string message) { Logs.Add(message); }
    public static void LogWarning(string message) { Warnings.Add(message); }
    public static void LogError(string message) { Logs.Add("ERROR " + message); }
}

sealed class GameObject
{
    public int layer;
    public string name;
    public Transform transform;
}
sealed class Transform
{
    public string name;
    public Transform parent;
    public GameObject gameObject = new GameObject();
    public Vector3 position;
    public Vector3 localPosition;
    public List<Transform> children = new List<Transform>();
    public int childCount { get { return children.Count; } }
    public Transform(string name, int layer, Transform parent = null)
    {
        this.name = name; gameObject.layer = layer; gameObject.name = name; gameObject.transform = this; this.parent = parent;
        if (parent != null) parent.children.Add(this);
    }
    public Transform GetChild(int index) { return children[index]; }
    public Transform Find(string path)
    {
        Transform current = this;
        foreach (string segment in path.Split('/'))
        {
            Transform next = null;
            foreach (Transform child in current.children)
                if (child.name == segment) { next = child; break; }
            if (next == null) return null;
            current = next;
        }
        return current;
    }
    public bool IsChildOf(Transform ancestor)
    {
        for (var current = this; current != null; current = current.parent)
            if (current == ancestor) return true;
        return false;
    }
}

// Stand-in for the descriptor the production rule reads (ArenaPaths.Map.keep_level_borders, PLAN-BOWL).
sealed class ArenaMapDescriptorData { public bool keep_level_borders; }
static class ArenaPaths { public static ArenaMapDescriptorData Map = new ArenaMapDescriptorData(); }

static class Program
{
    __PRODUCTION_FILTER__
    static int checkedCases;
    static void Check(string label, Transform owner, bool excluded, List<Transform> barriers = null)
    {
        // collider-source semantics (BakeNavMesh's source loop)
        string reason = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, barriers ?? new List<Transform>(), new List<Transform>());
        if ((reason != null) != excluded)
            throw new Exception(label + ": unexpected exclusion=" + reason);
        checkedCases++;
    }
    static void CheckSource(string label, Transform owner, bool excluded, List<Transform> barriers, List<Transform> exclusions)
    {
        // the exact composition of BakeNavMesh's collider-source loop (a test below pins the production text)
        string reason = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, barriers, new List<Transform>()) ?? BakeOnlyExclusionReason(owner, exclusions);
        if ((reason != null) != excluded)
            throw new Exception(label + ": unexpected exclusion=" + reason);
        checkedCases++;
    }
    static void CheckEqual<T>(string label, T actual, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception(label + ": expected " + expected + " but got " + actual);
        checkedCases++;
    }
    static void CheckTrue(string label, bool condition)
    {
        if (!condition) throw new Exception(label + ": expected true");
        checkedCases++;
    }
    static void CheckThrows(string label, float[] bounds)
    {
        try { UsesExplicitBakeBounds(bounds); }
        catch (InvalidOperationException) { checkedCases++; return; }
        throw new Exception(label + ": expected InvalidOperationException");
    }
    static void CheckMarkup(string label, Transform owner, bool excluded, List<Transform> barriers = null)
    {
        // NavMeshModifier/Volume semantics: round-open rules only, never the layer rule
        string reason = RoundOpenedReason(owner, barriers ?? new List<Transform>(), new List<Transform>());
        if ((reason != null) != excluded)
            throw new Exception(label + ": unexpected exclusion=" + reason);
        checkedCases++;
    }

    // ---- door poses (2026-10-23): rows of tools/maps/<stem>.doors.json decide which subtrees of an opened barrier are dropped ----
    static Transform Make(string name, int layer, Transform parent, float x = 0f, float y = 0f, float z = 0f)
    {
        var t = new Transform(name, layer, parent);
        t.position = new Vector3(x, y, z);
        t.localPosition = t.position;
        return t;
    }
    static ArenaDoorRow Row(string barrier, string part, string node, params float[] at)
    {
        return new ArenaDoorRow { barrier = barrier, part = part, node = node, at = at.Length == 0 ? null : at };
    }
    // CollectSources composition for a collider owner with the door-pose movers: layer rule, then the round-open rule.
    static string ReasonOf(Transform owner, List<Transform> legacyBarriers, List<Transform> moved)
    {
        return NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, legacyBarriers, moved);
    }
    static void CheckReason(string label, Transform owner, string expected, List<Transform> legacyBarriers, List<Transform> moved)
    {
        CheckEqual(label, ReasonOf(owner, legacyBarriers, moved), expected);
    }
    static string Describe(IEnumerable<string> items) { return string.Join(" | ", items); }

    // Bay5's OLD_WAREHOUSE_DOORS/Gates_Depot_Frame as the package has it: a frame root with two Telfer_Help posts, the frame
    // colliders, and two door parts that each hold one leaf with its own colliders.
    sealed class Gate
    {
        public Transform frame, post, postCollider, postBallistic, frameCollider, frameVisual, doorL, leafL, leafLCollider, leafLInner, doorR, leafR, leafRCollider;
    }
    static Gate MakeGate(Transform parent, float x, float y, float z)
    {
        var g = new Gate();
        g.frame = Make("Gates_Depot_Frame", 0, parent, x, y, z);
        g.post = Make("Telfer_Help (2)", 0, g.frame, x + 2.5f, y + 4.4f, z + 0.1f);
        g.postCollider = Make("Telfer_Help_Collider", 18, g.post);
        g.postBallistic = Make("Telfer_Help_Metal", 12, Make("Ballistic", 0, g.post));
        g.frameVisual = Make("Gates_Depot_Frame_LOD0", 0, g.frame);
        g.frameCollider = Make("Gates_Depot_Frame_COLLIDER", 18, g.frame);
        g.doorL = Make("Gates_Depot_Door_L", 0, g.frame);
        g.leafL = Make("Gates_Depot_Door_L_Leaf", 0, g.doorL);
        g.leafLCollider = Make("Gates_Depot_Door_L_COLLIDER", 18, g.leafL);
        g.leafLInner = Make("Gates_Depot_Door_02_L_COLLIDER", 18, Make("Gates_Depot_Door_02_L", 0, g.leafL));
        g.doorR = Make("Gates_Depot_Door_R", 0, g.frame);
        g.leafR = Make("Gates_Depot_Door_R_Leaf", 0, g.doorR);
        g.leafRCollider = Make("Gates_Depot_Door_R_COLLIDER", 18, g.leafR);
        return g;
    }

    static void DoorPoseCases()
    {
        const string DEPOT = "OLD_WAREHOUSE_DOORS/Gates_Depot_Frame";
        var none = new List<Transform>();
        List<string> unresolved;
        int posed;

        // --- DoorRowAppliesTo: no 'at' = every instance; otherwise the Euclidean world distance within 0.05 m (ArenaDoorPose.AppliesTo)
        var anywhere = Row("B", "P", "");
        var placed = Row("B", "P", "", 10f, 2f, -5f);
        CheckTrue("at-null-applies-anywhere", DoorRowAppliesTo(anywhere, new Vector3(1000f, -3f, 7f)));
        CheckTrue("at-exact", DoorRowAppliesTo(placed, new Vector3(10f, 2f, -5f)));
        CheckTrue("at-within-tolerance", DoorRowAppliesTo(placed, new Vector3(10.03f, 2.03f, -5f)));
        CheckTrue("at-axis-tolerance-is-euclidean", !DoorRowAppliesTo(placed, new Vector3(10.04f, 2.04f, -5f)));   // 0.0566 m away
        CheckTrue("at-outside-tolerance", !DoorRowAppliesTo(placed, new Vector3(10.06f, 2f, -5f)));
        CheckEqual("format-position", FormatDoorPosition(new Vector3(1.23456f, -0.5f, 100f)), "(1.235, -0.500, 100.000)");

        // --- FindAllInstances: every object a path names (all roots of the name, all children of the name per level)
        var sceneA = new Transform("OLD_WAREHOUSE_DOORS", 0);
        var depot1 = MakeGate(sceneA, 50.574f, -3.895f, 7.525f);
        var depot2 = MakeGate(sceneA, 69.045f, -3.883f, -6.317f);
        var depotRoots = new[] { sceneA.gameObject };
        var found = FindAllInstances(depotRoots, DEPOT);
        CheckEqual("find-all-instances-count", found.Count, 2);
        CheckTrue("find-all-instances-order", found[0] == depot1.frame && found[1] == depot2.frame);
        CheckEqual("find-all-first-only-differs", sceneA.Find("Gates_Depot_Frame") == depot1.frame, true);   // Transform.Find would stop at the first
        CheckEqual("find-all-root-itself", FindAllInstances(depotRoots, "OLD_WAREHOUSE_DOORS").Count, 1);
        CheckEqual("find-all-missing-leaf", FindAllInstances(depotRoots, "OLD_WAREHOUSE_DOORS/Nope").Count, 0);
        CheckEqual("find-all-missing-root", FindAllInstances(depotRoots, "Elsewhere/Gates_Depot_Frame").Count, 0);
        CheckEqual("find-all-wrong-middle", FindAllInstances(depotRoots, "OLD_WAREHOUSE_DOORS/Nope/Gates_Depot_Frame").Count, 0);
        var twinA = new Transform("SPAWN_DOORS", 0);
        var twinB = new Transform("SPAWN_DOORS", 0);
        Make("Door", 0, twinA); Make("Door", 0, twinB); Make("Door", 0, twinB);
        CheckEqual("find-all-same-name-roots", FindAllInstances(new[] { twinA.gameObject, twinB.gameObject }, "SPAWN_DOORS/Door").Count, 3);
        var group1 = Make("Group", 0, twinA); var group2 = Make("Group", 0, twinA);
        Make("Gate", 0, group1); Make("Gate", 0, group2); Make("Gate", 0, group2);
        CheckEqual("find-all-nested-duplicates", FindAllInstances(new[] { twinA.gameObject }, "SPAWN_DOORS/Group/Gate").Count, 3);
        CheckEqual("find-all-null-root-skipped", FindAllInstances(new GameObject[] { null, sceneA.gameObject }, DEPOT).Count, 2);

        // --- Bay5 depot gates: both instances have rows told apart by 'at'
        var depotRows = new[]
        {
            Row(DEPOT, "Gates_Depot_Door_L", "Gates_Depot_Door_L_Leaf", 50.574f, -3.895f, 7.525f),
            Row(DEPOT, "Gates_Depot_Door_R", "Gates_Depot_Door_R_Leaf", 50.574f, -3.895f, 7.525f),
            Row(DEPOT, "Gates_Depot_Door_L", "Gates_Depot_Door_L_Leaf", 69.045f, -3.883f, -6.317f),
            Row(DEPOT, "Gates_Depot_Door_R", "Gates_Depot_Door_R_Leaf", 69.045f, -3.883f, -6.317f),
        };
        var moved = new List<Transform>();
        var legacy = ResolveOpenedBarriers(depotRoots, new[] { DEPOT }, depotRows, moved, out unresolved, out posed);
        CheckEqual("depot-posed", posed, 1);
        CheckEqual("depot-legacy-empty", legacy.Count, 0);
        CheckEqual("depot-unresolved-empty", Describe(unresolved), "");
        CheckEqual("depot-moved-count", moved.Count, 4);
        CheckTrue("depot-moved-are-the-door-parts", moved[0] == depot1.doorL && moved[1] == depot1.doorR && moved[2] == depot2.doorL && moved[3] == depot2.doorR);
        CheckEqual("depot-no-warning", Debug.Warnings.Count, 0);
        // the leaves and everything below go, wherever they sit and whatever they are called ...
        foreach (var g in new[] { depot1, depot2 })
        {
            CheckReason("depot-door-part-itself", g.doorL, "door_poses:Gates_Depot_Door_L", legacy, moved);
            CheckReason("depot-leaf-collider", g.leafLCollider, "door_poses:Gates_Depot_Door_L", legacy, moved);
            CheckReason("depot-second-panel-collider", g.leafLInner, "door_poses:Gates_Depot_Door_L", legacy, moved);
            CheckReason("depot-right-leaf-collider", g.leafRCollider, "door_poses:Gates_Depot_Door_R", legacy, moved);
            // ... and the structure stays a wall: posts, post ballistic twins, frame colliders (frame-named or not)
            CheckReason("depot-post-stays", g.postCollider, null, legacy, moved);
            CheckReason("depot-post-ballistic-stays", g.postBallistic, null, legacy, moved);
            CheckReason("depot-frame-collider-stays", g.frameCollider, null, legacy, moved);
            CheckReason("depot-barrier-root-stays", g.frame, null, legacy, moved);
        }
        // the markup rule is the same one (a NavMeshModifier under a moved leaf must not carve a hole)
        CheckEqual("depot-markup-under-leaf", RoundOpenedReason(Make("Marker", 13, depot2.leafR), legacy, moved), "door_poses:Gates_Depot_Door_R");
        CheckEqual("depot-markup-under-post", RoundOpenedReason(Make("Marker", 13, depot2.post), legacy, moved), null);
        // a frame-named object INSIDE a moved part travels with it (the legacy rule would have kept it standing)
        var frameInsideLeaf = Make("Gates_Depot_Door_L_frame_plate", 18, depot1.leafL);
        CheckReason("depot-frame-named-inside-moved-part", frameInsideLeaf, "door_poses:Gates_Depot_Door_L", legacy, moved);
        // the same hierarchy under the LEGACY rule (no rows): posts go, frame colliders stay -- the behaviour the rows replace
        var legacyOnly = new List<Transform>();
        var legacyList = ResolveOpenedBarriers(depotRoots, new[] { DEPOT }, new ArenaDoorRow[0], legacyOnly, out unresolved, out posed);
        CheckEqual("legacy-posed-zero", posed, 0);
        CheckEqual("legacy-moved-empty", legacyOnly.Count, 0);
        CheckEqual("legacy-resolves-find-first", legacyList.Count, 1);
        CheckTrue("legacy-is-the-first-instance", legacyList[0] == depot1.frame);
        CheckReason("legacy-post-goes", depot1.postCollider, "opened_barriers:Gates_Depot_Frame", legacyList, legacyOnly);
        CheckReason("legacy-leaf-goes", depot1.leafLCollider, "opened_barriers:Gates_Depot_Frame", legacyList, legacyOnly);
        CheckReason("legacy-frame-collider-stays", depot1.frameCollider, null, legacyList, legacyOnly);
        CheckReason("legacy-second-instance-untouched", depot2.leafLCollider, null, legacyList, legacyOnly);

        // --- repeated path, a row for one instance only: the other stays a wall and the bake says so
        Debug.Warnings.Clear();
        var oneRow = new[] { Row(DEPOT, "Gates_Depot_Door_L", "Gates_Depot_Door_L_Leaf", 69.045f, -3.883f, -6.317f) };
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(depotRoots, new[] { DEPOT }, oneRow, moved, out unresolved, out posed);
        CheckEqual("uncovered-moved", moved.Count, 1);
        CheckTrue("uncovered-moved-is-the-covered-instance", moved[0] == depot2.doorL);
        CheckEqual("uncovered-not-legacy", legacy.Count, 0);
        CheckEqual("uncovered-unresolved-empty", Describe(unresolved), "");
        CheckEqual("uncovered-warning-count", Debug.Warnings.Count, 1);
        CheckTrue("uncovered-warning-names-the-instance", Debug.Warnings[0].Contains("(50.574, -3.895, 7.525)") && Debug.Warnings[0].Contains("stays baked as a wall"));
        CheckReason("uncovered-instance-stays-a-wall", depot1.leafLCollider, null, legacy, moved);
        CheckReason("uncovered-covered-leaf-goes", depot2.leafLCollider, "door_poses:Gates_Depot_Door_L", legacy, moved);
        CheckReason("uncovered-covered-sibling-door-stays", depot2.leafRCollider, null, legacy, moved);   // no row for the R part of this instance
        Debug.Warnings.Clear();

        // --- rows that resolve to nothing fail loudly (the caller throws on a non-empty list)
        moved = new List<Transform>();
        ResolveOpenedBarriers(depotRoots, new[] { DEPOT }, new[] { Row(DEPOT, "Gates_Depot_Door_L", "", 51.0f, -3.895f, 7.525f), depotRows[0] },
                              moved, out unresolved, out posed);
        CheckEqual("stale-at-reported", unresolved.Count, 1);
        CheckTrue("stale-at-names-the-row", unresolved[0].Contains("matches no instance") && unresolved[0].Contains("(51.000, -3.895, 7.525)"));
        CheckEqual("stale-at-other-row-still-applied", moved.Count, 1);
        CheckTrue("stale-at-other-row-moves-its-part", moved[0] == depot1.doorL);
        moved = new List<Transform>();
        ResolveOpenedBarriers(depotRoots, new[] { DEPOT }, new[] { Row(DEPOT, "Gates_Depot_Door_Q", "", 50.574f, -3.895f, 7.525f) }, moved, out unresolved, out posed);
        CheckEqual("missing-part-reported", unresolved.Count, 1);
        CheckTrue("missing-part-names-it", unresolved[0].Contains("Gates_Depot_Door_Q") && unresolved[0].Contains("door pose part"));
        CheckEqual("missing-part-moves-nothing", moved.Count, 0);
        moved = new List<Transform>();
        ResolveOpenedBarriers(depotRoots, new[] { "OLD_WAREHOUSE_DOORS/Gates_Missing" },
                              new[] { Row("OLD_WAREHOUSE_DOORS/Gates_Missing", "Gates_Depot_Door_L", "", 1f, 2f, 3f) }, moved, out unresolved, out posed);
        CheckEqual("missing-barrier-with-at-reported", Describe(unresolved), "OLD_WAREHOUSE_DOORS/Gates_Missing");
        moved = new List<Transform>();
        ResolveOpenedBarriers(depotRoots, new[] { "OLD_WAREHOUSE_DOORS/Gates_Missing" },
                              new[] { Row("OLD_WAREHOUSE_DOORS/Gates_Missing", "Gates_Depot_Door_L", "") }, moved, out unresolved, out posed);
        CheckEqual("missing-barrier-without-at-reported", Describe(unresolved), "OLD_WAREHOUSE_DOORS/Gates_Missing");
        ResolveOpenedBarriers(depotRoots, new[] { "OLD_WAREHOUSE_DOORS/Gates_Missing" }, new ArenaDoorRow[0], moved, out unresolved, out posed);
        CheckEqual("missing-barrier-legacy-reported", Describe(unresolved), "OLD_WAREHOUSE_DOORS/Gates_Missing");

        // --- Bay5 rollers: the root itself moves (part ""), no 'at' (the paths are unique), the neighbours stay
        var warehouse = new Transform("WAREHOUSE_DOORS (1)", 0);
        Transform[] rollers = new Transform[4];
        string[] rollerNames = { "Bay5_Warehouse_Gate_Door_01 (2)", "Bay5_Warehouse_Gate_Door_01 (3)", "Bay5_Warehouse_Gate_Door_01 (4)", "One_man_Door" };
        var rollerColliders = new Transform[4];
        var rollerRows = new List<ArenaDoorRow>();
        var rollerPaths = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            rollers[i] = Make(rollerNames[i], 0, warehouse, 100f + 10f * i, -2.9f, 40f);
            Make("Rollete_Gate_Door_01_LOD0", 0, rollers[i]);
            rollerColliders[i] = Make("Rollete_Gate_Door_01_COLLIDER", 18, rollers[i]);
            rollerPaths.Add("WAREHOUSE_DOORS (1)/" + rollerNames[i]);
            rollerRows.Add(Row(rollerPaths[i], "", ""));
        }
        var rollerFrame = Make("Bay5_Warehouse_Gate_Frame", 0, warehouse);
        var rollerFrameCollider = Make("Bay5_Warehouse_Gate_Frame_COLLIDER", 18, rollerFrame);
        var unlistedRoller = Make("Bay5_Warehouse_Gate_Door_01 (5)", 0, warehouse);
        var unlistedRollerCollider = Make("Rollete_Gate_Door_01_COLLIDER", 18, unlistedRoller);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { warehouse.gameObject }, rollerPaths.ToArray(), rollerRows.ToArray(), moved, out unresolved, out posed);
        CheckEqual("rollers-posed", posed, 4);
        CheckEqual("rollers-legacy-empty", legacy.Count, 0);
        CheckEqual("rollers-unresolved-empty", Describe(unresolved), "");
        CheckEqual("rollers-moved-roots", moved.Count, 4);
        for (int i = 0; i < 4; i++)
        {
            CheckTrue("roller-root-is-the-moved-subtree-" + i, moved[i] == rollers[i]);
            CheckReason("roller-collider-goes-" + i, rollerColliders[i], "door_poses:" + rollerNames[i], legacy, moved);
            CheckReason("roller-root-itself-goes-" + i, rollers[i], "door_poses:" + rollerNames[i], legacy, moved);
        }
        CheckReason("roller-frame-stays", rollerFrameCollider, null, legacy, moved);
        CheckReason("unlisted-roller-stays", unlistedRollerCollider, null, legacy, moved);
        CheckReason("warehouse-root-stays", warehouse, null, legacy, moved);

        // --- several rows on one part (a vehicle's wheels): the part is added once
        var depot = new Transform("DOORS_SPAWN_A", 0);
        var btr = Make("BTR_82_closed", 0, depot);
        var wheel = Make("BTR_82_wheel_L_01", 0, btr);
        var btrCollider = Make("BTR_82_exterior_closed_COLLIDER", 18, btr);
        var gateObj = Make("Bowl_Gates_Respawn_01", 0, depot);
        var rail = Make("Bowl_Gates_Respawn_01_RAIL_COLLIDER", 18, gateObj);
        var extra = Make("Extra_part_without_a_row", 0, depot);
        var extraCollider = Make("Extra_COLLIDER", 18, extra);
        var bowlRows = new[]
        {
            Row("DOORS_SPAWN_A", "BTR_82_closed", ""), Row("DOORS_SPAWN_A", "BTR_82_closed", "BTR_82_wheel_L_01"),
            Row("DOORS_SPAWN_A", "Bowl_Gates_Respawn_01", "Bowl_Gates_Respawn_01_Gate"),
        };
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { depot.gameObject }, new[] { "DOORS_SPAWN_A" }, bowlRows, moved, out unresolved, out posed);
        CheckEqual("vehicle-parts-deduplicated", moved.Count, 2);
        CheckEqual("vehicle-posed", posed, 1);
        CheckReason("vehicle-body-goes", btrCollider, "door_poses:BTR_82_closed", legacy, moved);
        CheckReason("vehicle-wheel-goes", wheel, "door_poses:BTR_82_closed", legacy, moved);
        CheckReason("vehicle-static-rail-of-a-posed-part-goes-too", rail, "door_poses:Bowl_Gates_Respawn_01", legacy, moved);
        CheckReason("barrier-part-without-a-row-stays", extraCollider, null, legacy, moved);   // the rule is per part: a posed barrier keeps what Arena does not move

        // --- a path with rows but no 'at' resolves like the legacy rule (first match), the twin stays
        var yardScene = new Transform("Root", 0);
        var gateA = Make("Gate", 0, yardScene, 1f, 0f, 0f);
        var gateB = Make("Gate", 0, yardScene, 2f, 0f, 0f);
        var gateALeaf = Make("Leaf", 0, gateA); var gateBLeaf = Make("Leaf", 0, gateB);
        var gateALeafCollider = Make("Leaf_COLLIDER", 18, gateALeaf); var gateBLeafCollider = Make("Leaf_COLLIDER", 18, gateBLeaf);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { yardScene.gameObject }, new[] { "Root/Gate" }, new[] { Row("Root/Gate", "Leaf", "") }, moved, out unresolved, out posed);
        CheckEqual("no-at-first-match-only", moved.Count, 1);
        CheckTrue("no-at-first-match-is-gate-a", moved[0] == gateALeaf);
        CheckReason("no-at-twin-stays", gateBLeafCollider, null, legacy, moved);
        CheckReason("no-at-first-goes", gateALeafCollider, "door_poses:Leaf", legacy, moved);

        // --- mixed list: a path with rows and a path without; rows of unlisted barriers and null inputs change nothing
        var legacyGateRoot = Make("LegacyGate", 0, yardScene);
        var legacyLeaf = Make("Leaf_COLLIDER", 18, Make("Leaf", 0, legacyGateRoot));
        var legacyFrame = Make("LegacyGate_frame_COLLIDER", 18, legacyGateRoot);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { yardScene.gameObject }, new[] { "Root/Gate", "Root/LegacyGate" }, new[] { Row("Root/Gate", "Leaf", "") }, moved, out unresolved, out posed);
        CheckEqual("mixed-posed-count", posed, 1);
        CheckEqual("mixed-legacy-count", legacy.Count, 1);
        CheckReason("mixed-legacy-leaf-goes", legacyLeaf, "opened_barriers:LegacyGate", legacy, moved);
        CheckReason("mixed-legacy-frame-stays", legacyFrame, null, legacy, moved);
        CheckReason("mixed-posed-leaf-goes", gateALeafCollider, "door_poses:Leaf", legacy, moved);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { yardScene.gameObject }, new string[0], new[] { Row("Root/Gate", "Leaf", "") }, moved, out unresolved, out posed);
        CheckTrue("unlisted-rows-ignored", legacy.Count == 0 && moved.Count == 0 && unresolved.Count == 0 && posed == 0);
        legacy = ResolveOpenedBarriers(new[] { yardScene.gameObject }, null, null, moved, out unresolved, out posed);
        CheckTrue("null-paths-and-rows", legacy.Count == 0 && moved.Count == 0 && unresolved.Count == 0 && posed == 0);
        legacy = ResolveOpenedBarriers(new[] { yardScene.gameObject }, new[] { "Root/LegacyGate", "" }, null, moved, out unresolved, out posed);
        CheckTrue("null-rows-keep-the-legacy-rule", legacy.Count == 1 && moved.Count == 0 && posed == 0);

        // --- Equator: the FCK hall shutter is no barrier any more (empty list), so its colliders are ordinary walls;
        //     the booth leaves are still dropped by name whatever the list says
        var soo = new Transform("SBG_Arena_Equator", 0);
        var shutter = Make("equator_roller_shutters_02 (9)", 0, Make("SOO_LOD0", 0, soo));
        var shutterCollider = Make("roller_shutters_02_COLLIDER", 18, shutter);
        var shutterBallistic = Make("roller_shutters_02_BALLISTIC_metalthin", 12, shutter);
        var shutterFrameCollider = Make("roller_shutters_frame_02_COLLIDER", 18, shutter);
        legacy = ResolveOpenedBarriers(new[] { soo.gameObject }, new string[0], new ArenaDoorRow[0], moved = new List<Transform>(), out unresolved, out posed);
        CheckReason("equator-shutter-collider-is-a-wall", shutterCollider, null, legacy, moved);
        CheckReason("equator-shutter-ballistic-is-a-wall", shutterBallistic, null, legacy, moved);
        CheckReason("equator-shutter-frame-is-a-wall", shutterFrameCollider, null, legacy, moved);
        var legacyShutter = ResolveOpenedBarriers(new[] { soo.gameObject }, new[] { "SBG_Arena_Equator/SOO_LOD0/equator_roller_shutters_02 (9)" }, new ArenaDoorRow[0],
                                                  new List<Transform>(), out unresolved, out posed);
        CheckReason("equator-shutter-as-a-legacy-barrier-would-go", shutterCollider, "opened_barriers:equator_roller_shutters_02 (9)", legacyShutter, none);
        CheckReason("equator-shutter-frame-as-a-legacy-barrier-stays", shutterFrameCollider, null, legacyShutter, none);
        var booth = Make("Door_Sliding_Respawn", 0, Make("SPAWN_DOORS", 0, null), -4.328f, 0.048f, 40.02f);
        var boothLeaf = Make("Door_Sliding_Respawn_L", 0, booth);
        var boothLeafCollider = Make("Door_Sliding_Respawn_L_COLLIDER", 18, boothLeaf);
        var boothSurround = Make("Door_Sliding_Respawn_COLLIDER", 18, booth);
        CheckReason("equator-booth-leaf-by-name", boothLeafCollider, "Door_Sliding_Respawn_L", legacy, moved);
        CheckReason("equator-booth-surround-stays", boothSurround, null, legacy, moved);
        // the booth assembly roots have rows but are not opened_barriers entries: listing nothing reads no row
        moved = new List<Transform>();
        ResolveOpenedBarriers(new[] { booth.parent.gameObject }, new string[0],
                              new[] { Row("SPAWN_DOORS/Door_Sliding_Respawn", "Door_Sliding_Respawn_L", "", -4.328f, 0.048f, 40.02f) }, moved, out unresolved, out posed);
        CheckTrue("equator-booth-rows-are-not-read-for-the-bake", moved.Count == 0 && unresolved.Count == 0 && posed == 0);
    }

    // ---- 2026-10-24 (AirPit): repeated part names told apart by the closed position, switch rows, node rows of unique parts ----
    static ArenaDoorRow RowClosed(string barrier, string part, string node, float cx, float cy, float cz, params float[] at)
    {
        return new ArenaDoorRow { barrier = barrier, part = part, node = node, at = at.Length == 0 ? null : at, closed_position = new[] { cx, cy, cz } };
    }
    static ArenaDoorRow SwitchRow(string barrier, string part, string node, params float[] at)
    {
        return new ArenaDoorRow { barrier = barrier, part = part, node = node, at = at.Length == 0 ? null : at, closed_position = new[] { 0f, 0f, 0f }, is_switch = true };
    }
    // A child at a LOCAL position (the stand-in has no rotation or scale, so the world position is the sum).
    static Transform MakeLocal(string name, int layer, Transform parent, float x, float y, float z)
    {
        var t = new Transform(name, layer, parent);
        t.localPosition = new Vector3(x, y, z);
        t.position = new Vector3(parent.position.x + x, parent.position.y + y, parent.position.z + z);
        return t;
    }

    // AirPit's PASSPORT_CONTROL_DOORS as the package has it: RED and GREEN lamp covers (a fog node with a cover below, no collider)
    // and 16 leaves under six names, each with a movement collider (layer 18) and a ballistic twin (layer 12).
    sealed class Passport
    {
        public Transform root, red, redFog, redCover, green, greenFog, greenCover, unknownLeaf, unknownCollider;
        public Transform[] leaves, colliders, ballistics;          // in the sidecar's row order
        public List<ArenaDoorRow> rows = new List<ArenaDoorRow>(); // GREEN, the 16 leaves, RED (the sidecar's order: sorted by part)
    }
    const string PASSPORT = "PASSPORT_CONTROL_DOORS";
    static Passport MakePassport(bool withUnknownLeaf)
    {
        var p = new Passport();
        p.root = new Transform(PASSPORT, 0);
        p.root.position = new Vector3(-16.146f, 3.945f, 63.186f);
        p.green = MakeLocal("GREEN", 0, p.root, 15.671f, 12.003f, -74.448f);
        p.greenFog = MakeLocal("GreenFog", 0, p.green, 0f, 0f, 0f);
        p.greenCover = MakeLocal("Passport_control_lamp_cover", 0, p.greenFog, 0f, 0f, 0f);
        p.red = MakeLocal("RED", 0, p.root, 15.671f, 12.003f, -74.448f);
        p.redFog = MakeLocal("RedFog", 0, p.red, 0f, 0f, 0f);
        p.redCover = MakeLocal("Passport_control_lamp_cover", 0, p.redFog, 0f, 0f, 0f);
        var spec = new List<float[]>();                              // { number, local x, local z }
        foreach (float x in new[] { -0.481f, 4.483f, 9.446f }) spec.Add(new[] { 1f, x, 3.099f });
        foreach (float x in new[] { -0.481f, 4.483f, 9.446f }) spec.Add(new[] { 2f, x, 5.938f });
        foreach (float x in new[] { -5.089f, -0.125f, 4.838f, 9.802f }) spec.Add(new[] { 3f, x, 3.099f });
        foreach (float x in new[] { -5.089f, -0.125f, 4.838f, 9.802f }) spec.Add(new[] { 4f, x, 5.938f });
        spec.Add(new[] { 5f, 19.37f, 3.099f });
        spec.Add(new[] { 6f, 19.37f, 5.938f });
        p.leaves = new Transform[spec.Count]; p.colliders = new Transform[spec.Count]; p.ballistics = new Transform[spec.Count];
        for (int i = spec.Count - 1; i >= 0; i--)                    // the scene lists the leaves in another order than the sidecar
        {
            string name = "Passport_control_01_Door (" + (int)spec[i][0] + ")";
            p.leaves[i] = MakeLocal(name, 0, p.root, spec[i][1], -3.23f, spec[i][2]);
            Make("Passport_control_01_Door_LOD0", 0, p.leaves[i]);
            p.colliders[i] = Make("Passport_control_01_Door_COLLIDER", 18, p.leaves[i]);
            p.ballistics[i] = Make("Passport_control_01_Door_BALLISTIC_Metalthin", 12, p.leaves[i]);
        }
        if (withUnknownLeaf)
        {
            p.unknownLeaf = MakeLocal("Passport_control_01_Door (4)", 0, p.root, 30f, -3.23f, 5.938f);
            p.unknownCollider = Make("Passport_control_01_Door_COLLIDER", 18, p.unknownLeaf);
        }
        p.rows.Add(SwitchRow(PASSPORT, "GREEN", "GreenFog"));
        for (int i = 0; i < spec.Count; i++)
            p.rows.Add(RowClosed(PASSPORT, "Passport_control_01_Door (" + (int)spec[i][0] + ")", "", spec[i][1], -3.23f, spec[i][2]));
        p.rows.Add(SwitchRow(PASSPORT, "RED", "RedFog"));
        return p;
    }

    static void PassportCases()
    {
        List<string> unresolved;
        int posed;

        // --- the real shape: 16 rows tell 16 leaves apart under six names, the two switch rows drop nothing
        Debug.Warnings.Clear();
        var p = MakePassport(false);
        var moved = new List<Transform>();
        var legacy = ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT }, p.rows.ToArray(), moved, out unresolved, out posed);
        CheckEqual("passport-posed", posed, 1);
        CheckEqual("passport-legacy-empty", legacy.Count, 0);
        CheckEqual("passport-unresolved-empty", Describe(unresolved), "");
        CheckEqual("passport-no-warning", Debug.Warnings.Count, 0);
        CheckEqual("passport-moved-count", moved.Count, 16);
        for (int k = 0; k < 16; k++)
        {
            CheckTrue("passport-row-" + k + "-moves-its-own-leaf", moved[k] == p.leaves[k]);
            CheckReason("passport-collider-" + k + "-goes", p.colliders[k], "door_poses:" + p.leaves[k].name, legacy, moved);
            CheckReason("passport-ballistic-" + k + "-goes", p.ballistics[k], "door_poses:" + p.leaves[k].name, legacy, moved);
        }
        // the lamp covers and the barrier root are not dropped: the switch rows move nothing and the client keeps them standing
        CheckReason("passport-green-part-stays", p.green, null, legacy, moved);
        CheckReason("passport-green-fog-stays", p.greenFog, null, legacy, moved);
        CheckReason("passport-green-cover-stays", p.greenCover, null, legacy, moved);
        CheckReason("passport-red-cover-stays", p.redCover, null, legacy, moved);
        CheckReason("passport-root-stays", p.root, null, legacy, moved);

        // --- the same leaves under the legacy rule (no rows): every non-frame descendant goes, which is what the rows replace
        var legacyMoved = new List<Transform>();
        var legacyList = ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT }, new ArenaDoorRow[0], legacyMoved, out unresolved, out posed);
        CheckEqual("passport-legacy-rule-posed-zero", posed, 0);
        CheckReason("passport-legacy-rule-drops-the-green-cover-too", p.greenCover, "opened_barriers:" + PASSPORT, legacyList, legacyMoved);

        // --- only the switch rows: the barrier counts as posed (the client keeps everything standing) and nothing is dropped
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT },
                                       new[] { SwitchRow(PASSPORT, "GREEN", "GreenFog"), SwitchRow(PASSPORT, "RED", "RedFog") }, moved, out unresolved, out posed);
        CheckEqual("switch-only-posed", posed, 1);
        CheckEqual("switch-only-nothing-moved", moved.Count, 0);
        CheckEqual("switch-only-legacy-empty", legacy.Count, 0);
        CheckEqual("switch-only-unresolved-empty", Describe(unresolved), "");
        CheckReason("switch-only-leaf-collider-stays-a-wall", p.colliders[0], null, legacy, moved);
        CheckReason("switch-only-lamp-cover-stays", p.greenCover, null, legacy, moved);

        // --- a switch row never fails the build: a part or node the scene does not have, or an 'at' that names no instance
        Debug.Warnings.Clear();
        moved = new List<Transform>();
        ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT },
                              new[] { SwitchRow(PASSPORT, "Nonexistent", "Nowhere"), SwitchRow(PASSPORT, "GREEN", "GreenFog", 1000f, 0f, 0f), p.rows[1] },
                              moved, out unresolved, out posed);
        CheckEqual("switch-missing-part-and-stale-at-unresolved-empty", Describe(unresolved), "");
        CheckEqual("switch-rows-move-nothing-the-move-row-moves-its-leaf", moved.Count, 1);
        CheckTrue("switch-rows-leave-the-leaf-row-alone", moved[0] == p.leaves[0]);
        // ... and a switch row that sits on a part a move row also names does not make the part move on its own
        moved = new List<Transform>();
        ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT }, new[] { SwitchRow(PASSPORT, "Passport_control_01_Door (5)", "") }, moved, out unresolved, out posed);
        CheckEqual("switch-row-on-a-leaf-name-drops-nothing", moved.Count, 0);
        // a move row with the same missing part still fails (the switch exemption is the row's, not the barrier's)
        moved = new List<Transform>();
        ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT }, new[] { RowClosed(PASSPORT, "Nonexistent", "Nowhere", 0f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("move-row-with-a-missing-part-still-fails", unresolved.Count, 1);

        // --- a leaf no row stands for stays a wall and the bake says so (a 17th leaf under an existing name)
        Debug.Warnings.Clear();
        var q = MakePassport(true);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { q.root.gameObject }, new[] { PASSPORT }, q.rows.ToArray(), moved, out unresolved, out posed);
        CheckEqual("unknown-leaf-moved-count", moved.Count, 16);
        CheckEqual("unknown-leaf-unresolved-empty", Describe(unresolved), "");
        CheckEqual("unknown-leaf-warning-count", Debug.Warnings.Count, 1);
        CheckTrue("unknown-leaf-warning-names-it", Debug.Warnings[0].Contains("Passport_control_01_Door (4)") && Debug.Warnings[0].Contains("(30.000, -3.230, 5.938)") &&
                                                   Debug.Warnings[0].Contains("stays baked as a wall"));
        CheckReason("unknown-leaf-collider-stays-a-wall", q.unknownCollider, null, legacy, moved);
        CheckReason("unknown-leaf-same-named-neighbour-goes", q.colliders[11], "door_poses:Passport_control_01_Door (4)", legacy, moved);
        Debug.Warnings.Clear();

        // --- a leaf row whose closed position matches no same-named child fails the build, naming the position
        moved = new List<Transform>();
        var shifted = RowClosed(PASSPORT, "Passport_control_01_Door (4)", "", 4.838f + 0.06f, -3.23f, 5.938f);
        ResolveOpenedBarriers(new[] { p.root.gameObject }, new[] { PASSPORT }, new[] { shifted }, moved, out unresolved, out posed);
        CheckEqual("shifted-closed-position-reported", unresolved.Count, 1);
        CheckTrue("shifted-closed-position-names-the-part-and-position", unresolved[0].Contains("Passport_control_01_Door (4)") && unresolved[0].Contains("door pose part") &&
                                                                      unresolved[0].Contains("closed position (4.898, -3.230, 5.938)"));
        CheckEqual("shifted-closed-position-moves-nothing", moved.Count, 0);
    }

    static void RepeatedNameCases()
    {
        List<string> unresolved;
        int posed;
        const string B = "Yard/Gate";
        var scene = new Transform("Yard", 0);
        var gate = Make("Gate", 0, scene, 10f, 0f, 0f);
        var leaf = new Transform[4];
        var leafCollider = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            leaf[i] = MakeLocal("Leaf", 0, gate, (float)i, 0f, 0f);
            leafCollider[i] = Make("Leaf_COLLIDER", 18, leaf[i]);
        }
        var roots = new[] { scene.gameObject };

        // --- one row of four same-named leaves: only the one standing at its closed position goes, the other three are named
        Debug.Warnings.Clear();
        var moved = new List<Transform>();
        var legacy = ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 2f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("repeat-one-row-moves-one", moved.Count, 1);
        CheckTrue("repeat-one-row-moves-the-third", moved[0] == leaf[2]);
        CheckEqual("repeat-one-row-unresolved-empty", Describe(unresolved), "");
        CheckEqual("repeat-one-row-warns-for-the-other-three", Debug.Warnings.Count, 3);
        CheckTrue("repeat-warning-names-a-position", Debug.Warnings[0].Contains("'Leaf'") && Debug.Warnings[0].Contains("(10.000, 0.000, 0.000)") && Debug.Warnings[0].Contains("stays baked as a wall"));
        CheckReason("repeat-moved-leaf-goes", leafCollider[2], "door_poses:Leaf", legacy, moved);
        CheckReason("repeat-first-leaf-stays", leafCollider[0], null, legacy, moved);   // Transform.Find would have taken this one for every row
        CheckReason("repeat-last-leaf-stays", leafCollider[3], null, legacy, moved);

        // --- four rows, four leaves, in any row order; the rows pick by position, not by sibling order
        Debug.Warnings.Clear();
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 3f, 0f, 0f), RowClosed(B, "Leaf", "", 0f, 0f, 0f),
                                                          RowClosed(B, "Leaf", "", 2f, 0f, 0f), RowClosed(B, "Leaf", "", 1f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("repeat-four-rows-move-four", moved.Count, 4);
        CheckTrue("repeat-rows-pick-by-position", moved[0] == leaf[3] && moved[1] == leaf[0] && moved[2] == leaf[2] && moved[3] == leaf[1]);
        CheckEqual("repeat-four-rows-no-warning", Debug.Warnings.Count, 0);
        CheckEqual("repeat-four-rows-unresolved-empty", Describe(unresolved), "");

        // --- Euclidean tolerance of 0.05 m on the local position
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 2.04f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("repeat-4cm-off-still-matches", moved.Count, 1);
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 2.03f, 0.03f, 0.03f) }, moved, out unresolved, out posed);   // 0.052 m
        CheckEqual("repeat-52mm-off-does-not-match", moved.Count, 0);
        CheckEqual("repeat-52mm-off-is-reported", unresolved.Count, 1);
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 2.03f, 0.03f, 0f) }, moved, out unresolved, out posed);          // 0.042 m
        CheckEqual("repeat-42mm-off-matches", moved.Count, 1);
        Debug.Warnings.Clear();

        // --- two rows that match the same leaf add it once; a row whose position fits two leaves (closer than the tolerance) takes both
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 1f, 0f, 0f), RowClosed(B, "Leaf", "", 1.01f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("repeat-two-rows-one-leaf-added-once", moved.Count, 1);
        var crowd = Make("Gate", 0, scene, 40f, 0f, 0f);
        MakeLocal("Leaf", 0, crowd, 5f, 0f, 0f); MakeLocal("Leaf", 0, crowd, 5.02f, 0f, 0f); MakeLocal("Leaf", 0, crowd, 9f, 0f, 0f);
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { RowClosed(B, "Leaf", "", 5.01f, 0f, 0f, 40f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("repeat-every-matching-sibling-goes", moved.Count, 2);
        crowd.parent.children.Remove(crowd);

        // --- a row without a closed position takes the first child of the name, as Transform.Find did (and the others are named)
        Debug.Warnings.Clear();
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { B }, new[] { new ArenaDoorRow { barrier = B, part = "Leaf", node = "" } }, moved, out unresolved, out posed);
        CheckEqual("repeat-no-closed-position-first-child-only", moved.Count, 1);
        CheckTrue("repeat-no-closed-position-is-the-first-child", moved[0] == leaf[0]);
        CheckEqual("repeat-no-closed-position-names-the-rest", Debug.Warnings.Count, 3);
        Debug.Warnings.Clear();

        // --- a name that occurs ONCE is taken whatever the closed position says (every unique part of the other maps)
        var solo = Make("Solo", 0, scene);
        var soloPart = MakeLocal("Panel", 0, solo, 7f, 0f, 0f);
        var soloCollider = Make("Panel_COLLIDER", 18, soloPart);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(roots, new[] { "Yard/Solo" }, new[] { RowClosed("Yard/Solo", "Panel", "", 99f, 99f, 99f) }, moved, out unresolved, out posed);
        CheckEqual("unique-name-ignores-the-closed-position", moved.Count, 1);
        CheckEqual("unique-name-unresolved-empty", Describe(unresolved), "");
        CheckEqual("unique-name-no-warning", Debug.Warnings.Count, 0);
        CheckReason("unique-name-collider-goes", soloCollider, "door_poses:Panel", legacy, moved);

        // --- a node row of a repeated name is told apart by the NODE's local position, and a twin without the node cannot match
        var pair = Make("Pair", 0, scene);
        var twinA = MakeLocal("Twin", 0, pair, 0f, 0f, 0f);
        var twinB = MakeLocal("Twin", 0, pair, 5f, 0f, 0f);
        var twinC = MakeLocal("Twin", 0, pair, 9f, 0f, 0f);
        var hingeA = MakeLocal("Hinge", 0, twinA, 1f, 0f, 0f);
        var hingeB = MakeLocal("Hinge", 0, twinB, 2f, 0f, 0f);                // twinC has no Hinge at all
        var colliderB = Make("Hinge_COLLIDER", 18, hingeB);
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(roots, new[] { "Yard/Pair" }, new[] { RowClosed("Yard/Pair", "Twin", "Hinge", 2f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckEqual("node-row-moves-one-twin", moved.Count, 1);
        CheckTrue("node-row-picks-the-twin-by-its-hinge-position", moved[0] == twinB);
        CheckEqual("node-row-unresolved-empty", Describe(unresolved), "");
        CheckReason("node-row-whole-twin-subtree-goes", colliderB, "door_poses:Twin", legacy, moved);
        CheckEqual("node-row-names-the-twins-no-row-stands-for", Debug.Warnings.Count, 2);
        Debug.Warnings.Clear();
        moved = new List<Transform>();
        ResolveOpenedBarriers(roots, new[] { "Yard/Pair" }, new[] { RowClosed("Yard/Pair", "Twin", "Hinge", 1f, 0f, 0f) }, moved, out unresolved, out posed);
        CheckTrue("node-row-position-of-the-part-itself-would-not-match", moved.Count == 1 && moved[0] == twinA);   // twin A sits at 0, its hinge at 1: the node decides
        Debug.Warnings.Clear();
    }

    // AirPit's BUS_DOORS: six assemblies with unique names, each with two hinges that hold a leaf; the sidecar has four node rows for each of the
    // three FRONT assemblies (the closed positions are the nodes', local to their parent) and none for the back row, which stands open.
    static void BusDoorCases()
    {
        List<string> unresolved;
        int posed;
        const string BUS = "BUS_DOORS";
        var root = new Transform(BUS, 0);
        root.position = new Vector3(-17.567f, 5.829f, -69.347f);
        string[] names = { "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_doors", "Apron_bus_MAZ_doors (5)", "Apron_bus_MAZ_doors (4)", "Apron_bus_MAZ_doors (2)", "Apron_bus_MAZ_doors (3)" };
        float[] xs = { 3.406f, 6.528f, -0.679f, 6.528f, -0.679f, 3.406f };
        float[] zs = { 4.347f, 4.347f, 1.187f, 1.187f, 4.347f, 1.187f };
        var assembly = new Transform[6];
        var legCollider = new Transform[6];
        var bodyCollider = new Transform[6];
        var rows = new List<ArenaDoorRow>();
        for (int i = 0; i < 6; i++)
        {
            assembly[i] = MakeLocal(names[i], 0, root, xs[i], -5.516f, zs[i]);
            bodyCollider[i] = Make("Apron_bus_MAZ_doors_COLLIDER", 18, assembly[i]);
            var hinge1 = MakeLocal("Apron_bus_MAZ_door_L_01_part_01", 0, assembly[i], -0.129f, 0.729f, 2.152f);
            var leaf1 = MakeLocal("Apron_bus_MAZ_door_L_01", 0, hinge1, 0.01f, -0.404f, -1.78f);
            var hinge2 = MakeLocal("Apron_bus_MAZ_door_L_02_part_01", 0, assembly[i], -0.129f, -0.728f, 2.152f);
            var leaf2 = MakeLocal("Apron_bus_MAZ_door_L_02", 0, hinge2, 0.01f, 0.403f, -1.78f);
            legCollider[i] = Make("Apron_bus_MAZ_door_L_01_COLLIDER", 18, leaf1);
            Make("Apron_bus_MAZ_door_L_02_COLLIDER", 18, leaf2);
        }
        foreach (string front in new[] { "Apron_bus_MAZ_doors", "Apron_bus_MAZ_doors (1)", "Apron_bus_MAZ_doors (2)" })
        {
            rows.Add(RowClosed(BUS, front, "Apron_bus_MAZ_door_L_01_part_01", -0.129f, 0.729f, 2.152f));
            rows.Add(RowClosed(BUS, front, "Apron_bus_MAZ_door_L_01_part_01/Apron_bus_MAZ_door_L_01", 0.01f, -0.404f, -1.78f));
            rows.Add(RowClosed(BUS, front, "Apron_bus_MAZ_door_L_02_part_01", -0.129f, -0.728f, 2.152f));
            rows.Add(RowClosed(BUS, front, "Apron_bus_MAZ_door_L_02_part_01/Apron_bus_MAZ_door_L_02", 0.01f, 0.403f, -1.78f));
        }
        Debug.Warnings.Clear();
        var moved = new List<Transform>();
        var legacy = ResolveOpenedBarriers(new[] { root.gameObject }, new[] { BUS }, rows.ToArray(), moved, out unresolved, out posed);
        CheckEqual("bus-posed", posed, 1);
        CheckEqual("bus-unresolved-empty", Describe(unresolved), "");
        CheckEqual("bus-no-warning", Debug.Warnings.Count, 0);
        CheckEqual("bus-three-front-assemblies-move", moved.Count, 3);
        CheckTrue("bus-front-assemblies-in-row-order", moved[0] == assembly[1] && moved[1] == assembly[0] && moved[2] == assembly[4]);
        foreach (int i in new[] { 0, 1, 4 })
        {
            CheckReason("bus-front-body-" + i + "-goes", bodyCollider[i], "door_poses:" + names[i], legacy, moved);
            CheckReason("bus-front-leaf-" + i + "-goes", legCollider[i], "door_poses:" + names[i], legacy, moved);
        }
        foreach (int i in new[] { 2, 3, 5 })
        {
            CheckReason("bus-back-body-" + i + "-is-a-wall", bodyCollider[i], null, legacy, moved);
            CheckReason("bus-back-leaf-" + i + "-is-a-wall", legCollider[i], null, legacy, moved);
        }
        CheckReason("bus-root-stays", root, null, legacy, moved);
        // the old rule dropped all six assemblies (every non-frame descendant): the three back ones are what the rows give back
        var oldMoved = new List<Transform>();
        var oldLegacy = ResolveOpenedBarriers(new[] { root.gameObject }, new[] { BUS }, new ArenaDoorRow[0], oldMoved, out unresolved, out posed);
        for (int i = 0; i < 6; i++)
            CheckReason("bus-old-rule-dropped-assembly-" + i, bodyCollider[i], "opened_barriers:" + BUS, oldLegacy, oldMoved);
        // a back-row assembly with a row of its own would be dropped too (the sidecar has none: it keeps standing open)
        moved = new List<Transform>();
        legacy = ResolveOpenedBarriers(new[] { root.gameObject }, new[] { BUS }, new[] { RowClosed(BUS, "Apron_bus_MAZ_doors (3)", "Apron_bus_MAZ_door_L_01_part_01", -0.129f, 0.729f, 2.152f) },
                                       moved, out unresolved, out posed);
        CheckTrue("bus-a-row-for-a-back-assembly-drops-it", moved.Count == 1 && moved[0] == assembly[5]);
        CheckReason("bus-other-back-assemblies-stay", bodyCollider[3], null, legacy, moved);
        Debug.Warnings.Clear();
    }

    static void Main()
    {
        // Numeric layer identity must work with the Editor's unnamed custom layers.
        // Ordinary floor/collision layers, including 12 and 18, must remain sources.
        for (int layer = 0; layer < 32; layer++)
            Check("owner-layer-" + layer, new Transform("Geometry", layer), layer == 13 || layer == 29);

        // A grouping parent's layer says nothing about its children's physical role.
        foreach (int parentLayer in new[] { 13, 29 })
            foreach (int floorLayer in new[] { 0, 12, 16, 18 })
                Check("solid-child-" + parentLayer + "-" + floorLayer,
                    new Transform("Floor_COLLIDER", floorLayer, new Transform("Group", parentLayer)), false);

        Check("stripped-spawn-box", new Transform("Bear house (9)", 13), true);
        Check("same-name-real-geometry", new Transform("Bear house (9)", 18), false);
        Check("spawn-bound", new Transform("Child", 18, new Transform("SPAWN_BOUND_FIGHTER", 0)), true);
        Check("named-border", new Transform("Cube", 18, new Transform("LevelBorder group", 0)), true);

        // KeepLevelBorders (Bowl, docs/PLAN-BOWL.md): a map that keeps its layer-29 border cubes bakes them as
        // physical sources; the Triggers rule and the name rules are unaffected.
        ArenaPaths.Map.keep_level_borders = true;
        Check("kept-border-layer-29", new Transform("Cube (301)", 29), false);
        Check("kept-border-triggers-still-dropped", new Transform("Marker", 13), true);
        Check("kept-border-named-prefix-still-dropped", new Transform("Cube", 18, new Transform("LevelBorder group", 0)), true);
        ArenaPaths.Map.keep_level_borders = false;
        Check("dropped-border-layer-29", new Transform("Cube (301)", 29), true);
        Check("sliding-door", new Transform("Door_Sliding_Respawn_L", 18), true);

        var barrier = new Transform("Gates_Depot_Frame", 0);
        var barriers = new List<Transform> { barrier };
        Check("opened-door", new Transform("Leaf_COLLIDER", 18, barrier), true, barriers);
        Check("retained-frame", new Transform("Gates_Depot_Frame_COLLIDER", 18, barrier), false, barriers);
        Check("unlisted-east-gate", new Transform("Leaf_COLLIDER", 18,
            new Transform("Gates_Depot_Frame", 0)), false, barriers);

        // Markups: Bay5's bruno_helix area overrides sit on Triggers-layer trigger boxes on purpose and
        // must survive; the name/opened-barrier rules still apply to them.
        CheckMarkup("markup-layer-13", new Transform("bruno_helix (47)", 13), false);
        CheckMarkup("markup-layer-29", new Transform("Marker", 29), false);
        CheckMarkup("markup-spawn-bound", new Transform("Marker", 13, new Transform("SPAWN_BOUND_FIGHTER", 0)), true);
        CheckMarkup("markup-opened-door", new Transform("Leaf_MARK", 13, barrier), true, barriers);
        CheckMarkup("markup-retained-frame", new Transform("Gates_Depot_Frame_MARK", 13, barrier), false, barriers);

        // PLAN-AIRPIT item 8, bake-only exclusions: the listed object and everything below it, frame parts included
        // (nothing is deactivated at runtime), siblings untouched, an empty list excludes nothing.
        var building = new Transform("BUILDING", 0);
        var cutter = new Transform("DonB_Ramen_closed", 0, building);
        var exclusions = new List<Transform> { cutter };
        var none = new List<Transform>();
        CheckSource("exclusion-object-itself", cutter, true, none, exclusions);
        CheckSource("exclusion-child-collider", new Transform("DonB_Ramen_closed_COLLIDER", 18, cutter), true, none, exclusions);
        CheckSource("exclusion-frame-named-child", new Transform("shop_frame_COLLIDER", 18, cutter), true, none, exclusions);
        CheckSource("exclusion-grandchild", new Transform("leaf", 12, new Transform("group", 0, cutter)), true, none, exclusions);
        CheckSource("exclusion-sibling", new Transform("DonB_Ramen_open", 18, building), false, none, exclusions);
        CheckSource("exclusion-parent-of-the-listed-object", building, false, none, exclusions);
        CheckSource("exclusion-empty-list", cutter, false, none, none);
        // a barrier and an exclusion side by side keep their different semantics
        var gate = new Transform("BUS_DOORS", 0);
        var gates = new List<Transform> { gate };
        CheckSource("barrier-object-itself-stays", gate, false, gates, exclusions);
        CheckSource("barrier-child-leaves", new Transform("Apron_bus_MAZ_doors", 18, gate), true, gates, exclusions);
        // markups follow the same rule (BakeNavMesh applies BakeOnlyExclusionReason to modifiers and volumes)
        if (BakeOnlyExclusionReason(new Transform("marker", 13, cutter), exclusions) == null)
            throw new Exception("markup-under-exclusion: expected an exclusion");
        checkedCases++;
        // Equator and Bay5 carry no exclusion: the combined expression equals the one that shipped before item 8
        // for every owner layer, with and without barriers.
        for (int layer = 0; layer < 32; layer++)
        {
            var owner = new Transform("Geometry", layer, new Transform("Group", 0));
            string before = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, none, none);
            string after = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, none, none) ?? BakeOnlyExclusionReason(owner, none);
            CheckEqual("no-exclusion-layer-" + layer, after, before);
        }

        // descriptor layer mask: no layers = the expression this method replaced, for every water layer index
        for (int water = -1; water < 32; water++)
        {
            int legacy = water >= 0 ? ~0 & ~(1 << water) : ~0;
            CheckEqual("mask-null-water-" + water, SourceLayerMask(null, water), legacy);
            CheckEqual("mask-empty-water-" + water, SourceLayerMask(new int[0], water), legacy);
        }
        CheckEqual("mask-arena", SourceLayerMask(new[] { 9, 11, 13, 18, 30 }, 4), 0x40042A00);
        CheckEqual("mask-arena-without-water-layer", SourceLayerMask(new[] { 9, 11, 13, 18, 30 }, -1), 0x40042A00);
        CheckEqual("mask-water-never-collected", SourceLayerMask(new[] { 4, 18 }, 4), 1 << 18);
        CheckEqual("mask-out-of-range-ignored", SourceLayerMask(new[] { 18, 32, -1 }, 4), 1 << 18);
        CheckEqual("mask-only-invalid", SourceLayerMask(new[] { 40 }, 4), 0);
        CheckEqual("mask-layer-31", SourceLayerMask(new[] { 31 }, 4), int.MinValue);

        // explicit bake volume: absent = the renderer union (false), well-formed = true, anything else throws
        CheckEqual("bounds-null", UsesExplicitBakeBounds(null), false);
        CheckEqual("bounds-empty", UsesExplicitBakeBounds(new float[0]), false);
        CheckEqual("bounds-airpit", UsesExplicitBakeBounds(new[] { -80f, 56f, -2f, 8f, -96f, 92f }), true);
        CheckThrows("bounds-five-numbers", new[] { -80f, 56f, -2f, 8f, -96f });
        CheckThrows("bounds-seven-numbers", new[] { -80f, 56f, -2f, 8f, -96f, 92f, 1f });
        CheckThrows("bounds-min-equals-max", new[] { -80f, 56f, 8f, 8f, -96f, 92f });
        CheckThrows("bounds-min-above-max", new[] { 56f, -80f, -2f, 8f, -96f, 92f });
        CheckThrows("bounds-nan", new[] { -80f, float.NaN, -2f, 8f, -96f, 92f });

        DoorPoseCases();
        PassportCases();
        RepeatedNameCases();
        BusDoorCases();
        Console.WriteLine("NAVMESH_SOURCE_FILTER_PASSED cases=" + checkedCases);
    }
}
"""


def run_filter(source, paths_source=None):
    # No production project references or deployment targets. Restore uses only
    # the locally installed SDK; the isolated project has no package dependencies.
    # LangVersion 9.0 is the Unity 2022.3 editor's language level: the production text may not use anything newer.
    if paths_source is None:
        paths_source = PATHS.read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory(prefix="arena-navmesh-filter-") as directory:
        folder = Path(directory)
        (folder / "Program.cs").write_text(HARNESS.replace("__PRODUCTION_FILTER__", filter_source(source, paths_source)), encoding="utf-8")
        project = folder / "Filter.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                           '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                           '<LangVersion>9.0</LangVersion><Nullable>disable</Nullable></PropertyGroup></Project>', encoding="utf-8")
        (folder / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        return subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"],
                              cwd=folder, capture_output=True, text=True, timeout=120)


# ---------------------------------------------------------------------------------------------------------------------
# The sidecar loader (ArenaPaths.ParseDoorRows): the production text of ArenaDoorRow, DoorSidecarPath and ParseDoorRows compiled against
# the .NET SDK's own Newtonsoft.Json.dll (standing in for the editor's), reading the real tools/maps/*.doors.json and malformed variants.
# Skipped when no SDK ships that assembly.
# ---------------------------------------------------------------------------------------------------------------------
LOADER_HARNESS = r"""
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

__ROW__

static class ArenaPaths
{
    __SIDECAR__
    __WARN__
    __PARSE__
}

static class Program
{
    static int failures;
    static void Expect(string label, bool ok) { if (!ok) { failures++; Console.WriteLine("FAIL " + label); } }
    static string Throws(string json)
    {
        try { ArenaPaths.ParseDoorRows(json, "test.json", "k"); return null; }
        catch (InvalidOperationException e) { return e.Message; }
    }
    static void Main(string[] args)
    {
        foreach (string file in args)
        {
            string key = Path.GetFileName(file).Replace(".doors.json", "");
            var json = File.ReadAllText(file);
            string mapKey = JObject.Parse(json)["key"].ToString();
            ArenaDoorRow[] rows = ArenaPaths.ParseDoorRows(json, file, mapKey);
            int sw = 0, withAt = 0, withClosed = 0;
            foreach (var r in rows) { if (r.is_switch) sw++; if (r.at != null) withAt++; if (r.closed_position != null) withClosed++; }
            Console.WriteLine(key + ": " + rows.Length + " row(s), " + sw + " switch, " + withAt + " with at, " + withClosed + " with closed_position; first = " + rows[0].barrier + " | " + rows[0].part + " | " + rows[0].node +
                              " | " + (rows[0].closed_position == null ? "null" : string.Join(",", rows[0].closed_position)));
        }
        string head = "{\"schema_version\":2,\"key\":\"k\",\"poses\":[";
        string tail = "]}";
        var ok = ArenaPaths.ParseDoorRows(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"closed\":{\"position\":[1,2.5,-3]},\"switch\":{\"closed\":true,\"open\":false}}" + tail, "t", "k");
        Expect("fields", ok.Length == 1 && ok[0].is_switch && ok[0].closed_position[0] == 1f && ok[0].closed_position[1] == 2.5f && ok[0].closed_position[2] == -3f && ok[0].at == null);
        var plain = ArenaPaths.ParseDoorRows(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"switch\":null}" + tail, "t", "k");
        Expect("absent closed and null switch", !plain[0].is_switch && plain[0].closed_position == null);
        var nullPos = ArenaPaths.ParseDoorRows(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"closed\":{\"position\":null}}" + tail, "t", "k");
        Expect("null closed.position", nullPos[0].closed_position == null);
        string e1 = Throws(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"closed\":{\"position\":[1,2]}}" + tail);
        Expect("two-number closed.position throws", e1 != null && e1.Contains("poses[0].closed.position must be three numbers") && e1.Contains("test.json"));
        string e2 = Throws(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"closed\":{\"position\":\"x\"}}" + tail);
        Expect("string closed.position throws", e2 != null);
        string e3 = Throws(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"closed\":{\"position\":[1,\"a\",3]}}" + tail);
        Expect("non-number closed.position throws", e3 != null);
        string e4 = Throws(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"switch\":\"yes\"}" + tail);
        Expect("string switch throws", e4 != null && e4.Contains("poses[0].switch must be an object or absent"));
        string e5 = Throws(head + "{\"barrier\":\"B\",\"part\":\"P\",\"node\":\"\",\"switch\":true}" + tail);
        Expect("bool switch throws", e5 != null);
        Expect("existing rule: missing node still throws", Throws(head + "{\"barrier\":\"B\",\"part\":\"P\"}" + tail) != null);
        Expect("existing rule: schema 1 throws", Throws("{\"schema_version\":1,\"key\":\"k\",\"poses\":[]}") != null);
        Expect("existing rule: wrong key throws", Throws("{\"schema_version\":2,\"key\":\"other\",\"poses\":[]}") != null);
        Expect("sidecar path", ArenaPaths.DoorSidecarPath("D:/x/airpit.json") == "D:/x/airpit.doors.json");
        // the missing-sidecar Warning (2026-10-24 review): only for a derivable path AND at least one opened barrier
        string warn = ArenaPaths.MissingDoorSidecarWarning("D:/x/airpit.doors.json", 12);
        Expect("missing sidecar with barriers warns", warn != null && warn.Contains("D:/x/airpit.doors.json") && warn.Contains("12 opened_barriers") && warn.Contains("LEGACY"));
        Expect("missing sidecar, no barrier: silent", ArenaPaths.MissingDoorSidecarWarning("D:/x/equator.doors.json", 0) == null);
        Expect("no sidecar path: silent", ArenaPaths.MissingDoorSidecarWarning(null, 12) == null && ArenaPaths.MissingDoorSidecarWarning("", 12) == null);
        Console.WriteLine(failures == 0 ? "LOADER_CHECKS_PASSED" : ("LOADER_CHECKS_FAILED " + failures));
    }
}
"""


def newtonsoft_dll():
    try:
        listing = subprocess.run(["dotnet", "--list-sdks"], capture_output=True, text=True, timeout=60).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    found = []
    for line in listing.splitlines():
        match = re.match(r"(\S+) \[(.+)\]", line.strip())
        if match and (Path(match.group(2)) / match.group(1) / "Newtonsoft.Json.dll").is_file():
            found.append(Path(match.group(2)) / match.group(1) / "Newtonsoft.Json.dll")
    return found[-1] if found else None


def run_loader(paths_source=None):
    if paths_source is None:
        paths_source = PATHS.read_text(encoding="utf-8")
    dll = newtonsoft_dll()
    program = (LOADER_HARNESS.replace("__ROW__", extract_function(paths_source, "public sealed class ArenaDoorRow"))
               .replace("__SIDECAR__", extract_function(paths_source, "public static string DoorSidecarPath("))
               .replace("__WARN__", extract_function(paths_source, "public static string MissingDoorSidecarWarning("))
               .replace("__PARSE__", extract_function(paths_source, "public static ArenaDoorRow[] ParseDoorRows(")))
    with tempfile.TemporaryDirectory(prefix="arena-doorloader-") as directory:
        folder = Path(directory)
        (folder / "Program.cs").write_text(program, encoding="utf-8")
        (folder / "Loader.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<LangVersion>9.0</LangVersion><Nullable>disable</Nullable></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json">'
            '<HintPath>' + dll.as_posix() + '</HintPath></Reference></ItemGroup></Project>', encoding="utf-8")
        (folder / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        sidecars = sorted(str(p) for p in (REPO / "tools" / "maps").glob("*.doors.json"))
        return subprocess.run(["dotnet", "run", "--project", str(folder / "Loader.csproj"), "-c", "Release", "--"] + sidecars,
                              cwd=folder, capture_output=True, text=True, timeout=180)


class NavMeshSourceLayerTests(unittest.TestCase):
    def test_numeric_layers_preserve_solid_children_and_existing_barrier_rules(self):
        result = run_filter(BAKE.read_text(encoding="utf-8"))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        # 292 before the AirPit rows of 2026-10-24 (repeated leaf names, switch rows, node rows of unique assemblies)
        self.assertIn("NAVMESH_SOURCE_FILTER_PASSED cases=429", result.stdout)

    def test_bake_composes_the_item8_inputs_in_the_documented_order(self):
        # The harness runs the production helper functions; this pins how Bake() wires them, so that the
        # Equator/Bay5 path (no exclusion, no layers, no bounds) stays the pre-item-8 expression.
        source = BAKE.read_text(encoding="utf-8")
        self.assertIn("NonPhysicalLayerReason(t) ?? RoundOpenedReason(t, openedBarriers, movedSubtrees) ?? BakeOnlyExclusionReason(t, bakeExclusions)", source)
        self.assertIn("RoundOpenedReason(m.transform, openedBarriers, movedSubtrees) ?? BakeOnlyExclusionReason(m.transform, bakeExclusions)", source)
        self.assertEqual(source.count("BakeOnlyExclusionReason(v.transform, bakeExclusions)"), 1)
        self.assertEqual(source.count("RoundOpenedReason(v.transform, openedBarriers, movedSubtrees) != null"), 1)
        # every call site passes the moved subtrees: no two-argument call is left behind
        self.assertEqual(source.count("RoundOpenedReason("), 4)                          # three call sites and the definition
        self.assertEqual(source.count(", openedBarriers, movedSubtrees)"), 3)
        self.assertIn("SourceLayerMask(bakeLayers, waterLayer)", source)
        self.assertIn("UsesExplicitBakeBounds(bakeBounds)", source)
        # the renderer-union volume (with its 10 m expansion and fallback box) is untouched, now in the else branch
        self.assertEqual(source.count("bounds.Expand(10f)"), 1)
        self.assertIn("new Bounds(Vector3.zero, new Vector3(200f, 50f, 200f))", source)
        # one resolver and one failure helper for both path lists: an unresolved barrier or exclusion fails the
        # build (review fix 2026-10-11; a barrier used to only warn, which re-sealed its pocket silently)
        self.assertIn("ResolveHierarchyPaths(geometryRoots, ArenaPaths.Map.navmesh_bake_exclusions, out unresolvedExclusions)", source)
        self.assertIn('FailOnUnresolvedPaths("opened_barriers", unresolvedBarriers);', source)
        self.assertIn('FailOnUnresolvedPaths("navmesh_bake_exclusions", unresolvedExclusions);', source)
        self.assertEqual(source.count("FailOnUnresolvedPaths("), 3)           # two calls plus the definition
        self.assertIn('" path(s) not found in the content scenes"', source)
        self.assertIn("throw new InvalidOperationException", source[source.index("static void FailOnUnresolvedPaths"):])
        self.assertNotIn("LogWarning(\"[ArenaMapPort][BakeNavMesh] opened_barriers", source.replace("Debug.LogWarning(\"[ArenaMapPort][BakeNavMesh] opened_barriers path '", ""))

    def test_bake_reads_the_door_rows_and_fails_on_rows_that_resolve_to_nothing(self):
        # 2026-10-23: opened_barriers is resolved together with the rows of tools/maps/<stem>.doors.json. A barrier with rows
        # drops only its moved subtrees; the pins keep the wiring honest where the harness cannot see it (Bake() itself
        # needs Unity).
        source = BAKE.read_text(encoding="utf-8")
        self.assertIn("ResolveOpenedBarriers(geometryRoots, ArenaPaths.Map.opened_barriers, ArenaPaths.DoorRows,", source)
        self.assertIn("movedSubtrees, out unresolvedBarriers, out posedBarriers)", source)
        self.assertEqual(source.count("ResolveOpenedBarriers("), 2)             # the call and the definition
        # unresolved rows and paths fail the build through the same helper and key as before
        self.assertLess(source.index("ResolveOpenedBarriers(geometryRoots"), source.index('FailOnUnresolvedPaths("opened_barriers", unresolvedBarriers);'))
        # the log names the rule: "none" for a map without barriers (Equator), otherwise the moved subtrees
        self.assertIn("opened_barriers: none;", source)
        self.assertIn("is baked as a wall", source)
        self.assertIn('" moved subtree(s) ["', source)
        self.assertIn('" with door poses: " + movedSubtrees.Count + " moved subtree(s))"', source)
        # the reason label of a moved subtree, one line per moved part in the per-reason counts
        self.assertIn('return "door_poses:" + moved.name;', source)
        # the booth leaves keep their name rule (their assembly roots are not opened_barriers entries)
        self.assertIn('{ "Door_Sliding_Respawn_L", "Door_Sliding_Respawn_R" }', source)
        # the tolerance is the descriptor's (ArenaDoorPose.AtTolerance)
        shared = (REPO / "ArenaMapPort.Shared" / "ArenaMapDescriptor.cs").read_text(encoding="utf-8")
        self.assertRegex(shared, r"public const float AtTolerance = 0\.05f;")
        self.assertIn("const float DoorAtTolerance = 0.05f;", source)
        # 2026-10-24 (AirPit): same-named leaves are told apart by the closed position, with the descriptor's tolerance; the resolver goes
        # through PartsOfRow and skips switch rows; the log names the switch rows once
        self.assertRegex(shared, r"public const float ClosedPositionTolerance = 0\.05f;")
        self.assertIn("const float DoorClosedPositionTolerance = 0.05f;", source)
        self.assertIn("List<Transform> parts = PartsOfRow(instance, row);", source)
        self.assertEqual(source.count("PartsOfRow("), 2)                        # the call and the definition
        self.assertIn("if (row.is_switch) continue;", source)
        self.assertIn("if (!rowUsed[i] && !pathRows[i].is_switch)", source)
        self.assertIn('"[ArenaMapPort][BakeNavMesh] door pose switch rows: "', source)

    def test_the_paths_loader_reads_the_rows_next_to_the_descriptor(self):
        # ArenaPaths cannot run here (Unity + Newtonsoft); pin its contract as text. The class the harness compiles is the
        # production one, so the field names the bake reads cannot drift.
        paths = PATHS.read_text(encoding="utf-8")
        for fragment in ("public sealed class ArenaDoorRow", "public string barrier;", "public string part;", "public string node;", "public float[] at;",
                         "public float[] closed_position;", "public bool is_switch;",
                         "closed_position = closedPosition, is_switch = isSwitch", "].closed.position must be three numbers", "].switch must be an object or absent",
                         "public static ArenaDoorRow[] DoorRows", "public static string DoorSidecarPath(string descriptorPath)",
                         'descriptorPath.EndsWith(".doors.json"', 'return descriptorPath.Substring(0, descriptorPath.Length - json.Length) + ".doors.json";',
                         "public static ArenaDoorRow[] ParseDoorRows(string json, string origin, string expectedKey)",
                         "schema_version must be 2", "is not the descriptor's", "ParseDoorRows(File.ReadAllText(path), path, Map.key)",
                         # 2026-10-24 review: a sidecar absent from the tree is a Warning, never a silent legacy-rule bake
                         "public static string MissingDoorSidecarWarning(string sidecarPath, int openedBarrierCount)",
                         "MissingDoorSidecarWarning(path, path != null && Map.opened_barriers != null ? Map.opened_barriers.Length : 0)",
                         'Debug.LogWarning("[ArenaMapPort][ArenaPaths] " + missing);'):
            self.assertIn(fragment, paths)
        # the Warning sits in the branch of a sidecar that was not found, before the empty array is returned
        branch = paths[paths.index("if (path == null || !File.Exists(path))"):]
        self.assertLess(branch.index("MissingDoorSidecarWarning("), branch.index("_doorRows = new ArenaDoorRow[0];"))
        # Equator's built-in default lists no opened barrier any more (user decision 2026-10-23), like tools/maps/equator.json
        default = paths[paths.index("static ArenaMapDescriptorData EquatorDefault()"):]
        initializer = re.sub(r"//[^\r\n]*", "", default.split("};")[0])                 # code only, the comment explains the omission
        self.assertNotIn("opened_barriers", initializer)
        self.assertNotIn("equator_roller_shutters_02", initializer)
        self.assertIn('infiltration_name = "ArenaEquator"', initializer)

    @unittest.skipUnless(newtonsoft_dll() is not None, "no .NET SDK with Newtonsoft.Json.dll")
    def test_the_loader_reads_the_four_real_sidecars_and_the_airpit_fields(self):
        # 2026-10-24: closed_position and is_switch are new. All four sidecars parse (airpit 30 rows / 2 switch, bay5 7, bowl 19, equator 10),
        # a malformed closed.position or switch throws with a message naming the file and the pose, and the older rules still hold.
        result = run_loader()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("LOADER_CHECKS_PASSED", result.stdout)
        for line in ("airpit: 30 row(s), 2 switch, 0 with at, 30 with closed_position", "bay5: 7 row(s), 0 switch, 4 with at, 7 with closed_position",
                     "bowl: 19 row(s), 0 switch, 0 with at, 19 with closed_position", "equator: 10 row(s), 0 switch, 10 with at, 10 with closed_position"):
            self.assertIn(line, result.stdout)

    @unittest.skipUnless(newtonsoft_dll() is not None, "no .NET SDK with Newtonsoft.Json.dll")
    def test_a_loader_that_ignores_the_switch_flag_fails_the_loader_checks(self):
        paths = PATHS.read_text(encoding="utf-8")
        paths, replacements = re.subn(r"isSwitch = true;", "isSwitch = false;", paths)
        self.assertEqual(replacements, 1)
        result = run_loader(paths)
        self.assertIn("FAIL fields", result.stdout + result.stderr)
        self.assertNotIn("LOADER_CHECKS_PASSED", result.stdout)

    @unittest.skipUnless(newtonsoft_dll() is not None, "no .NET SDK with Newtonsoft.Json.dll")
    def test_a_loader_that_never_warns_about_a_missing_sidecar_fails_the_loader_checks(self):
        paths = PATHS.read_text(encoding="utf-8")
        paths, replacements = re.subn(r"if \(string\.IsNullOrEmpty\(sidecarPath\) \|\| openedBarrierCount <= 0\) return null;",
                                      "return null;", paths)
        self.assertEqual(replacements, 1)
        result = run_loader(paths)
        self.assertIn("FAIL missing sidecar with barriers warns", result.stdout + result.stderr)
        self.assertNotIn("LOADER_CHECKS_PASSED", result.stdout)
        # and one that warns for a map without barriers or without a path is just as wrong
        paths = PATHS.read_text(encoding="utf-8")
        paths, replacements = re.subn(r"if \(string\.IsNullOrEmpty\(sidecarPath\) \|\| openedBarrierCount <= 0\) return null;", "", paths)
        self.assertEqual(replacements, 1)
        result = run_loader(paths)
        self.assertIn("FAIL missing sidecar, no barrier: silent", result.stdout + result.stderr)

    def test_missing_trigger_exclusion_fails_the_spawn_layer_assertion(self):
        source = BAKE.read_text(encoding="utf-8")
        source, replacements = re.subn(r'if\s*\(t\.gameObject\.layer\s*==\s*13\)\s*return\s*"[^"]*";', "", source)
        self.assertEqual(replacements, 1, "Negative control must remove the real layer-13 filter")
        result = run_filter(source)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("owner-layer-13: unexpected exclusion=", result.stdout + result.stderr)
        self.assertNotIn("NAVMESH_SOURCE_FILTER_PASSED", result.stdout)

    def test_missing_moved_subtree_rule_fails_the_door_pose_assertions(self):
        # Negative control: without the moved-subtree loop a posed leaf stays a NavMesh source.
        source = BAKE.read_text(encoding="utf-8")
        source, replacements = re.subn(r'foreach \(Transform moved in movedSubtrees\)\s*if \(t\.IsChildOf\(moved\)\) return "door_poses:" \+ moved\.name;', "", source)
        self.assertEqual(replacements, 1, "Negative control must remove the real moved-subtree rule")
        result = run_filter(source)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("depot-door-part-itself", result.stdout + result.stderr)
        self.assertNotIn("NAVMESH_SOURCE_FILTER_PASSED", result.stdout)

    def test_an_at_match_that_accepts_every_position_fails_the_assertions(self):
        # Negative control: if a row's 'at' did not tell instances apart, the second depot gate's rows would also move the first.
        source = BAKE.read_text(encoding="utf-8")
        source, replacements = re.subn(r"if \(row\.at == null\) return true;", "return true;", source)
        self.assertEqual(replacements, 1)
        result = run_filter(source)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn("NAVMESH_SOURCE_FILTER_PASSED", result.stdout)

    def negative_control(self, pattern, replacement, expected_case):
        # Mutating the production text must turn the harness red, at the named case.
        source = BAKE.read_text(encoding="utf-8")
        source, replacements = re.subn(pattern, replacement, source)
        self.assertEqual(replacements, 1, "Negative control must change the real text: " + pattern)
        result = run_filter(source)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(expected_case, result.stdout + result.stderr)
        self.assertNotIn("NAVMESH_SOURCE_FILTER_PASSED", result.stdout)

    def test_a_switch_row_that_drops_its_part_fails_the_assertions(self):
        # Without the exemption RED and GREEN would be moved parts: the passport's 18 children go instead of its 16 leaves.
        self.negative_control(r"if \(row\.is_switch\) continue;", "", "passport-moved-count")

    def test_a_closed_position_match_that_accepts_every_sibling_fails_the_assertions(self):
        self.negative_control(r"if \(ClosedPositionMatches\(child, row\)\) matching\.Add\(child\);", "matching.Add(child);",
                              "passport-row-0-moves-its-own-leaf")

    def test_taking_only_the_first_same_named_child_fails_the_assertions(self):
        # The Transform.Find behaviour the rows replace: every leaf row would move the first leaf of its name.
        self.negative_control(r"if \(named\.Count <= 1 \|\| row\.closed_position == null\)", "if (true)", "passport-no-warning")   # 10 leaves no row reaches are reported

    def test_comparing_the_part_instead_of_the_node_fails_the_assertions(self):
        # A node row's closed.position is the node's local position (a bus hinge inside its assembly), not the part's.
        self.negative_control(r"string\.IsNullOrEmpty\(row\.node\) \? child : child\.Find\(row\.node\)", "child", "node-row-moves-one-twin")

    def test_a_switch_row_that_can_fail_the_build_fails_the_assertions(self):
        self.negative_control(r"if \(!rowUsed\[i\] && !pathRows\[i\]\.is_switch\)", "if (!rowUsed[i])",
                              "switch-missing-part-and-stale-at-unresolved-empty")


if __name__ == "__main__":
    unittest.main()
