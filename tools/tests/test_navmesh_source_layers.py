"""Execute the Editor's source filter without loading Unity or rebuilding bundles."""
import re
from pathlib import Path
import subprocess
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[2]
BAKE = REPO / "UnityProject/Assets/ArenaMapPort/Editor/NavMesh/BakeNavMesh.cs"


def filter_source(source):
    start = source.index("static string RoundOpenedReason(")
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        if end >= len(source):
            raise ValueError("Unterminated RoundOpenedReason")
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    arrays = []
    for name in ("ExcludedNamePrefixes", "ExcludedLeafNames"):
        matches = re.findall(r"static readonly string\[\]\s+" + name + r"\s*=\s*\{[^}]*\};", source)
        if len(matches) != 1:
            raise ValueError(f"Expected one {name} declaration")
        arrays.append(matches[0])
    return "\n".join(arrays) + "\n" + source[start:end]


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

static class Program
{
    __PRODUCTION_FILTER__
    static int checkedCases;
    static void Check(string label, Transform owner, bool excluded, List<Transform> barriers = null)
    {
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
        Check("sliding-door", new Transform("Door_Sliding_Respawn_L", 18), true);

        var barrier = new Transform("Gates_Depot_Frame", 0);
        var barriers = new List<Transform> { barrier };
        Check("opened-door", new Transform("Leaf_COLLIDER", 18, barrier), true, barriers);
        Check("retained-frame", new Transform("Gates_Depot_Frame_COLLIDER", 18, barrier), false, barriers);
        Check("unlisted-east-gate", new Transform("Leaf_COLLIDER", 18,
            new Transform("Gates_Depot_Frame", 0)), false, barriers);
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
        self.assertIn("NAVMESH_SOURCE_FILTER_PASSED cases=48", result.stdout)

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
