"""The new-map checklist (PLAN-YARD item 2, 2026-11-20; S4 survey, risk 14 "silent defaults").

A map joins the pipeline through about thirty hand-kept per-map tables spread over Python extractors, validators, the client and the
server (C# switches and arrays, two csproj files, two PowerShell scripts, the Unity importer). Several of them fail silently for a map
that has no row: `MapLightingDefaults.Rig` hands an unlisted map the neutral donor values and a ZeroLevel of 0 without an error. This test
walks every map of the released registry and fails with ONE readable list of every table row that map lacks, so a promotion edit that
forgets a table is caught here and not on the host.

Why Python and not xunit: most tables are Python (the extractors and validators), the C# tables are plain `case "arena_x":` switches,
arrays and csproj items that a regex reads the way the existing source-pin tests do (test_crowdless_maps reads ArenaSpectators.cs the
same way), and there is no C# registry that the xunit side could enumerate for the Python tables. The registry is read from
ArenaMapDescriptor.cs (`ArenaMapRegistry.All`), the same array registry_membership_errors parses.

What counts as a decision. Some features are optional per map (a crowd, flares, native water, an overview, an official description, stand-ins).
For those the map must be in the has-list that the real readers use OR in the written does-not-have list below: a new map therefore
cannot be forgotten, it can only be decided, and deciding is an edit of this file in the same commit as the promotion. The does-not-have
lists are NOT an exemption mechanism for a missing row: a map in a has-list still needs every row of that feature.

What this test cannot see (honest limits):
  * it checks that a row EXISTS, never that its numbers are right (ZeroLevel, ambient mix, bounds, pins of the native contract);
  * the five client foreach arrays are read by their text (the `ArenaMapRegistry.<Name>` tokens on the foreach line), not run;
  * prose, config descriptions and comments that spell out the map names are not checked (a grep-based "names the registry maps" check is NOT built, decided at the item 15 fix pass 2026-11-21:
    history comments name maps on purpose and would make it noisy; the sweep is manual);
  * count pins that go from six to seven live in the tests that own them and fail there;
  * the Unity editor side is read only for the audio importer (the one per-map switch found there);
  * tables NOT covered (a new map still needs them by hand; PLAN-YARD item 15 lists them): extract_ai_anchors bounds and born/mine pins,
    extract_mode_objects TEAM_BOUND_RULES / SIDE_BOUND_RULES (optional layout rules), tools/extract/gate_sound_fit.json, the donor rows of
    spawn_coverage.py, the selection markers.json source_key row, extract_spatial_audio REVIEWED_MIRRORED_VOLUMES and repair rows,
    ShaderStandinPolicy's UsesExtendedStandins and target tables, and the completeness of the descriptor's own fields (a Catalog row has far
    fewer fields than a released map's);
  * Python tables keyed by the sidecar stem assume stem == key without the `arena_` prefix (true for all six maps; the test checks it).
"""
import json
import re
import sys
import unittest
from pathlib import Path
from typing import NamedTuple

REPO = Path(__file__).resolve().parents[2]
for sub in ("validate", "release", "extract", "bundle", "dynamicmaps"):
    sys.path.insert(0, str(REPO / "tools" / sub))

import validate_map_descriptors as VD  # noqa: E402

