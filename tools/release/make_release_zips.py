#!/usr/bin/env python3
"""Split the staged distribution (dist/) into the multi-part release ZIPs.

dist/ holds two install roots, `BepInEx/plugins/maschine-ArenaMapPort/...` (client) and
`SPT_Runtime/user/mods/ArenaMapPort/...` (server). Together they are far larger than the 2 GiB a single
ZIP can safely carry through the release channels, so the release is cut into parts that install by extracting
EVERY part into the SPT root:

    part1ofN-core         client DLL, server DLL, original map descriptions, README, notices
    part2ofN-<map stem>   one part per released map: bundles, db/<key>/base.json, acoustics, AI sidecar,
                          flares, water, loading screens, overview map, shader sidecar, banner

The released maps and their order come from tools/validate/validate_map_descriptors.py
(RELEASE_MAP_KEYS / RELEASE_MAP_NAMES). The version comes from the `<Version>` of both csproj files, which must
agree (`--version` overrides). Outputs, under `--out` (default work/releases/<version>/):

    maschine-ArenaMapPort-<version>-part<i>of<N>-<id>.zip     one per part
    maschine-ArenaMapPort-<version>.sha256                    '<sha256>  <zip name>' per part, in part order

Classification is strict and anchored: every dist file must match exactly one rule (see build_rules). A file
that matches none (a parked map's output such as arena_airpit2_scenes.bundle or ai/airpit2.ai.json, a stray
.pdb or backup, a Thumbs.db or .bak inside an acoustics / flares / water / dynamicmaps folder) or more than one
is a hard error, never a silent skip; so is a directory that cannot be listed. The rules pin the leaf names the
build stages, and required_files() demands everything every released map must carry (bundles, database entry,
shader sidecar, AI sidecar, banner, acoustic graph, first loading screen); only flares, water and the overview
map are optional per map. Every part carries
BepInEx/plugins/maschine-ArenaMapPort/parts/<id>.json, a schema-1 manifest naming ALL parts plus this part's
own entries with size and SHA-256, so any single ZIP says which others complete the install. The core part adds
README.txt and THIRD-PARTY-NOTICES.txt (client and server root); the notices are cut from the MIT blocks of
THIRD-PARTY-NOTICES.md.

Compression is chosen per file: ZIP_STORED for .jpg and for any file whose probe (deflate level 6 over the first
8 MiB) gains less than 3 %, ZIP_DEFLATED level 6 otherwise. Timestamps are fixed (1980-01-01) so identical inputs
give identical ZIP bytes. Every part must end up below 2**31 bytes.

Modes:
    --plan                    print the partition and size estimates, write nothing
    (default)                 build the ZIPs and the .sha256 (clean git tree required unless --allow-dirty;
                              an allowed dirty tree is recorded as '<commit>+dirty' in every part manifest)
    --force                   replace an existing release of this version. The previous set is only touched
                              after the new one is complete and verified: a failed rebuild changes nothing, and
                              parts of an older part count are removed once the new set is in place.
    --verify-only <dir>       re-hash existing parts against the .sha256, check every part manifest, the size
                              gate and the partition invariant (each map's bundles and database entry travel
                              together in that map's part, nowhere else)
    --extract-check <scratch> after building (or after --verify-only): extract all parts into an EMPTY scratch
                              directory and compare byte for byte with dist/, ignoring generated entries. The
                              scratch directory is validated before anything is built.

Nothing here touches dist/ or any SPT install; the only writes are the output directory and the scratch
directory (git runs with --no-optional-locks, so even the index is left alone). BSG-derived bytes never enter
git: outputs land under the ignored work/.
"""
from __future__ import annotations

import argparse
import filecmp
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
import zlib
from dataclasses import dataclass
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "validate"))
from validate_map_descriptors import RELEASE_MAP_KEYS, RELEASE_MAP_NAMES  # noqa: E402

PACKAGE = "maschine-ArenaMapPort"
PLUGIN_ROOT = "BepInEx/plugins/maschine-ArenaMapPort"
SERVER_ROOT = "SPT_Runtime/user/mods/ArenaMapPort"
CORE_ID = "core"
# Equator keeps its pre-generalisation sidecar name; every other map uses material-shaders.<key>.json
# (ShaderRebind.cs, ArenaMapPort.Client.csproj ArenaMapPortStageWpOutputs).
LEGACY_SHADER_SIDECAR_KEY = "arena_equator"
CLIENT_CSPROJ = "ArenaMapPort.Client/ArenaMapPort.Client.csproj"
SERVER_CSPROJ = "ArenaMapPort.Server/ArenaMapPort.Server.csproj"

SIZE_LIMIT = 2 ** 31
PROBE_BYTES = 8 * 1024 * 1024
MIN_GAIN = 0.03
COMPRESS_LEVEL = 6
CHUNK = 4 * 1024 * 1024
ZIP_EPOCH = (1980, 1, 1, 0, 0, 0)
STORED_SUFFIXES = (".jpg", ".jpeg")
LARGE_FILE_BYTES = 1_000_000
MANIFEST_SCHEMA = 1
MANIFEST_KEYS = ("schema", "version", "source_commit", "part", "parts", "entries")
ALLOWED_METHODS = (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)  # the only two the packager ever writes
# Leaf file name of a staged extract (clip, bake, texture, layer): no separators, no stray suffix games.
LEAF = r"[A-Za-z0-9_.-]+"

