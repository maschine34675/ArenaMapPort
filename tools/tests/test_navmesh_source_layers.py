"""Execute the Editor's source filter without loading Unity or rebuilding bundles."""
import re
from pathlib import Path
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
BAKE = REPO / "UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs"


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


def filter_source(source):
    # PLAN-TWO-MAPS item 4 (2026-09-25): the layer rule (13 Triggers / 29 LevelBorder) lives in
    # NonPhysicalLayerReason and applies to collider sources only; RoundOpenedReason (names, opened
    # barriers) applies to sources AND to NavMeshModifier/Volume markups. The harness mirrors the
    # collider loop's `NonPhysicalLayerReason(t) ?? RoundOpenedReason(t, openedBarriers)`.
    # PLAN-AIRPIT item 8 added the bake-only exclusion, the descriptor layer mask and the explicit-bounds check;
    # all three are pure (no Unity types), so the harness runs the production text.
    functions = "\n".join(extract_function(source, sig) for sig in
                          ("static string NonPhysicalLayerReason(", "static string RoundOpenedReason(",
                           "static string BakeOnlyExclusionReason(", "static int SourceLayerMask(",
                           "static bool UsesExplicitBakeBounds("))
    arrays = []
    for name in ("ExcludedNamePrefixes", "ExcludedLeafNames"):
        matches = re.findall(r"static readonly string\[\]\s+" + name + r"\s*=\s*\{[^}]*\};", source)
        if len(matches) != 1:
            raise ValueError(f"Expected one {name} declaration")
        arrays.append(matches[0])
    return "\n".join(arrays) + "\n" + functions


HARNESS = r"""
using System;
using System.Collections.Generic;

sealed class GameObject { public int layer; }
sealed class Transform
{
    public string name;
    public Transform parent;
    public GameObject gameObject = new GameObject();
    public Transform(string name, int layer, Transform parent = null)
    { this.name = name; gameObject.layer = layer; this.parent = parent; }
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
        string reason = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, barriers ?? new List<Transform>());
        if ((reason != null) != excluded)
            throw new Exception(label + ": unexpected exclusion=" + reason);
        checkedCases++;
    }
    static void CheckSource(string label, Transform owner, bool excluded, List<Transform> barriers, List<Transform> exclusions)
    {
        // the exact composition of BakeNavMesh's collider-source loop (a test below pins the production text)
        string reason = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, barriers) ?? BakeOnlyExclusionReason(owner, exclusions);
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
    static void CheckThrows(string label, float[] bounds)
    {
        try { UsesExplicitBakeBounds(bounds); }
        catch (InvalidOperationException) { checkedCases++; return; }
        throw new Exception(label + ": expected InvalidOperationException");
    }
    static void CheckMarkup(string label, Transform owner, bool excluded, List<Transform> barriers = null)
    {
        // NavMeshModifier/Volume semantics: round-open rules only, never the layer rule
        string reason = RoundOpenedReason(owner, barriers ?? new List<Transform>());
        if ((reason != null) != excluded)
            throw new Exception(label + ": unexpected exclusion=" + reason);
        checkedCases++;
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
            string before = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, none);
            string after = NonPhysicalLayerReason(owner) ?? RoundOpenedReason(owner, none) ?? BakeOnlyExclusionReason(owner, none);
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
        Console.WriteLine("NAVMESH_SOURCE_FILTER_PASSED cases=" + checkedCases);
    }
}
"""


def run_filter(source):
    # No production project references or deployment targets. Restore uses only
    # the locally installed SDK; the isolated project has no package dependencies.
    with tempfile.TemporaryDirectory(prefix="arena-navmesh-filter-") as directory:
        folder = Path(directory)
        (folder / "Program.cs").write_text(HARNESS.replace("__PRODUCTION_FILTER__", filter_source(source)), encoding="utf-8")
        project = folder / "Filter.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                           '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                           '<Nullable>disable</Nullable></PropertyGroup></Project>', encoding="utf-8")
        (folder / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        return subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"],
                              cwd=folder, capture_output=True, text=True, timeout=60)


class NavMeshSourceLayerTests(unittest.TestCase):
    def test_numeric_layers_preserve_solid_children_and_existing_barrier_rules(self):
        result = run_filter(BAKE.read_text(encoding="utf-8"))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("NAVMESH_SOURCE_FILTER_PASSED cases=179", result.stdout)

    def test_bake_composes_the_item8_inputs_in_the_documented_order(self):
        # The harness runs the production helper functions; this pins how Bake() wires them, so that the
        # Equator/Bay5 path (no exclusion, no layers, no bounds) stays the pre-item-8 expression.
        source = BAKE.read_text(encoding="utf-8")
        self.assertIn("NonPhysicalLayerReason(t) ?? RoundOpenedReason(t, openedBarriers) ?? BakeOnlyExclusionReason(t, bakeExclusions)", source)
        self.assertIn("RoundOpenedReason(m.transform, openedBarriers) ?? BakeOnlyExclusionReason(m.transform, bakeExclusions)", source)
        self.assertEqual(source.count("BakeOnlyExclusionReason(v.transform, bakeExclusions)"), 1)
        self.assertIn("SourceLayerMask(bakeLayers, waterLayer)", source)
        self.assertIn("UsesExplicitBakeBounds(bakeBounds)", source)
        # the renderer-union volume (with its 10 m expansion and fallback box) is untouched, now in the else branch
        self.assertEqual(source.count("bounds.Expand(10f)"), 1)
        self.assertIn("new Bounds(Vector3.zero, new Vector3(200f, 50f, 200f))", source)
        # one resolver and one failure helper for both path lists: an unresolved barrier or exclusion fails the
        # build (review fix 2026-10-11; a barrier used to only warn, which re-sealed its pocket silently)
        self.assertIn("ResolveHierarchyPaths(geometryRoots, ArenaPaths.Map.opened_barriers, out unresolvedBarriers)", source)
        self.assertIn("ResolveHierarchyPaths(geometryRoots, ArenaPaths.Map.navmesh_bake_exclusions, out unresolvedExclusions)", source)
        self.assertIn('FailOnUnresolvedPaths("opened_barriers", unresolvedBarriers);', source)
        self.assertIn('FailOnUnresolvedPaths("navmesh_bake_exclusions", unresolvedExclusions);', source)
        self.assertEqual(source.count("FailOnUnresolvedPaths("), 3)           # two calls plus the definition
        self.assertIn('" path(s) not found in the content scenes"', source)
        self.assertIn("throw new InvalidOperationException", source[source.index("static void FailOnUnresolvedPaths"):])
        self.assertNotIn("LogWarning(\"[ArenaMapPort][BakeNavMesh] opened_barriers", source)

    def test_missing_trigger_exclusion_fails_the_spawn_layer_assertion(self):
        source = BAKE.read_text(encoding="utf-8")
        source, replacements = re.subn(r'if\s*\(t\.gameObject\.layer\s*==\s*13\)\s*return\s*"[^"]*";', "", source)
        self.assertEqual(replacements, 1, "Negative control must remove the real layer-13 filter")
        result = run_filter(source)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("owner-layer-13: unexpected exclusion=", result.stdout + result.stderr)
        self.assertNotIn("NAVMESH_SOURCE_FILTER_PASSED", result.stdout)


if __name__ == "__main__":
    unittest.main()