# ---------------------------------------------------------------------------------------------------------------------------------
# Written decisions. Each entry is a fact about a released map that this test cannot derive; changing one is a deliberate edit.
# ---------------------------------------------------------------------------------------------------------------------------------
# Optional features: the map is in the real has-list, or here.
NO_FLARES = frozenset({"arena_airpit", "arena_iceberg", "arena_yard"})              # no active flare lamps in the play space (CROWDLESS-style decision list)
NO_WATER = frozenset({"arena_equator", "arena_airpit", "arena_bowl", "arena_autoservice", "arena_yard"})   # no sea mesh (NATIVE_WATER_MAP_KEYS is the has-list)
NO_OVERVIEW = frozenset({"arena_yard"})                                  # Yard has no DynamicMaps overview (plan: none); the other six ship one
NO_OFFICIAL_DESCRIPTION = frozenset({"arena_iceberg"})                  # Arena's locales carry none; the port's own LocaleDescription is used
# Maps that take an older path than the table the checklist asks about (their rows predate the table, listed so that the exemption is visible).
LEGACY_AMBIENT_EXPORT = frozenset({"arena_equator", "arena_bay5"})      # native_features.AUDIO_EXPORTERS: ambient audio through Equator's / Bay5's own driver
LEGACY_NO_SOURCE_PLAN = frozenset({"arena_equator", "arena_bay5"})      # extracted by extract_equator.py / extract_bay5.py, no tools/extract/plans/<stem>.json
LEGACY_NAVMESH_DIFF = frozenset({"arena_equator", "arena_bay5"})        # navmesh_diff.ARENA_SOURCES has no row for them (the oracle predates the table)
# Sidecars every released map has (tools/maps/<stem>.<part>.json).
SIDECAR_PARTS = ("", ".modes", ".ai", ".bots", ".exits", ".doors", ".gatesounds", ".monitors")
PLAN_ROLES = ("scripts", "sound", "ai", "culling")


class Spec(NamedTuple):
    name: str   # the ArenaMapRegistry property name, which is also the player-facing registry name (AutoService)
    key: str
    stem: str


def strip_comments(text: str) -> str:
    return VD.strip_line_comments(text)


def read(rel: str) -> str:
    return (REPO / rel).read_text(encoding="utf-8")


def method_body(text: str, header: str) -> str:
    """Text from the first match of `header` up to the next member of the same indentation (a switch table of one method)."""
    match = re.search(header, text)
    if not match:
        return ""
    rest = text[match.end():]
    end = re.search(r"\n        (?:internal |public |private |static |/// )", rest)
    return rest[:end.start()] if end else rest


def released_specs() -> list[Spec]:
    """ArenaMapRegistry.All of ArenaMapDescriptor.cs, name -> key -> stem."""
    text = strip_comments(read("ArenaMapPort.Shared/ArenaMapDescriptor.cs"))
    match = re.search(r"IReadOnlyList<ArenaMapDescriptor>\s+All\s*=\s*new\[\]\s*\{([^}]+)\}", text, re.S)
    if not match:
        raise AssertionError("ArenaMapRegistry.All could not be parsed")
    specs = []
    for name in [part.strip() for part in match.group(1).split(",") if part.strip()]:
        key = "arena_" + name.lower()
        specs.append(Spec(name, key, name.lower()))
    return specs