VERSION_RE = re.compile(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?")
SHA256_LINE_RE = re.compile(r"([0-9a-f]{64})  (\S+\.zip)")
MANIFEST_PATH_RE = re.compile(re.escape(PLUGIN_ROOT) + r"/parts/([a-z0-9]+)\.json")

README_NAME = f"{PLUGIN_ROOT}/README.txt"
CLIENT_NOTICES_NAME = f"{PLUGIN_ROOT}/THIRD-PARTY-NOTICES.txt"
SERVER_NOTICES_NAME = f"{SERVER_ROOT}/THIRD-PARTY-NOTICES.txt"
DESCRIPTIONS_NAME = f"{SERVER_ROOT}/locales/map-descriptions.json"


class PackagingError(Exception):
    """Any refusal: bad input, unclassified file, existing output, failed gate."""


class ClassificationError(PackagingError):
    pass


class PartTooLarge(PackagingError):
    pass


class VerificationError(PackagingError):
    pass


# --------------------------------------------------------------------------------------------------------------
# Release description
# --------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class MapRelease:
    key: str      # arena_equator
    stem: str     # equator (part id, ai sidecar stem)
    display: str  # Equator (DynamicMaps folder name)


@dataclass(frozen=True)
class ReleaseSpec:
    version: str
    maps: tuple
    client_dll: str
    server_dll: str


def default_maps():
    return tuple(zip(RELEASE_MAP_KEYS, RELEASE_MAP_NAMES))


def csproj_property(path: Path, name: str):
    """Value of <name> in the project's PropertyGroups, or None; conflicting values are an error."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        raise PackagingError(f"cannot read {path}: {exc}") from exc
    values = {(e.text or "").strip() for e in root.findall(f"PropertyGroup/{name}") if (e.text or "").strip()}
    if len(values) > 1:
        raise PackagingError(f"{path}: conflicting <{name}> values {sorted(values)}")
    return next(iter(values)) if values else None


def check_version(version: str) -> str:
    if not VERSION_RE.fullmatch(version):
        raise PackagingError(f"invalid version {version!r} (expected MAJOR.MINOR.PATCH[-label])")
    return version


def load_spec(repo: Path, version: str | None = None, maps=None) -> ReleaseSpec:
    client = repo / CLIENT_CSPROJ
    server = repo / SERVER_CSPROJ
    names = {}
    for label, path in (("client", client), ("server", server)):
        assembly = csproj_property(path, "AssemblyName")
        if not assembly:
            raise PackagingError(f"{path}: no <AssemblyName>")
        names[label] = assembly
    if version is None:
        found = {label: csproj_property(path, "Version") for label, path in (("client", client), ("server", server))}
        if not all(found.values()):
            raise PackagingError(f"missing <Version>: {found}")
        if found["client"] != found["server"]:
            raise PackagingError(f"client and server csproj versions disagree: {found} (pass --version to override)")
        version = found["client"]
    check_version(version)
    pairs = tuple(default_maps() if maps is None else maps)
    if not pairs:
        raise PackagingError("no released maps")
    releases = []
    for key, display in pairs:
        if not re.fullmatch(r"arena_[a-z0-9]+", key):
            raise PackagingError(f"unexpected map key {key!r}")
        releases.append(MapRelease(key, key.removeprefix("arena_"), display))
    stems = [m.stem for m in releases]
    if len(set(stems)) != len(stems) or CORE_ID in stems:
        raise PackagingError(f"map stems must be unique and not {CORE_ID!r}: {stems}")
    return ReleaseSpec(version, tuple(releases), f"{PLUGIN_ROOT}/{names['client']}.dll",
                       f"{SERVER_ROOT}/{names['server']}.dll")


# --------------------------------------------------------------------------------------------------------------
# dist scan and classification
# --------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class DistFile:
    rel: str
    path: Path
    size: int


@dataclass(frozen=True)
class Rule:
    part: str
    label: str
    regex: re.Pattern


@dataclass
class PartPlan:
    index: int
    count: int
    id: str
    zip_name: str
    files: list

    @property
    def raw_bytes(self) -> int:
        return sum(f.size for f in self.files)


def check_safe_name(name: str) -> None:
    parts = name.split("/")
    if (not name or not name.isascii() or "\\" in name or ":" in name or name.startswith("/")
            or any(part in ("", ".", "..") or part != part.strip() or part.endswith(".") for part in parts)):
        raise PackagingError(f"unsafe path: {name!r}")


def _walk_error(exc: OSError):
    """os.walk swallows scandir errors unless told otherwise; an unlistable directory must stop the run, or
    files in it (acoustics, flares, water, overview map ...) could vanish from a release unnoticed."""
    raise PackagingError(f"cannot list {exc.filename}: {exc}") from exc


def check_case_collisions(files) -> None:
    seen = {}
    for f in files:
        other = seen.setdefault(f.rel.casefold(), f.rel)
        if other != f.rel:
            raise PackagingError(f"case-insensitive path collision: {other} / {f.rel}")


def scan_dist(dist: Path) -> list:
    """Every regular file below dist (read-only walk), sorted by relative posix path."""
    if not dist.is_dir():
        raise PackagingError(f"dist directory not found: {dist}")
    files = []
    for dirpath, dirnames, filenames in os.walk(dist, followlinks=False, onerror=_walk_error):
        for name in dirnames:
            directory = Path(dirpath, name)
            if directory.is_symlink() or getattr(directory, "is_junction", lambda: False)():
                raise PackagingError(f"linked directory refused: {directory}")
        for name in filenames:
            full = Path(dirpath, name)
            if full.is_symlink():
                raise PackagingError(f"symbolic link refused: {full}")
            rel = full.relative_to(dist).as_posix()
            check_safe_name(rel)
            try:
                size = full.stat().st_size
            except OSError as exc:
                raise PackagingError(f"cannot read {full}: {exc}") from exc
            files.append(DistFile(rel, full, size))
    files.sort(key=lambda f: f.rel)
    check_case_collisions(files)
    if not files:
        raise PackagingError(f"dist is empty: {dist}")
    return files


def shader_sidecar_name(key: str) -> str:
    return "material-shaders.json" if key == LEGACY_SHADER_SIDECAR_KEY else f"material-shaders.{key}.json"


def build_rules(spec: ReleaseSpec) -> list:
    """Exactly the dist layout the build stages. Every pattern is matched with fullmatch, and the key / stem /
    display name is escaped and followed by its delimiter ('/', '_', '.'), so the parked arena_airpit2 can never
    match arena_airpit (and ai/airpit2.ai.json never matches ai/airpit.ai.json). The folder rules (acoustics,
    flares, water, dynamicmaps) pin the leaf names the build stages (ArenaMapPort.Client.csproj, the spatial-audio
    and flare extractors), flat or one 'textures/' / 'Layers/' level deep, so a stray Thumbs.db, desktop.ini,
    .bak, .git folder or nested leftover is a classification error instead of shipping."""
    rules = []

    def add(part, label, pattern):
        rules.append(Rule(part, label, re.compile(pattern)))

    add(CORE_ID, "client DLL", re.escape(spec.client_dll))
    add(CORE_ID, "server DLL", re.escape(spec.server_dll))
    add(CORE_ID, "map descriptions", re.escape(DESCRIPTIONS_NAME))
    for m in spec.maps:
        key = re.escape(m.key)
        add(m.stem, "bundle", re.escape(f"{PLUGIN_ROOT}/bundles/") + key + r"_(?:scenes|navmesh|preset)\.bundle")
        add(m.stem, "acoustics", re.escape(f"{PLUGIN_ROOT}/acoustics/{m.key}/")
            + rf"(?:spatial-audio\.json|source\.audiobakedata|{LEAF}\.wav)")
        add(m.stem, "ai sidecar", re.escape(f"{PLUGIN_ROOT}/ai/{m.stem}.ai.json"))
        add(m.stem, "flares", re.escape(f"{PLUGIN_ROOT}/flares/{m.key}/") + rf"(?:flares\.json|textures/{LEAF}\.bin)")
        add(m.stem, "water", re.escape(f"{PLUGIN_ROOT}/water/{m.key}/") + rf"(?:water\.json|textures/{LEAF}\.bin)")
        add(m.stem, "loading screen", re.escape(f"{PLUGIN_ROOT}/loadscreens/") + key + r"_[0-9]+\.jpg")
        folder = f"ArenaMapPort_{m.display}"
        add(m.stem, "dynamicmaps", re.escape(f"{PLUGIN_ROOT}/dynamicmaps/{folder}/")
            + rf"(?:{re.escape(folder)}\.jsonc|Layers/{LEAF}\.svg)")
        add(m.stem, "shader sidecar", re.escape(f"{PLUGIN_ROOT}/{shader_sidecar_name(m.key)}"))
        add(m.stem, "server database", re.escape(f"{SERVER_ROOT}/db/{m.key}/base.json"))
        add(m.stem, "banner", re.escape(f"{SERVER_ROOT}/banners/{m.key}.jpg"))
    return rules


def classify(rel: str, rules) -> Rule:
    hits = [rule for rule in rules if rule.regex.fullmatch(rel)]
    if not hits:
        raise ClassificationError(f"unclassified dist file (not part of any released map or the core): {rel}")
    if len(hits) > 1:
        raise ClassificationError(
            f"doubly classified dist file: {rel} matches {[f'{r.part}:{r.label}' for r in hits]}")
    return hits[0]


def required_files(spec: ReleaseSpec) -> dict:
    """Part id -> files that must exist in dist for that part to be installable. Mirrors what
    tools/validate/validate_distribution.py treats as mandatory per released map: a missing AI sidecar silently
    falls back to the NavMesh-synthesised AI, a missing shader sidecar leaves pink materials, and a map without
    its acoustic graph, banner or loading screens is not the map the README promises. Only flares, water and the
    DynamicMaps overview are optional (AirPit ships none of them)."""
    required = {CORE_ID: [spec.client_dll, spec.server_dll, DESCRIPTIONS_NAME]}
    for m in spec.maps:
        names = [f"{PLUGIN_ROOT}/bundles/{m.key}_{kind}.bundle" for kind in ("scenes", "navmesh", "preset")]
        names += [f"{SERVER_ROOT}/db/{m.key}/base.json",
                  f"{PLUGIN_ROOT}/{shader_sidecar_name(m.key)}",
                  f"{PLUGIN_ROOT}/ai/{m.stem}.ai.json",
                  f"{SERVER_ROOT}/banners/{m.key}.jpg",
                  f"{PLUGIN_ROOT}/acoustics/{m.key}/spatial-audio.json",
                  f"{PLUGIN_ROOT}/loadscreens/{m.key}_1.jpg"]
        required[m.stem] = names
    return required


def zip_name_for(spec: ReleaseSpec, index: int, count: int, part_id: str) -> str:
    return f"{PACKAGE}-{spec.version}-part{index}of{count}-{part_id}.zip"


def partition_dist(files, spec: ReleaseSpec, rules=None) -> list:
    """Assign every dist file to exactly one part. A map's database entry always travels with its bundles."""
    rules = build_rules(spec) if rules is None else rules
    ids = [CORE_ID] + [m.stem for m in spec.maps]
    buckets = {part_id: [] for part_id in ids}
    for f in files:
        buckets[classify(f.rel, rules).part].append(f)
    present = {f.rel for f in files}
    for part_id, names in required_files(spec).items():
        missing = [name for name in names if name not in present]
        if missing:
            raise PackagingError(f"incomplete dist: part '{part_id}' lacks {missing}")
    count = len(ids)
    return [PartPlan(i, count, part_id, zip_name_for(spec, i, count, part_id), buckets[part_id])
            for i, part_id in enumerate(ids, 1)]


# --------------------------------------------------------------------------------------------------------------
# Compression choice
# --------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class Decision:
    method: int
    gain: float
    estimate: int


def method_name(method: int) -> str:
    return "DEFLATED" if method == zipfile.ZIP_DEFLATED else "STORED"


def _deflated_size(sample: bytes) -> int:
    compressor = zlib.compressobj(COMPRESS_LEVEL, zlib.DEFLATED, -15)
    return len(compressor.compress(sample)) + len(compressor.flush())


def _decide(name: str, size: int, read_sample) -> Decision:
    if name.lower().endswith(STORED_SUFFIXES) or size == 0:
        return Decision(zipfile.ZIP_STORED, 0.0, size)
    sample = read_sample()
    if not sample:
        return Decision(zipfile.ZIP_STORED, 0.0, size)
    gain = 1.0 - _deflated_size(sample) / len(sample)
    if gain < MIN_GAIN:
        return Decision(zipfile.ZIP_STORED, gain, size)
    return Decision(zipfile.ZIP_DEFLATED, gain, int(round(size * (1.0 - gain))))


def decide_file(f: DistFile, probe_bytes: int = PROBE_BYTES) -> Decision:
    """STORED for .jpg and for files whose deflate probe (first probe_bytes at level 6) gains under 3 %."""
    def read_sample():
        with f.path.open("rb") as stream:
            return stream.read(probe_bytes)
    return _decide(f.rel, f.size, read_sample)


def decide_bytes(name: str, data: bytes, probe_bytes: int = PROBE_BYTES) -> Decision:
    return _decide(name, len(data), lambda: data[:probe_bytes])


def zip_overhead(name: str) -> int:
    """Local header + central directory record of one entry (no extra fields)."""
    return 30 + 46 + 2 * len(name)


# --------------------------------------------------------------------------------------------------------------
# git
# --------------------------------------------------------------------------------------------------------------

# Spelled out so the answer never depends on the user's git configuration: status.showUntrackedFiles=no would hide
# untracked files, diff.ignoreSubmodules would hide a dirty submodule, and a plain `git status` refreshes (rewrites)
# .git/index, which this tool must not do (and which can collide with a concurrent git operation).
STATUS_ARGS = ("--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none")
COMMIT_RE = re.compile(r"[0-9a-f]{40,64}")


def _git(repo: Path, *args):
    # The caller's GIT_* variables (GIT_DIR / GIT_WORK_TREE from a hook or an IDE task, GIT_INDEX_FILE ...) would
    # redirect git to another repository; drop them all and only set the ceiling below.
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_")}
    # Never walk above the repo: a scratch directory inside some other checkout must not report that one.
    env["GIT_CEILING_DIRECTORIES"] = str(repo.parent)
    try:
        return subprocess.run(["git", *args], cwd=repo, env=env, capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.SubprocessError):
        return None


def source_commit(repo: Path) -> str:
    proc = _git(repo, "rev-parse", "HEAD")
    if proc is None or proc.returncode != 0 or not COMMIT_RE.fullmatch(proc.stdout.strip()):
        return "unknown"
    return proc.stdout.strip()


def working_tree_status(repo: Path):
    """git status lines, or None when git cannot answer."""
    proc = _git(repo, *STATUS_ARGS)
    if proc is None or proc.returncode != 0:
        return None
    return proc.stdout.rstrip().splitlines()


def require_clean_tree(repo: Path) -> None:
    lines = working_tree_status(repo)
    if lines is None:
        raise PackagingError("cannot read git status; run inside the repository or pass --allow-dirty")
    if lines:
        shown = "\n  ".join(lines[:10]) + (f"\n  ... {len(lines) - 10} more" if len(lines) > 10 else "")
        raise PackagingError("git working tree is not clean (a release records its source commit); "
                             f"commit or remove these, or pass --allow-dirty:\n  {shown}")


def resolve_commit(repo: Path, allow_dirty: bool, warn=None) -> str:
    """The provenance recorded in every part manifest. A tree that --allow-dirty let through must not look like a
    clean build of HEAD: its commit gets a '+dirty' suffix (also when git cannot say), and an unidentifiable
    commit is reported instead of recorded silently."""
    warn = (lambda message: print(f"WARNING: {message}", file=sys.stderr)) if warn is None else warn
    commit = source_commit(repo)
    if commit == "unknown":
        warn("the source commit cannot be determined; the release records 'unknown'")
        return commit
    if allow_dirty:
        lines = working_tree_status(repo)
        if lines is None:
            warn("cannot read git status; the release records its commit as +dirty")
            return commit + "+dirty"
        if lines:
            warn(f"the working tree has {len(lines)} uncommitted change(s); the release records its commit as "
                 f"{commit[:12]}+dirty")
            return commit + "+dirty"
    return commit


# --------------------------------------------------------------------------------------------------------------
# Generated entries
# --------------------------------------------------------------------------------------------------------------

def manifest_name(part_id: str) -> str:
    return f"{PLUGIN_ROOT}/parts/{part_id}.json"


def notices_text(repo: Path) -> str:
    """Third-party notices from the MIT blocks of THIRD-PARTY-NOTICES.md: the distinct licence texts, once each.
    A new distinct block is an error so a new attribution is reviewed here instead of dropped."""
    source = (repo / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8")
    blocks = list(dict.fromkeys(re.findall(r"```\n(MIT License\n.*?)\n```", source, re.S)))
    if (len(blocks) != 2 or "Copyright (c) 2026 danauraborealis" not in blocks[0]
            or "Copyright (c) 2026 LennoxP90" not in blocks[1]):
        raise PackagingError("THIRD-PARTY-NOTICES.md must carry exactly the ManimalIcebreaker and "
                             "SPT-InterchangeRework MIT blocks; update make_release_zips.notices_text")
    return ("Third-party software notices\n\n"
            "ArenaMapPort includes adaptations of ManimalIcebreaker (location registration,\n"
            "banner routing, raid-start repairs, skybox fallback and preset tooling) and\n"
            "SPT-InterchangeRework (NavMesh installation and shader-rebinding structure).\n\n"
            "ManimalIcebreaker\n==================\n" + blocks[0] + "\n\n"
            "SPT-InterchangeRework\n====================\n" + blocks[1] + "\n\n"
            "Credits: RaiRaiTheRaichu's HiddenCaches inspired the exit-smoke approach;\n"
            "no code was copied from HiddenCaches.\n\n"
            "These notices apply to the attributed software, not to bundled game assets.\n")


def install_instruction(parts) -> str:
    return (f"Close EFT and the SPT server, then extract ALL {len(parts)} parts of this release into the SPT "
            "root directory (the folder that contains BepInEx and SPT_Runtime), merging folders and replacing "
            "existing ArenaMapPort files. The parts do not overlap and can be extracted in any order.")


def readme_text(spec: ReleaseSpec, parts) -> str:
    display = {m.stem: m.display for m in spec.maps}
    lines = []
    for p in parts:
        what = ("plugin, server mod, original map descriptions, notices" if p.id == CORE_ID
                else f"{display[p.id]} map data (bundles, NavMesh, server database entry, audio, art)")
        lines.append(f"  {p.index} of {p.count}  {p.zip_name}\n         {what}")
    names = ", ".join(m.display for m in spec.maps)
    return (f"ArenaMapPort {spec.version}\n\n"
            "Target: SPT 4.1.x / EFT 0.16.9.5\n\n"
            f"This release comes in {len(parts)} ZIP parts. Install ALL of them; a map is complete only with\n"
            "its own part and the core part.\n\n"
            f"{chr(10).join(lines)}\n\n"
            "Installation\n"
            "1. Close the game and the SPT server.\n"
            "2. Extract EVERY part into your SPT installation directory (the folder that contains\n"
            "   BepInEx and SPT_Runtime). Merge the folders and replace existing ArenaMapPort files\n"
            "   when prompted. The parts do not overlap and can be extracted in any order.\n"
            "3. When upgrading a development build, also remove the former BepInEx/plugins/ArenaMapPort\n"
            "   folder (the plugin folder is now maschine-ArenaMapPort), the former\n"
            "   SPT_Runtime/user/mods/ArenaMapPort/db/base.json, and the development copies\n"
            "   DynamicMaps/Maps/ArenaMapPort_Equator and ArenaMapPort_Bay5 if still present, to avoid\n"
            "   duplicate maps. Unused arena-audio and arena-ui folders may be removed too.\n"
            "4. Start the SPT server, then the game.\n\n"
            f"Included locations: {names}.\n"
            "These maps are intended for ArenaMode; no loot or scav raids are provided.\n"
            "ArenaMode and other mods are not included.\n\n"
            "Optional DynamicMaps integration: overview map data lives in this plugin's own dynamicmaps\n"
            "folder and is registered automatically when DynamicMaps is installed.\n\n"
            "Settings: F12 exposes NaturalEquatorNight for a natural Equator midnight look on the next\n"
            "raid. DeveloperMode appears under Advanced and requires a restart. A fresh installation\n"
            "creates only the compact player configuration; existing user settings are preserved.\n\n"
            "Verification: maschine-ArenaMapPort-" + spec.version + ".sha256 lists the SHA-256 of every part\n"
            "(Get-FileHash in PowerShell). BepInEx/plugins/maschine-ArenaMapPort/parts/ records, per part,\n"
            "the files it installed.\n\n"
            "See THIRD-PARTY-NOTICES.txt for third-party software attribution.\n")


def generated_core_entries(spec: ReleaseSpec, parts, repo: Path) -> dict:
    notices = notices_text(repo).encode("utf-8")
    return {README_NAME: readme_text(spec, parts).encode("utf-8"),
            CLIENT_NOTICES_NAME: notices,
            SERVER_NOTICES_NAME: notices}


def manifest_bytes(spec: ReleaseSpec, parts, plan: PartPlan, commit: str, entries) -> bytes:
    document = {
        "schema": MANIFEST_SCHEMA,
        "version": spec.version,
        "source_commit": commit,
        "part": {"index": plan.index, "of": plan.count, "id": plan.id, "zip": plan.zip_name},
        "parts": [{"index": p.index, "id": p.id, "zip": p.zip_name, "dist_files": len(p.files),
                   "raw_bytes": p.raw_bytes} for p in parts],
        "install": install_instruction(parts),
        "entries": sorted(entries, key=lambda e: e["path"]),
    }
    return (json.dumps(document, indent=2) + "\n").encode("utf-8")


# --------------------------------------------------------------------------------------------------------------
# Plan
# --------------------------------------------------------------------------------------------------------------

@dataclass
class PartEstimate:
    plan: PartPlan
    generated_count: int
    raw_bytes: int       # dist files only
    estimated_zip: int


def estimate_parts(spec, parts, decisions, generated, commit, probe_bytes=PROBE_BYTES) -> list:
    estimates = []
    for plan in parts:
        total = 22  # end of central directory
        entries = []
        for f in plan.files:
            total += decisions[f.rel].estimate + zip_overhead(f.rel)
            entries.append({"path": f.rel, "size": f.size, "sha256": "0" * 64, "origin": "dist"})
        own = generated if plan.id == CORE_ID else {}
        for name, data in own.items():
            total += decide_bytes(name, data, probe_bytes).estimate + zip_overhead(name)
            entries.append({"path": name, "size": len(data), "sha256": "0" * 64, "origin": "generated"})
        manifest = manifest_bytes(spec, parts, plan, commit, entries)
        mname = manifest_name(plan.id)
        total += decide_bytes(mname, manifest, probe_bytes).estimate + zip_overhead(mname)
        estimates.append(PartEstimate(plan, len(own) + 1, plan.raw_bytes, total))
    return estimates


def _size(n: int) -> str:
    return f"{n:,} B ({n / 2 ** 20:,.1f} MiB)"


def format_plan(spec, dist, commit, estimates, decisions, size_limit) -> str:
    out = [f"ArenaMapPort release plan  version {spec.version}  parts {len(estimates)}",
           f"  source commit {commit}",
           f"  dist          {dist}",
           f"  size limit    {size_limit:,} B per part"]
    warnings = []
    total_raw = total_zip = total_files = 0
    for est in estimates:
        plan = est.plan
        out.append("")
        out.append(f"part {plan.index} of {plan.count}  {plan.id}  ->  {plan.zip_name}")
        out.append(f"  dist files {len(plan.files)} (+{est.generated_count} generated)  raw {_size(est.raw_bytes)}"
                   f"  estimated zip {_size(est.estimated_zip)}")
        big = [f for f in plan.files if f.size > LARGE_FILE_BYTES]
        if big:
            out.append(f"  files over {LARGE_FILE_BYTES:,} B:")
            for f in big:
                d = decisions[f.rel]
                out.append(f"    {method_name(d.method):8} gain {d.gain * 100:5.1f}%  {f.size:>13,} B -> "
                           f"~{d.estimate:>13,} B  {f.rel}")
        if est.estimated_zip >= size_limit:
            warnings.append(f"part {plan.index} ({plan.id}) is estimated at {est.estimated_zip:,} B, "
                            f"not below the {size_limit:,} B limit")
        total_raw += est.raw_bytes
        total_zip += est.estimated_zip
        total_files += len(plan.files)
    out.append("")
    out.append(f"total: {total_files} dist files, raw {_size(total_raw)}, estimated zip {_size(total_zip)}")
    out.extend(f"WARNING: {w}" for w in warnings)
    return "\n".join(out)


# --------------------------------------------------------------------------------------------------------------
# Build
# --------------------------------------------------------------------------------------------------------------

def _zipinfo(name: str, method: int) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=ZIP_EPOCH)
    info.compress_type = method
    info.create_system = 3
    info.external_attr = 0o100644 << 16
    return info


def _write_file_entry(archive: zipfile.ZipFile, f: DistFile, method: int):
    info = _zipinfo(f.rel, method)
    info.file_size = f.size  # lets zipfile pick Zip64 up front for a huge entry
    if method == zipfile.ZIP_DEFLATED:
        try:
            info._compresslevel = COMPRESS_LEVEL  # no public setter for a streamed entry; default is 6 anyway
        except AttributeError:
            pass
    digest = hashlib.sha256()
    count = 0
    with f.path.open("rb") as source, archive.open(info, "w") as target:
        for block in iter(lambda: source.read(CHUNK), b""):
            digest.update(block)
            count += len(block)
            target.write(block)
    if count != f.size:
        raise PackagingError(f"dist file changed while packaging: {f.rel} ({f.size} -> {count} bytes)")
    return count, digest.hexdigest()


def _write_bytes_entry(archive: zipfile.ZipFile, name: str, data: bytes, method: int) -> dict:
    archive.writestr(_zipinfo(name, method), data, compress_type=method, compresslevel=COMPRESS_LEVEL)
    return {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def write_part(plan, parts, spec, commit, generated, decisions, dest: Path, probe_bytes: int) -> None:
    """Write one part to a new file (never overwrites). The manifest is the last entry: it needs every hash."""
    entries = []
    with zipfile.ZipFile(dest, "x", compression=zipfile.ZIP_STORED, allowZip64=True) as archive:
        for f in plan.files:
            size, digest = _write_file_entry(archive, f, decisions[f.rel].method)
            entries.append({"path": f.rel, "size": size, "sha256": digest, "origin": "dist"})
        own = generated if plan.id == CORE_ID else {}
        for name in sorted(own):
            data = own[name]
            info = _write_bytes_entry(archive, name, data, decide_bytes(name, data, probe_bytes).method)
            entries.append({"path": name, "origin": "generated", **info})
        mname = manifest_name(plan.id)
        data = manifest_bytes(spec, parts, plan, commit, entries)
        _write_bytes_entry(archive, mname, data, decide_bytes(mname, data, probe_bytes).method)


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


@dataclass
class BuildResult:
    out_dir: Path
    sha256_path: Path
    parts: list      # PartPlan
    zips: list       # Path
    sizes: list
    digests: list


def _is_within(path: Path, root: Path) -> bool:
    resolved = path.resolve()
    base = root.resolve()
    return resolved == base or base in resolved.parents


def sha256_name(spec: ReleaseSpec) -> str:
    return f"{PACKAGE}-{spec.version}.sha256"


def other_parts_of_version(out_dir: Path, spec: ReleaseSpec, keep) -> list:
    """ZIP parts (finished or .partial) of this version in out_dir that are not in `keep`: the leftovers of a
    release with a different part count (a map added or removed within the same version)."""
    if not out_dir.is_dir():
        return []
    pattern = re.compile(re.escape(PACKAGE) + "-" + re.escape(spec.version)
                         + r"-part[0-9]+of[0-9]+-[a-z0-9]+\.zip(?:\.partial)?")
    keep = {p.name for p in keep}
    return sorted(p for p in out_dir.iterdir() if pattern.fullmatch(p.name) and p.name not in keep)


def build_release(spec: ReleaseSpec, dist: Path, out_dir: Path, repo: Path, *, force: bool = False,
                  size_limit: int = SIZE_LIMIT, probe_bytes: int = PROBE_BYTES, commit: str | None = None,
                  log=print) -> BuildResult:
    """Write the parts as .partial files, check each, then rename everything into place. With `force`, only
    stale .partial files are removed up front; an existing release stays untouched until the new set is complete
    (os.replace overwrites atomically), so a failed rebuild is a no-op. Parts of an older part count are removed
    last."""
    if _is_within(out_dir, dist):
        raise PackagingError(f"the output directory must not be inside dist: {out_dir}")
    files = scan_dist(dist)
    parts = partition_dist(files, spec)
    commit = source_commit(repo) if commit is None else commit
    finals = [out_dir / p.zip_name for p in parts]
    partials = [Path(str(path) + ".partial") for path in finals]
    sha_final = out_dir / sha256_name(spec)
    sha_partial = Path(str(sha_final) + ".partial")
    other_counts = other_parts_of_version(out_dir, spec, finals + partials)
    existing = [p for p in finals + partials + [sha_final, sha_partial] if p.exists()] + other_counts
    if existing and not force:
        raise PackagingError("refusing to overwrite existing output (pass --force): "
                             + ", ".join(p.name for p in existing))
    generated = generated_core_entries(spec, parts, repo)
    decisions = {f.rel: decide_file(f, probe_bytes) for f in files}
    out_dir.mkdir(parents=True, exist_ok=True)
    for leftover in partials + [sha_partial] + [p for p in other_counts if p.name.endswith(".partial")]:
        if leftover.is_file():
            leftover.unlink()
    sizes, digests = [], []
    try:
        for plan, partial in zip(parts, partials):
            log(f"writing part {plan.index} of {plan.count} ({plan.id}): {len(plan.files)} files, "
                f"{plan.raw_bytes:,} raw bytes")
            write_part(plan, parts, spec, commit, generated, decisions, partial, probe_bytes)
            size = partial.stat().st_size
            if size >= size_limit:
                raise PartTooLarge(f"{plan.zip_name} is {size:,} B, not below the {size_limit:,} B limit")
            verify_zip(partial, expected_name=plan.zip_name)
            sizes.append(size)
            digests.append(sha256_file(partial))
        sha_partial.write_text("".join(f"{d}  {p.zip_name}\n" for d, p in zip(digests, parts)),
                               encoding="ascii", newline="\n")
    except BaseException:
        for leftover in partials + [sha_partial]:
            if leftover.exists():
                leftover.unlink()
        raise
    for partial, final in zip(partials, finals):
        os.replace(partial, final)
    os.replace(sha_partial, sha_final)
    for old in other_counts:  # only now: the new set is complete, so nothing of the previous one is still needed
        if old.name.endswith(".zip") and old.is_file():
            old.unlink()
    return BuildResult(out_dir, sha_final, parts, finals, sizes, digests)


# --------------------------------------------------------------------------------------------------------------
# Verification
# --------------------------------------------------------------------------------------------------------------

def verify_zip(zip_path: Path, expected_name: str | None = None) -> dict:
    """Read every entry (CRC), compare it with the part manifest inside, and return that manifest."""
    name = expected_name or zip_path.name
    try:
        with zipfile.ZipFile(zip_path) as archive:
            infos = archive.infolist()
            names = [i.filename for i in infos]
            if len(names) != len(set(names)):
                raise VerificationError(f"{name}: duplicate entry names")
            for info in infos:
                try:
                    check_safe_name(info.filename)
                except PackagingError as exc:
                    raise VerificationError(f"{name}: {exc}") from exc
                if info.is_dir() or info.flag_bits & 1:
                    raise VerificationError(f"{name}: directory or encrypted entry {info.filename}")
                if info.compress_type not in ALLOWED_METHODS:
                    raise VerificationError(f"{name}: {info.filename} uses compression method "
                                            f"{info.compress_type}; only STORED and DEFLATED are ever written")
            manifests = [n for n in names if MANIFEST_PATH_RE.fullmatch(n)]
            if len(manifests) != 1:
                raise VerificationError(f"{name}: expected exactly one parts/<id>.json, found {manifests}")
            manifest = json.loads(archive.read(manifests[0]).decode("utf-8"))
            actual = {}
            for info in infos:
                if info.filename == manifests[0]:
                    continue
                digest = hashlib.sha256()
                count = 0
                with archive.open(info) as stream:
                    for block in iter(lambda: stream.read(CHUNK), b""):
                        digest.update(block)
                        count += len(block)
                if count != info.file_size:
                    raise VerificationError(f"{name}: {info.filename} size differs from the ZIP directory")
                actual[info.filename] = (count, digest.hexdigest())
    # NotImplementedError: an unsupported compression method; EOFError / RuntimeError: truncated or damaged streams.
    # Whatever the cause, a damaged ZIP is a verification failure with a message, never a traceback.
    except (zipfile.BadZipFile, OSError, zlib.error, UnicodeDecodeError, json.JSONDecodeError,
            NotImplementedError, EOFError, RuntimeError) as exc:
        raise VerificationError(f"{name}: {exc}") from exc
    _check_manifest(name, manifests[0], manifest, actual)
    return manifest


def _check_manifest(name: str, manifest_path: str, manifest: dict, actual: dict) -> None:
    def fail(message):
        raise VerificationError(f"{name}: {message}")

    try:
        if not isinstance(manifest, dict):
            fail("malformed manifest (not a JSON object)")
        missing = [key for key in MANIFEST_KEYS if key not in manifest]
        if missing:
            fail(f"malformed manifest (missing {missing})")
        if manifest["schema"] != MANIFEST_SCHEMA:
            fail(f"unsupported manifest schema {manifest['schema']!r}")
        if not isinstance(manifest["version"], str) or not isinstance(manifest["source_commit"], str):
            fail("malformed manifest (version and source_commit must be strings)")
        part = manifest["part"]
        parts = manifest["parts"]
        if part["id"] != MANIFEST_PATH_RE.fullmatch(manifest_path).group(1):
            fail("manifest file name does not match its part id")
        if part["zip"] != name:
            fail(f"manifest names {part['zip']!r} as its own ZIP")
        if (not isinstance(part["index"], int) or not isinstance(part["of"], int)
                or not 1 <= part["index"] <= part["of"]
                or len(parts) != part["of"] or [p["index"] for p in parts] != list(range(1, part["of"] + 1))
                or parts[part["index"] - 1]["id"] != part["id"] or parts[part["index"] - 1]["zip"] != name):
            fail("manifest part list is inconsistent with this part")
        listed = {}
        for entry in manifest["entries"]:
            if entry["path"] in listed:
                fail(f"manifest lists {entry['path']} twice")
            if entry["origin"] not in ("dist", "generated"):
                fail(f"manifest entry {entry['path']} has unknown origin {entry['origin']!r}")
            listed[entry["path"]] = entry
        if set(listed) != set(actual):
            fail(f"manifest and ZIP entries differ: only in manifest {sorted(set(listed) - set(actual))}, "
                 f"only in ZIP {sorted(set(actual) - set(listed))}")
        for path, (size, digest) in actual.items():
            if listed[path]["size"] != size or listed[path]["sha256"] != digest:
                fail(f"size or SHA-256 mismatch: {path}")
        dist_entries = [e for e in listed.values() if e["origin"] == "dist"]
        mine = parts[part["index"] - 1]
        if mine["dist_files"] != len(dist_entries) or mine["raw_bytes"] != sum(e["size"] for e in dist_entries):
            fail("manifest dist file count or raw bytes differ from its entries")
    except (KeyError, TypeError, IndexError) as exc:
        fail(f"malformed manifest ({exc!r})")


@dataclass
class VerifiedPart:
    index: int
    id: str
    zip_name: str
    path: Path
    sha256: str
    size: int
    manifest: dict


@dataclass
class VerifiedSet:
    version: str
    sha256_path: Path
    parts: list


def find_sha256_file(directory: Path, version: str | None) -> tuple:
    if not directory.is_dir():
        raise VerificationError(f"not a directory: {directory}")
    pattern = re.compile(re.escape(PACKAGE) + r"-(" + VERSION_RE.pattern + r")\.sha256")
    found = []
    for path in sorted(directory.iterdir()):
        match = pattern.fullmatch(path.name)
        if match and not path.name.endswith(".zip.sha256") and (version is None or match.group(1) == version):
            found.append((match.group(1), path))
    if len(found) != 1:
        raise VerificationError(f"expected exactly one {PACKAGE}-<version>.sha256 in {directory}"
                                + (f" for version {version}" if version else "") + f", found {len(found)}")
    return found[0]


def check_partition(parts) -> None:
    """The invariant partition_dist establishes, re-asserted on the finished ZIPs (extract_check cannot see it:
    the union of the parts still equals dist when a file sits in the wrong part). Part 1 is the core; every map part
    carries its three bundles and its database entry; nothing of map X's bundles or db/ folder is in any other part."""
    ids = [p.id for p in parts]
    if len(set(ids)) != len(ids) or ids[0] != CORE_ID or CORE_ID in ids[1:]:
        raise VerificationError(f"part ids must be unique with '{CORE_ID}' first: {ids}")
    paths = {p.id: {e["path"] for e in p.manifest["entries"]} for p in parts}
    for part in parts[1:]:
        key = f"arena_{part.id}"
        required = [f"{PLUGIN_ROOT}/bundles/{key}_{kind}.bundle" for kind in ("scenes", "navmesh", "preset")]
        required.append(f"{SERVER_ROOT}/db/{key}/base.json")
        missing = [name for name in required if name not in paths[part.id]]
        if missing:
            raise VerificationError(f"{part.zip_name}: map part lacks {missing}")
    for part in parts:
        for other in parts[1:]:
            if other.id == part.id:
                continue
            key = f"arena_{other.id}"
            foreign = sorted(name for name in paths[part.id]
                             if name.startswith(f"{SERVER_ROOT}/db/{key}/")
                             or name.startswith(f"{PLUGIN_ROOT}/bundles/{key}_"))
            if foreign:
                raise VerificationError(f"{part.zip_name} carries files that belong to part '{other.id}': {foreign}")


def verify_set(directory: Path, version: str | None = None, log=print, size_limit: int | None = None) -> VerifiedSet:
    """Re-hash every part against the .sha256 and check each part's manifest against its content and the others,
    the per-part size gate and the partition invariant (see check_partition)."""
    limit = SIZE_LIMIT if size_limit is None else size_limit
    version, sha_path = find_sha256_file(directory, version)
    text = sha_path.read_bytes().decode("ascii", errors="replace")
    lines = text.splitlines()
    if not text.endswith("\n") or not lines:
        raise VerificationError(f"{sha_path.name}: empty or missing final newline")
    expected = []
    for line in lines:
        match = SHA256_LINE_RE.fullmatch(line)
        if not match:
            raise VerificationError(f"{sha_path.name}: malformed line {line!r}")
        expected.append((match.group(1), match.group(2)))
    names = [zip_name for _, zip_name in expected]
    if len(set(names)) != len(names):
        raise VerificationError(f"{sha_path.name}: duplicate part")
    part_re = re.compile(re.escape(PACKAGE) + "-" + re.escape(version) + r"-part([0-9]+)of([0-9]+)-([a-z0-9]+)\.zip")
    stray = sorted(p.name for p in directory.iterdir()
                   if part_re.fullmatch(p.name) and p.name not in names)
    if stray:
        raise VerificationError(f"ZIP parts present but not listed in {sha_path.name}: {stray}")
    verified = []
    for position, (digest, zip_name) in enumerate(expected, 1):
        match = part_re.fullmatch(zip_name)
        if not match or int(match.group(1)) != position or int(match.group(2)) != len(expected):
            raise VerificationError(f"{sha_path.name}: {zip_name} is not part {position} of {len(expected)}")
        path = directory / zip_name
        if not path.is_file():
            raise VerificationError(f"missing part: {zip_name}")
        actual = sha256_file(path)
        if actual != digest:
            raise VerificationError(f"SHA-256 mismatch: {zip_name} (listed {digest}, actual {actual})")
        size = path.stat().st_size
        if size >= limit:
            raise VerificationError(f"{zip_name} is {size:,} B, not below the {limit:,} B limit")
        manifest = verify_zip(path)
        if (manifest["part"]["id"] != match.group(3) or manifest["part"]["index"] != position
                or manifest["part"]["of"] != len(expected)):
            raise VerificationError(f"{zip_name}: manifest part id/index/count disagree with the file name")
        verified.append(VerifiedPart(position, match.group(3), zip_name, path, digest, size, manifest))
        log(f"verified part {position} of {len(expected)}: {zip_name}")
    first = verified[0].manifest
    seen = {}
    for part in verified:
        for key in ("version", "source_commit", "parts"):
            if part.manifest[key] != first[key]:
                raise VerificationError(f"{part.zip_name}: manifest field '{key}' differs from part 1")
        for entry in part.manifest["entries"]:
            other = seen.setdefault(entry["path"], part.zip_name)
            if other != part.zip_name:
                raise VerificationError(f"{entry['path']} appears in both {other} and {part.zip_name}")
        listed = first["parts"][part.index - 1]
        if listed["zip"] != part.zip_name:
            raise VerificationError(f"{part.zip_name}: the parts list names {listed['zip']} for this index")
    if first["version"] != version:
        raise VerificationError(f"manifest version {first['version']} differs from the file name version {version}")
    if [p["zip"] for p in first["parts"]] != names:
        raise VerificationError(f"{sha_path.name} and the part manifests list different ZIPs")
    check_partition(verified)
    return VerifiedSet(version, sha_path, verified)


def check_scratch(scratch: Path, dist: Path) -> None:
    """Preconditions of the extract-check scratch directory. run() calls this BEFORE building or verifying so a
    bad path does not surface only after minutes of work and a complete, unchecked set left behind."""
    if scratch.exists() and (not scratch.is_dir() or any(scratch.iterdir())):
        raise PackagingError(f"extract-check scratch directory must be empty or absent: {scratch}")
    if _is_within(scratch, dist):
        raise PackagingError(f"the extract-check scratch directory must not be inside dist: {scratch}")


def extract_check(verified: VerifiedSet, dist: Path, scratch: Path, log=print) -> int:
    """Extract all parts into an empty scratch directory and compare it byte for byte with dist/."""
    check_scratch(scratch, dist)
    scratch.mkdir(parents=True, exist_ok=True)
    root = scratch.resolve()
    generated = set()
    for part in verified.parts:
        generated.add(manifest_name(part.id))
        generated.update(e["path"] for e in part.manifest["entries"] if e["origin"] == "generated")
        with zipfile.ZipFile(part.path) as archive:
            for info in archive.infolist():
                check_safe_name(info.filename)
                target = root.joinpath(*info.filename.split("/"))
                if root not in target.resolve().parents:
                    raise VerificationError(f"{part.zip_name}: entry escapes the scratch directory: {info.filename}")
                target.parent.mkdir(parents=True, exist_ok=True)
                try:
                    with archive.open(info) as source, open(target, "xb") as sink:
                        shutil.copyfileobj(source, sink, CHUNK)
                except FileExistsError as exc:
                    raise VerificationError(f"{info.filename} is extracted by more than one part") from exc
    extracted = {f.rel: f for f in scan_dist(root)}
    original = {f.rel: f for f in scan_dist(dist)}
    for name in generated:
        if name not in extracted:
            raise VerificationError(f"generated entry missing after extraction: {name}")
    shipped = {name: f for name, f in extracted.items() if name not in generated}
    if set(shipped) != set(original):
        raise VerificationError(f"extracted files differ from dist: missing {sorted(set(original) - set(shipped))}, "
                                f"unexpected {sorted(set(shipped) - set(original))}")
    for name, f in sorted(shipped.items()):
        if f.size != original[name].size or not filecmp.cmp(f.path, original[name].path, shallow=False):
            raise VerificationError(f"extracted file differs from dist: {name}")
    log(f"extract-check OK: {len(shipped)} files identical to dist, {len(generated)} generated entries ignored")
    return len(shipped)


# --------------------------------------------------------------------------------------------------------------
# Command line
# --------------------------------------------------------------------------------------------------------------

def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dist", help="distribution tree (default <repo>/dist)")
    parser.add_argument("--out", help="output directory (default <repo>/work/releases/<version>)")
    parser.add_argument("--version", help="release version (default: the agreeing csproj <Version>)")
    parser.add_argument("--plan", action="store_true", help="print the partition and estimates; write nothing")
    parser.add_argument("--force", action="store_true",
                        help="replace an existing release of this version; the old files stay until the new set "
                             "is complete, so a failed rebuild changes nothing")
    parser.add_argument("--allow-dirty", action="store_true",
                        help="skip the clean git tree gate (the manifests then record '<commit>+dirty')")
    parser.add_argument("--verify-only", metavar="DIR", help="verify the parts in DIR instead of building")
    parser.add_argument("--extract-check", metavar="SCRATCH",
                        help="after building / verifying, extract all parts into this EMPTY directory and "
                             "compare with dist")
    args = parser.parse_args(argv)
    if args.plan and (args.verify_only or args.extract_check or args.force):
        parser.error("--plan writes nothing and cannot be combined with --verify-only, --extract-check or --force")
    if args.verify_only and (args.force or args.out):
        parser.error("--verify-only reads an existing directory; --out and --force do not apply")
    return args


def warn_unidentified_source(verified: VerifiedSet) -> None:
    commit = verified.parts[0].manifest["source_commit"]
    if not COMMIT_RE.fullmatch(commit):
        print(f"WARNING: this release was built from an unidentified or uncommitted source ({commit!r})",
              file=sys.stderr)


def run(args, repo: Path) -> int:
    dist = Path(args.dist) if args.dist else repo / "dist"
    if args.extract_check:
        check_scratch(Path(args.extract_check), dist)  # before any work, not after a finished build
    if args.verify_only:
        verified = verify_set(Path(args.verify_only), args.version)
        print(f"RELEASE_ZIPS_VERIFIED {verified.version} ({len(verified.parts)} parts)")
        warn_unidentified_source(verified)
        if args.extract_check:
            extract_check(verified, dist, Path(args.extract_check))
        return 0
    spec = load_spec(repo, args.version, default_maps())
    if args.plan:
        files = scan_dist(dist)
        parts = partition_dist(files, spec)
        commit = source_commit(repo)
        decisions = {f.rel: decide_file(f) for f in files}
        estimates = estimate_parts(spec, parts, decisions, generated_core_entries(spec, parts, repo), commit)
        print(format_plan(spec, dist, commit, estimates, decisions, SIZE_LIMIT))
        return 0
    if not args.allow_dirty:
        require_clean_tree(repo)
    out = Path(args.out) if args.out else repo / "work" / "releases" / spec.version
    result = build_release(spec, dist, out, repo, force=args.force,
                           commit=resolve_commit(repo, args.allow_dirty))
    for plan, path, size, digest in zip(result.parts, result.zips, result.sizes, result.digests):
        print(f"{path.name}  {size:,} B  {digest}")
    print(f"wrote {result.sha256_path}")
    if args.extract_check:
        extract_check(verify_set(out, spec.version), dist, Path(args.extract_check))
    print("RELEASE_ZIPS_BUILT")
    return 0


def main(argv=None, repo: Path = REPO) -> int:
    args = parse_args(argv)
    try:
        return run(args, repo)
    except (PackagingError, OSError) as exc:  # OSError: unwritable --out, full disk, locked file, vanished input
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