class Inputs:
    """Every table, read once. Python tables are imported, C# and project files are text."""

    def __init__(self):
        import extract_spectator, native_features, extract_door_poses, extract_gate_sounds, extract_multiflare
        import extract_score_monitors, extract_loadscreens, extract_banners, extract_map_descriptions, navmesh_diff
        import validate_distribution
        self.spectator_scenes = {f"arena_{n}" for n in extract_spectator.MAP_SCENES}
        self.audio_exporters = set(native_features.AUDIO_EXPORTERS)
        self.door_sources = set(extract_door_poses.MAP_SOURCES) | set(extract_door_poses.DOT_SOURCES)
        self.dot_sources = set(extract_door_poses.DOT_SOURCES)
        self.clip_sources = set(extract_door_poses.MAP_SOURCES)
        self.gate_sounds = set(extract_gate_sounds.MAPS)
        self.multiflare = set(extract_multiflare.SCENES) | set(extract_multiflare.EXPECTED_CENSUS)
        self.monitors = set(extract_score_monitors.MAPS)
        self.loadscreens = set(extract_loadscreens.LOADSCREENS)
        self.banners = set(extract_banners.BANNERS)
        self.description_keys = set(extract_map_descriptions.DESCRIPTION_KEYS)
        self.navmesh_sources = {f"arena_{s}" for s in navmesh_diff.ARENA_SOURCES}
        self.overview_names = set(validate_distribution.OVERVIEW_MAP_NAMES)
        self.flare_keys = set(validate_distribution.FLARE_MAP_KEYS)
        self.crowdless = set(VD.CROWDLESS_MAP_KEYS)
        self.water = set(VD.NATIVE_WATER_MAP_KEYS)
        self.release_names = set(VD.RELEASE_MAP_NAMES)
        self.release_keys = set(VD.RELEASE_MAP_KEYS)
        # read by AST, not imported: the exporter needs shapely and other packages the checklist must not depend on
        import ast
        tree = ast.parse(read("tools/dynamicmaps/export_equator.py"))
        self.overview_profiles = next(set(ast.literal_eval(n.value)) for n in tree.body
                                      if isinstance(n, ast.Assign) and any(getattr(x, "id", "") == "MAP_PROFILES" for x in n.targets))
        self.key_sources = set(json.loads(read("tools/bundle/key-sources.json"))["maps"])
        self.client_csproj = read("ArenaMapPort.Client/ArenaMapPort.Client.csproj")
        self.server_csproj = read("ArenaMapPort.Server/ArenaMapPort.Server.csproj")
        self.profiles = strip_comments(read("ArenaMapPort.Client/AmbientAudioProfiles.cs"))
        self.defaults = strip_comments(read("ArenaMapPort.Client/AmbientAudioDefaults.cs"))
        self.lighting = strip_comments(read("ArenaMapPort.Client/MapLightingDefaults.cs"))
        self.policy = strip_comments(read("ArenaMapPort.Client/ShaderStandinPolicy.cs"))
        self.standins = strip_comments(read("ArenaMapPort.Client/ShaderStandins.cs"))
        self.spectators = strip_comments(read("ArenaMapPort.Client/ArenaSpectators.cs"))
        self.dynamic_assets = strip_comments(read("ArenaMapPort.Client/DynamicMapAssets.cs"))
        self.banners_cs = strip_comments(read("ArenaMapPort.Server/ArenaMapBanners.cs"))
        self.descriptions_cs = strip_comments(read("ArenaMapPort.Server/ArenaMapDescriptions.cs"))
        self.audio_importer = strip_comments(read("UnityProject/Assets/ArenaMapPort/Editor/Import/Bay5AudioImporter.cs"))
        self.deploy = read("tools/deploy-to-spt.ps1")
        self.park = read("tools/park-unreleased-dist.ps1")
        self.arrays = {f: strip_comments(read(f"ArenaMapPort.Client/{f}.cs"))
                       for f in ("NativeDecalRepairs", "NativeSpatialAudio", "PrecipitationAudio", "RoomReverb", "NativeMultiFlare")}


def foreach_line(text: str) -> str:
    match = re.search(r"foreach \(var map in new\[\] \{([^}]*)\}\)", text)
    return match.group(1) if match else ""


def decide(spec: Spec, label: str, has: bool, lacks: bool, where: str, out: list[str]) -> bool:
    """A feature is a decision: in the has-list xor in the written does-not-have list. Returns whether the map has it."""
    if has and lacks:
        out.append(f"{label}: in BOTH the has-list ({where}) and the written does-not-have list of this test")
    elif not has and not lacks:
        out.append(f"{label}: in neither the has-list ({where}) nor the written does-not-have list of this test: decide, and add the map to one of them")
    return has


def checklist(spec: Spec, t: Inputs) -> list[str]:
    """Every per-map table row `spec` lacks, one readable line each ('<table>: <what is missing> (<where it goes>)')."""
    out: list[str] = []
    key, name, stem = spec.key, spec.name, spec.stem

    def need(condition: bool, line: str):
        if not condition:
            out.append(line)

    # --- identity, sidecars, contract, plan -------------------------------------------------------------------------------------
    for part in SIDECAR_PARTS:
        rel = f"tools/maps/{stem}{part}.json"
        need((REPO / rel).is_file(), f"sidecar: {rel} is missing")
    sidecar = REPO / f"tools/maps/{stem}.json"
    if sidecar.is_file():
        need(json.loads(sidecar.read_text(encoding="utf-8")).get("key") == key, f"sidecar: tools/maps/{stem}.json does not carry key {key!r} (stem must equal the key without 'arena_')")
    need((REPO / f"tools/extract/native_contracts/{stem}.json").is_file(), f"native contract: tools/extract/native_contracts/{stem}.json is missing")
    need((REPO / f"ArenaMapPort.Server/db/{key}/base.json").is_file(),
         f"server db: ArenaMapPort.Server/db/{key}/base.json is missing")
    plan = REPO / f"tools/extract/plans/{stem}.json"
    if key in LEGACY_NO_SOURCE_PLAN:
        need(not plan.is_file(), f"source plan: {key} is in LEGACY_NO_SOURCE_PLAN but tools/extract/plans/{stem}.json exists: drop the exemption")
    elif not plan.is_file():
        out.append(f"source plan: tools/extract/plans/{stem}.json is missing")
    else:
        roles = {r.get("role") for r in json.loads(plan.read_text(encoding="utf-8")).get("excluded_scenes", []) if isinstance(r, dict)}
        for role in PLAN_ROLES:
            need(role in roles, f"source plan: tools/extract/plans/{stem}.json has no excluded scene with role {role!r} (source_plan.SCENE_ROLES)")
    need(key in t.key_sources, f"key source: tools/bundle/key-sources.json maps has no {key!r} row")

    # --- validator lists ----------------------------------------------------------------------------------------------------------
    need(name in t.release_names, f"validator list: validate_map_descriptors.RELEASE_MAP_NAMES lacks {name!r}")
    need(key in t.release_keys, f"validator list: validate_map_descriptors.RELEASE_MAP_KEYS lacks {key!r}")
    need(not re.search(r"@\([^)]*'" + re.escape(stem) + r"'", t.park.split("$parked")[1].split("\n")[0]) if "$parked" in t.park else True,
         f"park-unreleased-dist.ps1: '{stem}' is still in $parked, which archives its released outputs")

    # --- extractor tables ---------------------------------------------------------------------------------------------------------
    if key in LEGACY_AMBIENT_EXPORT:
        need(key not in t.audio_exporters, f"native_features.AUDIO_EXPORTERS: {key} is in LEGACY_AMBIENT_EXPORT but has a row: drop the exemption")
    else:
        need(key in t.audio_exporters, f"native_features.AUDIO_EXPORTERS lacks {key!r} (tools/extract/native_features.py)")
    need(key in t.door_sources, f"door poses: neither extract_door_poses.MAP_SOURCES nor DOT_SOURCES has {key!r}")
    need(key in t.gate_sounds, f"gate sounds: extract_gate_sounds.MAPS lacks {key!r}")
    need(key in t.monitors, f"score monitors: extract_score_monitors.MAPS lacks {key!r}")
    need(key in t.loadscreens, f"loading screens: extract_loadscreens.LOADSCREENS lacks {key!r}")
    need(key in t.banners, f"banners: extract_banners.BANNERS lacks {key!r}")
    if key in LEGACY_NAVMESH_DIFF:
        need(key not in t.navmesh_sources, f"navmesh_diff.ARENA_SOURCES: {key} is in LEGACY_NAVMESH_DIFF but has a row: drop the exemption")
    else:
        need(key in t.navmesh_sources, f"navmesh_diff.ARENA_SOURCES lacks {stem!r} (tools/validate/navmesh_diff.py)")
    has_desc = decide(spec, "official description", key in t.description_keys, key in NO_OFFICIAL_DESCRIPTION,
                      "extract_map_descriptions.DESCRIPTION_KEYS", out)
    in_cs = bool(re.search(r"ArenaMapRegistry\." + re.escape(name) + r"\.Key", method_body(t.descriptions_cs, r"OfficialTextMapKeys\s*=\s*new\[\]\s*\{")))
    need(in_cs == has_desc, f"official description: ArenaMapDescriptions.OfficialTextMapKeys and extract_map_descriptions.DESCRIPTION_KEYS disagree about {key!r}")

    # --- crowd (partition of the release) ----------------------------------------------------------------------------------------------
    crowd = decide(spec, "spectator crowd", key in t.spectator_scenes, key in t.crowdless, "extract_spectator.MAP_SCENES; or CROWDLESS_MAP_KEYS", out)
    case = re.search(r'case "' + re.escape(key) + r'":\s*return (null|"[A-Za-z_0-9]+");', method_body(t.spectators, r"static string DesignScene\(string key\)"))
    if case is None:
        out.append(f"spectator crowd: ArenaSpectators.DesignScene has no case for {key!r} (a scene name, or null for a crowdless map)")
    else:
        need((case.group(1) != "null") == crowd, f"spectator crowd: ArenaSpectators.DesignScene returns {case.group(1)} for {key!r} but the lists say crowd={crowd}")

    # --- optional features: flares, water, overview -------------------------------------------------------------------------------------
    flares = decide(spec, "flares", key in t.flare_keys, key in NO_FLARES, "validate_distribution.FLARE_MAP_KEYS", out)
    need((key in t.multiflare) == flares, f"flares: extract_multiflare.SCENES/EXPECTED_CENSUS membership of {key!r} is {key in t.multiflare}, the flare decision is {flares}")
    flare_target = re.search(r'Name="ArenaMapPortStageMultiFlare".*?</Target>', t.client_csproj, re.S)
    in_flare_block = bool(flare_target and f"<MapKey>{key}</MapKey>" in flare_target.group(0))
    need(in_flare_block == flares, f"flares: client csproj ArenaMapPortStageMultiFlare has {'no' if flares else 'a'} <MapKey>{key}</MapKey> row but the flare decision is {flares}")
    need((name in foreach_line(t.arrays["NativeMultiFlare"]) .replace("ArenaMapRegistry.", "").replace(" ", "").split(",")) == flares,
         f"flares: NativeMultiFlare foreach array {'lacks' if flares else 'wrongly lists'} ArenaMapRegistry.{name}")

    water = decide(spec, "native water", key in t.water, key in NO_WATER, "validate_map_descriptors.NATIVE_WATER_MAP_KEYS", out)
    need((f"{stem}-water\\water.json" in t.client_csproj) == water, f"native water: client csproj water rows for {stem!r} do not match the water decision ({water})")

    overview = decide(spec, "overview", name in t.overview_names, key in NO_OVERVIEW, "validate_distribution.OVERVIEW_MAP_NAMES (by registry name)", out)
    if overview:
        need(f"ArenaMapPort_{name}\\ArenaMapPort_{name}.jsonc" in t.client_csproj, f"overview: client csproj ArenaMapPortStageDynamicMaps has no ArenaMapPort_{name}\\ArenaMapPort_{name}.jsonc row")
        need(f'"{name}"' in method_body(t.dynamic_assets, r"internal static readonly string\[\] Maps\s*="), f"overview: DynamicMapAssets.Maps lacks \"{name}\"")
        need(f"'{name}'" in re.search(r"\[ValidateSet\(([^)]*)\)\]\s*\[string\]\$DynamicMapsMap", t.deploy).group(1), f"overview: deploy-to-spt.ps1 DynamicMapsMap ValidateSet lacks '{name}'")
        need(key in t.overview_profiles, f"overview: export_equator.MAP_PROFILES lacks {key!r}")

    # --- client per-map tables ----------------------------------------------------------------------------------------------------------
    need(f'new AmbientAudioProfile("{key}"' in method_body(t.profiles, r"static readonly AmbientAudioProfile\[\] Table\s*="),
         f"ambient profile: AmbientAudioProfiles.Table lacks a profile for {key!r} (a profile in StagedTable is not consulted)")
    for header, label in ((r"internal static void Values\(", "Values (start mix)"), (r"static void PreviousValues\(", "PreviousValues"), (r"static bool HasLegacyMix\(", "HasLegacyMix")):
        need(f'case "{key}"' in method_body(t.defaults, header), f"ambient defaults: AmbientAudioDefaults.{label} has no case for {key!r} (the default throws ArgumentException at raid start)")
    rig = method_body(t.lighting, r"internal static RigValues Rig\(string mapKey\)")
    var = re.search(r"bool (\w+) = mapKey == ArenaMapRegistry\." + re.escape(name) + r"\.Key", rig)
    if var is None:
        out.append(f"map lighting: MapLightingDefaults.Rig has no 'bool x = mapKey == ArenaMapRegistry.{name}.Key' line: the map silently gets donor 'Off' and ZeroLevel 0 (risk 14)")
    else:
        rig_line = re.search(r"bool rig = ([^;]*);", rig)
        need(bool(rig_line) and re.search(r"\b" + var.group(1) + r"\b", rig_line.group(1)) is not None,
             f"map lighting: '{var.group(1)}' is not in MapLightingDefaults.Rig's `bool rig` list: Donor Off, no texture budget, no donor camera (risk 14)")
        zero = re.search(r"ZeroLevel = ([^;]*);", rig)
        need(bool(zero) and re.search(r"\b" + var.group(1) + r"\b", zero.group(1)) is not None,
             f"map lighting: '{var.group(1)}' is not in MapLightingDefaults.Rig's ZeroLevel expression: it silently falls to 0 (risk 14)")
    sidecar_data = json.loads(sidecar.read_text(encoding="utf-8")) if sidecar.is_file() else {}
    if sidecar_data.get("shader_fallback_names"):
        # a map that lists fallback names has stand-ins: the policy must switch them on for this map and the map needs its developer key
        pred = f"Is{name}(mapKey)"
        need(pred in method_body(t.policy, r"internal static bool FallbacksActive\("), f"shader stand-ins: ShaderStandinPolicy.FallbacksActive never asks {pred} although tools/maps/{stem}.json lists shader_fallback_names")
        if name != "Bay5":   # Bay5 keeps its legacy key EnableShaderFallbacks
            need(f'"{name}ShaderStandins"' in t.standins, f"shader stand-ins: ShaderStandins.BindConfig has no \"{name}ShaderStandins\" key")
    for array, text in t.arrays.items():
        if array == "NativeMultiFlare":
            continue   # the flare decision above
        need(name in foreach_line(text).replace("ArenaMapRegistry.", "").replace(" ", "").split(","), f"client array: {array}.cs foreach array lacks ArenaMapRegistry.{name}")
    importer_root = method_body(t.audio_importer, r"static string RootNameFor\(string mapKey\)")
    importer_folder = method_body(t.audio_importer, r"static string AssetFolderFor\(string mapKey\)")
    need(f'case "{key}"' in importer_root, f"unity importer: Bay5AudioImporter.RootNameFor has no case for {key!r} (throws for an unknown map)")
    need(f'case "{key}"' in importer_folder, f"unity importer: Bay5AudioImporter.AssetFolderFor has no case for {key!r} (throws for an unknown map)")

    # --- server --------------------------------------------------------------------------------------------------------------------------
    need(f"ArenaMapRegistry.{name}.Key] = new Descriptor(" in t.banners_cs, f"banners: ArenaMapBanners.ByMapKey has no row for ArenaMapRegistry.{name}")
    need(f"db\\{key}\\base.json" in re.search(r'<Content Include="([^"]*)"', t.server_csproj).group(1), f"server csproj: Content include lacks db\\{key}\\base.json")
    need(f"db\\{key}\\base.json\"><ReleasePath>" in t.server_csproj, f"server csproj: DeployToDist has no ArenaMapPortDbFiles row for db\\{key}\\base.json")

    # --- client csproj staging blocks ------------------------------------------------------------------------------------------------------
    c = t.client_csproj
    for token, what in ((f"arena_{stem}_scenes.bundle", "scenes bundle row"), (f"arena_{stem}_navmesh.bundle", "navmesh bundle row"), (f"arena_{stem}_preset.bundle", "preset bundle row"),
                        (f"stage --map-key {key} ", "BundleCrypt stage Exec"), (f"material-shaders.{key}.json", "shader sidecar Copy"),
                        (f"ArenaMapPortCreateKey_{key}", "create-key property (the allow-list of the first stage)"),
                        (f"{stem}.ai.json", "AI sidecar row")):
        need(token in c, f"client csproj: no {what} ({token})")
    need(re.search(re.escape(key) + r".{0,200}spatial-audio\.json", c, re.S) is not None, f"client csproj: no acoustics item for {key!r} (spatial-audio.json)")
    need(f"{key}\\gate-sounds.json" in c, f"client csproj: no gate-sounds item for {key!r}")
    need(f"'$(ArenaMapPortCreateKeyMap)'!='{key}'" in c, f"client csproj: the create-key allow-list Error does not name {key!r}")
    return out


class ChecklistTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.inputs = Inputs()
        cls.specs = released_specs()

    def test_the_registry_is_the_six_released_maps(self):
        self.assertEqual({s.name for s in self.specs}, set(VD.RELEASE_MAP_NAMES))
        self.assertEqual({s.key for s in self.specs}, set(VD.RELEASE_MAP_KEYS))
        for spec in self.specs:
            self.assertTrue((REPO / "tools" / "maps" / f"{spec.stem}.json").is_file(), spec)

    def test_every_released_map_has_every_per_map_table_row(self):
        problems = []
        for spec in self.specs:
            problems += [f"{spec.key}: {line}" for line in checklist(spec, self.inputs)]
        self.assertEqual(problems, [], "\n" + f"{len(problems)} missing per-map table row(s):\n" + "\n".join(problems))

    def test_the_written_decision_lists_hold_no_stale_entries(self):
        keys = {s.key for s in self.specs}
        for label, entries in (("NO_FLARES", NO_FLARES), ("NO_WATER", NO_WATER), ("NO_OVERVIEW", NO_OVERVIEW),
                               ("NO_OFFICIAL_DESCRIPTION", NO_OFFICIAL_DESCRIPTION), ("LEGACY_AMBIENT_EXPORT", LEGACY_AMBIENT_EXPORT),
                               ("LEGACY_NO_SOURCE_PLAN", LEGACY_NO_SOURCE_PLAN), ("LEGACY_NAVMESH_DIFF", LEGACY_NAVMESH_DIFF)):
            self.assertLessEqual(set(entries), keys, f"{label} names a map outside the registry: {sorted(set(entries) - keys)}")

    def test_a_scratch_seventh_map_fails_with_a_readable_list(self):
        """The negative control: a fake registry entry (the registry itself is not edited) lacks every row and every decision."""
        fake = Spec("ScratchMap", "arena_scratchmap", "scratchmap")
        problems = checklist(fake, self.inputs)
        text = "\n".join(problems)
        self.assertGreaterEqual(len(problems), 30, text)
        for needle in ("sidecar: tools/maps/scratchmap.json", "native contract", "source plan: tools/extract/plans/scratchmap.json is missing",
                       "key source", "RELEASE_MAP_KEYS", "AUDIO_EXPORTERS", "door poses", "gate sounds", "score monitors", "loading screens", "banners",
                       "navmesh_diff.ARENA_SOURCES", "official description: in neither", "spectator crowd: in neither", "flares: in neither",
                       "native water: in neither", "overview: in neither", "AmbientAudioProfiles.Table", "AmbientAudioDefaults.Values",
                       "MapLightingDefaults.Rig has no", "NativeDecalRepairs.cs foreach", "RoomReverb.cs foreach", "Bay5AudioImporter.RootNameFor",
                       "ArenaMapBanners.ByMapKey", "server csproj: Content include", "client csproj: no scenes bundle row", "create-key allow-list"):
            self.assertIn(needle, text, f"the scratch map's list does not name {needle!r}:\n{text}")

    def test_the_known_silent_default_is_caught(self):
        """Risk 14: a map that is in the rig list but not in ZeroLevel's expression must fail (the text is rewritten in memory only)."""
        spec = Spec("Bowl", "arena_bowl", "bowl")
        inputs = Inputs()
        self.assertEqual(checklist(spec, inputs), [])
        inputs.lighting = inputs.lighting.replace("(equator || airPit || bowl) ? -4.4f : 0f", "(equator || airPit) ? -4.4f : 0f")
        self.assertIn("ZeroLevel expression", "\n".join(checklist(spec, inputs)))


    def test_every_table_of_the_checklist_is_exercised_by_an_in_memory_mutation(self):
        """Negative controls on REAL maps (fixer, 2026-11-20): each row removes one table entry from the text or set the checklist reads
        and asserts that the matching line appears. Covers the has-branches (flares, water, overview), the stand-in rows and the arrays the
        scratch-map test cannot reach. A table dropped from checklist() turns its row red here."""
        def cut_set(attr, value):
            return lambda t: getattr(t, attr).discard(value)

        def sub(attr, old, new):
            def apply(t):
                text = getattr(t, attr)
                self.assertIn(old, text, f"mutation target {old!r} is not in Inputs.{attr}")
                setattr(t, attr, text.replace(old, new))
            return apply

        def sub_array(array, old, new):
            def apply(t):
                self.assertIn(old, t.arrays[array], f"mutation target {old!r} is not in {array}")
                t.arrays[array] = t.arrays[array].replace(old, new)
            return apply

        cases = [
            ("Bowl", cut_set("flare_keys", "arena_bowl"), "flares: in neither"),
            ("Bowl", cut_set("multiflare", "arena_bowl"), "extract_multiflare"),
            ("Bowl", sub("client_csproj", "<MapKey>arena_bowl</MapKey>", "<MapKey>zz</MapKey>"), "ArenaMapPortStageMultiFlare"),
            ("Bowl", sub_array("NativeMultiFlare", "ArenaMapRegistry.Bowl", "ArenaMapRegistry.Zz"), "NativeMultiFlare foreach"),
            ("Bay5", cut_set("water", "arena_bay5"), "native water: in neither"),
            ("Bay5", sub("client_csproj", "bay5-water\\water.json", "zz.json"), "native water: client csproj water rows"),
            ("Bay5", cut_set("overview_names", "Bay5"), "overview: in neither"),
            ("Bay5", sub("client_csproj", "ArenaMapPort_Bay5\\ArenaMapPort_Bay5.jsonc", "zz"), "overview: client csproj"),
            ("Bay5", sub("dynamic_assets", '"Bay5"', '"Zz"'), "DynamicMapAssets.Maps"),
            ("Bay5", sub("deploy", "'Bay5'", "'Zz'"), "DynamicMapsMap ValidateSet"),
            ("Bay5", cut_set("overview_profiles", "arena_bay5"), "MAP_PROFILES"),
            ("Bowl", lambda t: setattr(t, "park", "$parked = @('bowl','x')" + chr(10)), "$parked"),
            ("Bowl", sub_array("NativeSpatialAudio", "ArenaMapRegistry.Bowl", "ArenaMapRegistry.Zz"), "NativeSpatialAudio.cs foreach"),
            ("Bowl", sub_array("PrecipitationAudio", "ArenaMapRegistry.Bowl", "ArenaMapRegistry.Zz"), "PrecipitationAudio.cs foreach"),
            ("Bowl", sub("descriptions_cs", "ArenaMapRegistry.Bowl.Key", "ArenaMapRegistry.Zz.Key"), "OfficialTextMapKeys"),
            ("Bowl", sub("defaults", 'case "arena_bowl"', 'case "arena_zz"'), "ambient defaults"),
            ("Bowl", sub("profiles", 'new AmbientAudioProfile("arena_bowl"', 'new AmbientAudioProfile("arena_zz"'), "AmbientAudioProfiles.Table"),
            ("Bowl", sub("lighting", "ArenaMapRegistry.Bowl.Key", "ArenaMapRegistry.Zz.Key"), "MapLightingDefaults.Rig has no"),
            ("Bowl", sub("lighting", "|| bowl", "|| zz"), "bool rig"),
            ("Bowl", sub("policy", "IsBowl(mapKey)", "IsZz(mapKey)"), "ShaderStandinPolicy.FallbacksActive"),
            ("Bowl", sub("standins", '"BowlShaderStandins"', '"ZzShaderStandins"'), "BowlShaderStandins"),
            ("Bowl", sub("client_csproj", "ArenaMapPortCreateKey_arena_bowl", "ArenaMapPortCreateKey_zz"), "create-key property"),
            ("Bowl", sub("audio_importer", 'case "arena_bowl"', 'case "arena_zz"'), "unity importer"),
            ("Bowl", sub("banners_cs", "ArenaMapRegistry.Bowl.Key] = new Descriptor(", "ArenaMapRegistry.Zz.Key] = new Descriptor("), "ArenaMapBanners.ByMapKey"),
        ]
        for stem_name, mutate, needle in cases:
            spec = next(s for s in self.specs if s.name == stem_name)
            inputs = Inputs()
            self.assertEqual(checklist(spec, inputs), [], f"{stem_name} must be clean before the mutation")
            try:
                mutate(inputs)
            except AssertionError as error:
                self.fail(f"mutation for {needle!r} does not apply: {error}")
            self.assertIn(needle, chr(10).join(checklist(spec, inputs)), f"removing a table entry for {stem_name} was not caught ({needle!r})")


if __name__ == "__main__":
    unittest.main()
