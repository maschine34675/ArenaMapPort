#!/usr/bin/env python3
"""Package the staged distribution (dist/) as release ZIP(s): ONE ZIP (--single) or a multi-part set.

dist/ holds two install roots, `BepInEx/plugins/maschine-ArenaMapPort/...` (client) and
`SPT_Runtime/user/mods/ArenaMapPort/...` (server). Both layouts install by extracting into the SPT root.

Two layouts, one classification, one verifier:

  --single   (the release form the user chose; about 3.4 GiB with the four encrypted maps of 0.3.0, hosted on Pixeldrain, not GitHub)
      maschine-ArenaMapPort-<version>.zip     EVERY dist file, the generated README.txt and
                                              THIRD-PARTY-NOTICES.txt entries, and the manifest
                                              BepInEx/plugins/maschine-ArenaMapPort/parts/full.json
                                              (part index 1 of 1, id "full")
      maschine-ArenaMapPort-<version>.sha256  one line: '<sha256>  <zip name>'
      The 2**31 per-ZIP size gate does NOT apply here: it exists because GitHub-style release channels cap
      a single file near 2 GiB, while Pixeldrain takes multi-GB files. The final size is printed instead.
      allowZip64 stays on, so entries or an archive beyond 4 GiB would still be written correctly.

  (default)  the multi-part layout, kept available for channels with a file size cap. The release is cut into
      parts that install by extracting EVERY part into the SPT root:

      part1ofN-core         client DLL, server DLL, original map descriptions, selection artwork, score monitor sprites,
                            README, notices
      part2ofN-<map stem>   one part per released map: bundles, db/<key>/base.json, acoustics, gate sounds, AI sidecar,
                            spectators, flares, water, loading screens, overview map, shader sidecar, banner

      maschine-ArenaMapPort-<version>-part<i>of<N>-<id>.zip     one per part
      maschine-ArenaMapPort-<version>.sha256                    '<sha256>  <zip name>' per part, in part order

The released maps and their order come from tools/validate/validate_map_descriptors.py
(RELEASE_MAP_KEYS / RELEASE_MAP_NAMES).

Encrypted bundles (stage 2 of the Arena gate, docs/PLAN-ENCRYPTION.md): every bundle in dist is an AMPB container
and every released map carries its key file keys/<key>.json (Arena file names, sizes and the sample, never a hash). The packer
refuses a plaintext bundle twice: on the dist files before anything is written (the first four bytes must be
"AMPB") and on the finished ZIPs (the same four bytes of every bundle entry); a map part without its key file is
not installable. Ciphertext does not compress, so the bundles are STORED. The version comes from the `<Version>` of both csproj files, which must
agree (`--version` overrides). Outputs land under `--out` (default work/releases/<version>/).

Classification is strict and anchored and identical for both layouts: every dist file must match exactly one
rule (see build_rules). A file that matches none (a parked map's output such as arena_airpit2_scenes.bundle or
ai/airpit2.ai.json, a stray .pdb or backup, a Thumbs.db or .bak inside an acoustics / gate-sounds / flares / water /
dynamicmaps folder) or more than one is a hard error, never a silent skip; so is a directory that cannot be
listed. The rules pin the leaf names the build stages, and required_files() demands everything every released
map must carry (bundles, key file, database entry, shader sidecar, AI sidecar, banner, acoustic graph, gate sound
manifest, spectator package, first loading screen); only flares, water and the overview map are optional per map. Every ZIP carries
BepInEx/plugins/maschine-ArenaMapPort/parts/<id>.json, a schema-1 manifest naming ALL parts (one, in the single
layout) plus this ZIP's own entries with size and SHA-256, so any ZIP says which others complete the install.
The core part (or the single ZIP) adds README.txt and THIRD-PARTY-NOTICES.txt (client and server root); the
notices are cut from the MIT blocks of THIRD-PARTY-NOTICES.md. The manifest is always the last entry.

Compression is chosen per file: ZIP_STORED for .jpg and for any file whose probe (deflate level 6 over the first
8 MiB) gains less than 3 %, ZIP_DEFLATED level 6 otherwise. Timestamps are fixed (1980-01-01) so identical inputs
give identical ZIP bytes. Multi-part: every part must end up below 2**31 bytes; single: no size gate.

Modes:
    --plan                    print the partition (or the one archive) and size estimates, write nothing
    (default)                 build the multi-part ZIPs and the .sha256 (clean git tree required unless
                              --allow-dirty; an allowed dirty tree is recorded as '<commit>+dirty' in every
                              manifest)
    --single                  build the single-ZIP layout instead; combines with --plan, --force, --allow-dirty,
                              --out, --version and --extract-check
    --force                   replace an existing release of this version, in EITHER layout. The previous files
                              of this version (both layouts, finished or .partial, any part count) are only
                              touched after the new set is complete and verified: they are then renamed to
                              '<name>.superseded', the new set is renamed into place and the renamed copies are
                              deleted. Any failure before that point (a locked old file, a failing rename, Ctrl-C)
                              removes the new files and renames the old ones back, so a failed rebuild changes
                              nothing and leaves no .partial file. A superseded copy that cannot be deleted at the
                              very end is reported as a warning and is harmless (it is not a release ZIP).
                              Without --force any existing release of this version in the output directory,
                              of either layout, is refused.
    --verify-only <dir>       re-hash the existing ZIP(s) against the .sha256 and check every manifest. The layout
                              is detected from the .sha256 (one line naming <package>-<version>.zip = single).
                              Multi-part also checks the size gate and the partition invariant (each map's
                              bundles and database entry travel together in that map's part, nowhere else);
                              single checks that the one archive carries the three bundles and db/<key>/base.json
                              of EVERY RELEASED map (RELEASE_MAP_KEYS, not just the maps it happens to mention,
                              so a ZIP that lost a whole map fails) plus the generated README and notices.
                              Cannot be combined with --single.
    --extract-check <scratch> after building (or after --verify-only): extract all ZIPs (the one ZIP, or every
                              part) into an EMPTY scratch directory and compare byte for byte with dist/,
                              ignoring generated entries. The scratch directory is validated before anything
                              is built.

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
# Part id of the single-ZIP layout: its manifest is parts/full.json (index 1 of 1). Reserved like CORE_ID: a map
# stem may be neither.
FULL_ID = "full"
LAYOUT_PARTS = "parts"
LAYOUT_SINGLE = "single"
# Every map's shader sidecar is material-shaders.<key>.json (ShaderRebind.cs, ArenaMapPort.Client.csproj
# ArenaMapPortStageWpOutputs); Equator's pre-generalisation "material-shaders.json" was renamed on 2026-10-17.
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

AMPB_MAGIC = b"AMPB"  # the first four bytes of every distributed bundle (ArenaMapPort.Crypto, docs/PLAN-ENCRYPTION.md 0.5)
BUNDLE_ENTRY_RE = re.compile(re.escape(f"{PLUGIN_ROOT}/bundles/") + r"arena_[a-z0-9]+_(?:scenes|navmesh|preset)\.bundle")

README_NAME = f"{PLUGIN_ROOT}/README.txt"
CLIENT_NOTICES_NAME = f"{PLUGIN_ROOT}/THIRD-PARTY-NOTICES.txt"
SERVER_NOTICES_NAME = f"{SERVER_ROOT}/THIRD-PARTY-NOTICES.txt"
DESCRIPTIONS_NAME = f"{SERVER_ROOT}/locales/map-descriptions.json"
SELECTION_MAP_NAMES = (f"{PLUGIN_ROOT}/map-selection/region-map.png",
                       f"{PLUGIN_ROOT}/map-selection/manifest.json")
# The artwork of the score monitor faces (docs/progress/score-monitors.md): one manifest and one PNG per sprite, the same for every map, so
# they travel in the core part. The rule pins the folder and leaf shape; validate_distribution.py checks the exact file set.
MONITORS_MANIFEST_NAME = f"{PLUGIN_ROOT}/monitors/monitors.json"
SPECTATOR_AUDIO_MANIFEST_NAME = f"{PLUGIN_ROOT}/spectator-audio/spectator-audio.json"
SPECTATOR_TEXTURES_MANIFEST_NAME = f"{PLUGIN_ROOT}/spectators/shared/spectator-textures.json"


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
    if len(set(stems)) != len(stems) or CORE_ID in stems or FULL_ID in stems:
        raise PackagingError(f"map stems must be unique and not {CORE_ID!r} or {FULL_ID!r}: {stems}")
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
    return f"material-shaders.{key}.json"


def build_rules(spec: ReleaseSpec) -> list:
    """Exactly the dist layout the build stages. Every pattern is matched with fullmatch, and the key / stem /
    display name is escaped and followed by its delimiter ('/', '_', '.'), so the parked arena_airpit2 can never
    match arena_airpit (and ai/airpit2.ai.json never matches ai/airpit.ai.json). The folder rules (acoustics,
    flares, water, dynamicmaps, gate sounds, score monitors) pin the leaf names the build stages (ArenaMapPort.Client.csproj, the spatial-audio
    and flare extractors), flat or one 'textures/' / 'Layers/' level deep, so a stray Thumbs.db, desktop.ini,
    .bak, .git folder or nested leftover is a classification error instead of shipping."""
    rules = []

    def add(part, label, pattern):
        rules.append(Rule(part, label, re.compile(pattern)))

    add(CORE_ID, "client DLL", re.escape(spec.client_dll))
    add(CORE_ID, "server DLL", re.escape(spec.server_dll))
    add(CORE_ID, "map descriptions", re.escape(DESCRIPTIONS_NAME))
    for name in SELECTION_MAP_NAMES:
        add(CORE_ID, "selection map", re.escape(name))
    add(CORE_ID, "score monitors", re.escape(f"{PLUGIN_ROOT}/monitors/") + rf"(?:monitors\.json|{LEAF}\.png)")
    add(CORE_ID, "spectator audio", re.escape(f"{PLUGIN_ROOT}/spectator-audio/") + r"(?:spectator-audio\.json|arena_fan(?:[7-9]|1[0-3])_participating_\d{2}\.wav)")
    add(CORE_ID, "spectator textures", re.escape(f"{PLUGIN_ROOT}/spectators/shared/") + r"(?:spectator-textures\.json|[0-9a-f]{16}(?:-[0-5])?\.png)")
    for m in spec.maps:
        key = re.escape(m.key)
        add(m.stem, "bundle", re.escape(f"{PLUGIN_ROOT}/bundles/") + key + r"_(?:scenes|navmesh|preset)\.bundle")
        add(m.stem, "key file", re.escape(f"{PLUGIN_ROOT}/keys/{m.key}.json"))
        add(m.stem, "acoustics", re.escape(f"{PLUGIN_ROOT}/acoustics/{m.key}/")
            + rf"(?:spatial-audio\.json|source\.audiobakedata|{LEAF}\.wav)")
        add(m.stem, "gate sounds", re.escape(f"{PLUGIN_ROOT}/gate-sounds/{m.key}/") + rf"(?:gate-sounds\.json|{LEAF}\.wav)")
        add(m.stem, "spectators", re.escape(f"{PLUGIN_ROOT}/spectators/{m.key}/spectator.json.gz"))
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
    tools/validate/validate_distribution.py treats as mandatory per released map: a map without its key file cannot
    unlock its encrypted bundles, a missing AI sidecar silently falls back to the NavMesh-synthesised AI, a missing
    shader sidecar leaves pink materials, a map without its gate sound manifest has silent gates, and a map without its
    acoustic graph, banner or loading screens is not the map the README promises; the core part needs the score monitor
    manifest the client reads when a mode plugin claims the TV screens. Only flares, water and the
    DynamicMaps overview are optional (AirPit has an overview but no flares or water package)."""
    required = {CORE_ID: [spec.client_dll, spec.server_dll, DESCRIPTIONS_NAME, *SELECTION_MAP_NAMES, MONITORS_MANIFEST_NAME, SPECTATOR_AUDIO_MANIFEST_NAME, SPECTATOR_TEXTURES_MANIFEST_NAME]}
    for m in spec.maps:
        names = [f"{PLUGIN_ROOT}/bundles/{m.key}_{kind}.bundle" for kind in ("scenes", "navmesh", "preset")]
        names += [f"{SERVER_ROOT}/db/{m.key}/base.json",
                  f"{PLUGIN_ROOT}/keys/{m.key}.json",
                  f"{PLUGIN_ROOT}/{shader_sidecar_name(m.key)}",
                  f"{PLUGIN_ROOT}/ai/{m.stem}.ai.json",
                  f"{SERVER_ROOT}/banners/{m.key}.jpg",
                  f"{PLUGIN_ROOT}/acoustics/{m.key}/spatial-audio.json",
                  f"{PLUGIN_ROOT}/gate-sounds/{m.key}/gate-sounds.json",
                  f"{PLUGIN_ROOT}/spectators/{m.key}/spectator.json.gz",
                  f"{PLUGIN_ROOT}/loadscreens/{m.key}_1.jpg"]
        required[m.stem] = names
    return required


def zip_name_for(spec: ReleaseSpec, index: int, count: int, part_id: str) -> str:
    return f"{PACKAGE}-{spec.version}-part{index}of{count}-{part_id}.zip"


def require_encrypted_bundle(f: DistFile) -> None:
    """A bundle that is not an AMPB container is a plaintext map: the release must never carry one (stage 2 of the
    Arena gate). Four bytes are enough here; tools/validate/validate_distribution.py authenticates the whole container."""
    try:
        with f.path.open("rb") as stream:
            head = stream.read(4)
    except OSError as exc:
        raise PackagingError(f"cannot read {f.path}: {exc}") from exc
    if head != AMPB_MAGIC:
        raise PackagingError(f"plaintext bundle in dist (not an AMPB container): {f.rel}; "
                             "run a full, non-DllOnly client build so dist/ is restaged encrypted")


def partition_dist(files, spec: ReleaseSpec, rules=None) -> list:
    """Assign every dist file to exactly one part. A map's database entry always travels with its bundles, and
    every bundle must be an encrypted AMPB container (require_encrypted_bundle)."""
    rules = build_rules(spec) if rules is None else rules
    ids = [CORE_ID] + [m.stem for m in spec.maps]
    buckets = {part_id: [] for part_id in ids}
    for f in files:
        rule = classify(f.rel, rules)
        if rule.label == "bundle":
            require_encrypted_bundle(f)
        buckets[rule.part].append(f)
    present = {f.rel for f in files}
    for part_id, names in required_files(spec).items():
        missing = [name for name in names if name not in present]
        if missing:
            raise PackagingError(f"incomplete dist: part '{part_id}' lacks {missing}")
    count = len(ids)
    return [PartPlan(i, count, part_id, zip_name_for(spec, i, count, part_id), buckets[part_id])
            for i, part_id in enumerate(ids, 1)]


def single_zip_name(version: str) -> str:
    return f"{PACKAGE}-{version}.zip"


def partition_single(files, spec: ReleaseSpec, rules=None) -> list:
    """The single-ZIP layout: ONE PartPlan (index 1 of 1, id 'full') holding every dist file. The files first go
    through partition_dist, so the strict classification (a stray or parked file is a hard error) and the
    required_files demands are exactly those of the multi-part layout; only the grouping differs. The files stay
    sorted by relative path, the order scan_dist produced, so the ZIP bytes are reproducible."""
    everything = [f for part in partition_dist(files, spec, rules) for f in part.files]
    everything.sort(key=lambda f: f.rel)
    return [PartPlan(1, 1, FULL_ID, single_zip_name(spec.version), everything)]


def is_single_layout(parts) -> bool:
    return len(parts) == 1 and parts[0].id == FULL_ID


def carries_generated(plan: PartPlan) -> bool:
    """The core part and the single ZIP hold the generated README and notices; map parts hold only their manifest."""
    return plan.id in (CORE_ID, FULL_ID)


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
    if is_single_layout(parts):
        return ("Close EFT and the SPT server, then extract this ZIP into the SPT root directory (the folder "
                "that contains BepInEx and SPT_Runtime), merging folders and replacing existing ArenaMapPort files.")
    return (f"Close EFT and the SPT server, then extract ALL {len(parts)} parts of this release into the SPT "
            "root directory (the folder that contains BepInEx and SPT_Runtime), merging folders and replacing "
            "existing ArenaMapPort files. The parts do not overlap and can be extracted in any order.")


# The Arena installation requirement (release 0.2.3, docs/progress/arena-install-check.md). The same statement as
# README.md, FORGE.md and docs/releases/v0.2.3.md (a good-faith installation check on names and sizes, never copy protection),
# plus, since the in-game map notices of 0.3.0, the clause that a message at the main menu names the reason.
REQUIREMENT_TEXT = (
    "Requirement: Escape from Tarkov: Arena must be installed. It is sold separately from Escape from\n"
    "Tarkov, and these maps are built from its files, so the server registers the locations and the client\n"
    "loads the map files only when Arena is found (the Battlestate Games launcher's entry in the Windows\n"
    "registry) and its folder is complete: the game executable and the launcher's file list (ConsistencyInfo)\n"
    "must be there and every listed file must exist with the listed size. The installation check compares only\n"
    "names and sizes, reads no game data and sends nothing anywhere. If Arena is installed without a launcher entry, set\n"
    "its folder in General / ArenaInstallPath (BepInEx/config/com.maschine.ArenaMapPort.cfg) and in\n"
    "SPT_Runtime/user/mods/ArenaMapPort/config/arena-install.json, for example\n"
    "{\"installPath\": \"D:/Battlestate Games/Escape from Tarkov Arena\"}; both are checked like the automatic\n"
    "path, and nothing turns the check off. Without a verified installation both components log the reason\n"
    "and the Arena locations do not appear; the game shows a message at the main menu that names the reason\n"
    "and what to do (General / ShowMapNotices hides that message; it never turns the check off).\n\n"
)

# The Arena build the shipped key files are built for: the one installed on the build VM when `stage` wrote them (the launcher
# manifest's version, which the client prints as "Arena: found <version>"). tools/bundle/key-sources.json ("arena_build"),
# README.md, FORGE.md and the release notes name the same build; RealRepoTests pins the equality. Raise it with the key files.
KEYS_BUILT_FOR = "0.6.1.0.48206"

# The encrypted bundles (stage 2, docs/PLAN-ENCRYPTION.md, key source of section 6). The same four statements as README.md and
# the release notes: encrypted with a key derived from the player's Arena files, not copy protection, art and audio stay
# plaintext, and the build the keys are built for (and the 0.6.0 build verified to open them, its resource files being identical).
ENCRYPTION_TEXT = (
    "Encrypted bundles: the map bundles in bundles/ (scenes, materials, textures, NavMesh) are encrypted with a key\n"
    "that your PC derives from the Arena resource files each map is built from (the texture and mesh data of its\n"
    "scenes; a fixed sample of a few MB is read when the map is first used and never sent anywhere; keys/<map>.json\n"
    "lists the files by path and size only). They work only next to your own Arena files. The keys in this release\n"
    f"are built for Arena {KEYS_BUILT_FOR} and also open on 0.6.0.0.47752, whose resource files are identical (your\n"
    "build is in the client log at startup, \"Arena: found ...\"); if an Arena update changes a map's resource files,\n"
    "that map does not load (the client log names the cause) until an updated ArenaMapPort package carries the key\n"
    "for your build, and the other maps keep working. This is a requirement, not copy protection: anyone who owns\n"
    "Arena can derive the key. Loading screens, banners, audio, overview maps and map descriptions are not encrypted.\n\n"
)


def readme_text(spec: ReleaseSpec, parts) -> str:
    """The generated README.txt. The multi-part wording is fixed; the single-ZIP layout replaces exactly the
    three paragraphs that depend on it (what the release consists of, the extraction step, the verification)."""
    names = ", ".join(m.display for m in spec.maps)
    sha_name = f"{PACKAGE}-{spec.version}.sha256"
    if is_single_layout(parts):
        intro = (f"This release is ONE ZIP: {parts[0].zip_name}. It holds the plugin, the server mod and the\n"
                 f"data of every included map ({names}); there is nothing else to download.\n\n")
        extract = ("2. Extract the ZIP into your SPT installation directory (the folder that contains\n"
                   "   BepInEx and SPT_Runtime). Merge the folders and replace existing ArenaMapPort files\n"
                   "   when prompted.\n")
        verification = (f"Verification: {sha_name} lists the SHA-256 of the ZIP\n"
                        "(Get-FileHash in PowerShell). BepInEx/plugins/maschine-ArenaMapPort/parts/full.json records\n"
                        "the files it installed.\n\n")
    else:
        display = {m.stem: m.display for m in spec.maps}
        lines = []
        for p in parts:
            what = ("plugin, server mod, shared spectator textures/calls, original map descriptions, selection artwork, score monitor sprites, notices" if p.id == CORE_ID
                    else f"{display[p.id]} map data (bundles, NavMesh, server database entry, audio, art)")
            lines.append(f"  {p.index} of {p.count}  {p.zip_name}\n         {what}")
        intro = (f"This release comes in {len(parts)} ZIP parts. Install ALL of them; a map is complete only with\n"
                 "its own part and the core part.\n\n"
                 f"{chr(10).join(lines)}\n\n")
        extract = ("2. Extract EVERY part into your SPT installation directory (the folder that contains\n"
                   "   BepInEx and SPT_Runtime). Merge the folders and replace existing ArenaMapPort files\n"
                   "   when prompted. The parts do not overlap and can be extracted in any order.\n")
        verification = (f"Verification: {sha_name} lists the SHA-256 of every part\n"
                        "(Get-FileHash in PowerShell). BepInEx/plugins/maschine-ArenaMapPort/parts/ records, per part,\n"
                        "the files it installed.\n\n")
    return (f"ArenaMapPort {spec.version}\n\n"
            "Target: SPT 4.1.x / EFT 0.16.9.5\n\n"
            + REQUIREMENT_TEXT
            + ENCRYPTION_TEXT +
            intro +
            "Installation\n"
            "1. Close the game and the SPT server.\n"
            + extract +
            "3. When upgrading from 0.2.1 or a development build, also remove the former BepInEx/plugins/ArenaMapPort\n"
            "   folder (the plugin folder is now maschine-ArenaMapPort), the former\n"
            "   SPT_Runtime/user/mods/ArenaMapPort/db/base.json, the former\n"
            "   BepInEx/plugins/maschine-ArenaMapPort/material-shaders.json (Equator's shader sidecar is now\n"
            "   material-shaders.arena_equator.json), and the development copies\n"
            "   DynamicMaps/Maps/ArenaMapPort_Equator and ArenaMapPort_Bay5 if still present, to avoid\n"
            "   duplicate maps. Unused arena-audio and arena-ui folders may be removed too.\n"
            "4. Start the SPT server, then the game.\n\n"
            "Updating: extract the complete release package (ALL parts if split) with the SPT server closed, so the client\n"
            "and the server DLL, the bundles, the keys folder and the server database all come from this release (a\n"
            "running server keeps its DLL locked); keep BepInEx/config. The keys folder\n"
            "(BepInEx/plugins/maschine-ArenaMapPort/keys) belongs to the bundles next to it: a missing or damaged key\n"
            "file is reported as an error in the client log, and the maps do not load.\n\n"
            "If the maps do not show up: the game shows a message at the main menu that ends with a ref code, for\n"
            "example \"ref NO_ARENA\". The refs and their fixes are listed in the project README, section \"If the maps\n"
            "do not show up\" (https://github.com/maschine34675/ArenaMapPort). When you report a problem, include the\n"
            "ref and the lines marked [Arena] from BepInEx/LogOutput.log.\n\n"
            f"Included locations: {names}.\n"
            "These maps are intended for ArenaMode; no loot or scav raids are provided.\n"
            "ArenaMode and other mods are not included.\n"
            "Known audio limits: intermittent short noise reported on Equator remains unresolved; no noise fix is\n"
            "claimed. The Books impact-sound bank is missing, so those prop impacts remain silent.\n"
            "The gates and vehicles at the team spawn enclosures use Arena's own open positions and, where Arena\n"
            "has them, its motions (at half their time). They stand open from raid start, or a mode plugin such as\n"
            "ArenaMode holds them closed and opens them when the fight starts; Air Pit's passport control stays\n"
            "shut until a mode plugin opens it. A few doors that Arena itself never moves (three warehouse\n"
            "rollers on Bay5, the FCK shop's roller shutter on Equator) stay shut.\n\n"
            "Optional DynamicMaps integration: overview map data lives in this plugin's own dynamicmaps\n"
            "folder and is registered automatically when DynamicMaps is installed.\n\n"
            "Settings: F12 exposes NaturalEquatorNight for a natural Equator midnight look on the next\n"
            "raid, ArenaInstallPath, the Arena installation folder (empty = automatic), and ShowMapNotices\n"
            "(on by default), the message at the main menu that names why the Arena maps are not available;\n"
            "turning it off hides only that message, never the Arena check. DeveloperMode\n"
            "appears under Advanced and requires a restart. A fresh installation creates only the compact\n"
            "player configuration; existing user settings are preserved.\n\n"
            + verification +
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
        own = generated if carries_generated(plan) else {}
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
    """The plan text. `size_limit=None` is the single-ZIP layout: one archive, no size gate."""
    single = is_single_layout([est.plan for est in estimates])
    if single:
        out = [f"ArenaMapPort release plan  version {spec.version}  layout single (1 ZIP)",
               f"  source commit {commit}",
               f"  dist          {dist}",
               "  size limit    none (single ZIP: Pixeldrain takes multi-GB files)"]
    else:
        out = [f"ArenaMapPort release plan  version {spec.version}  parts {len(estimates)}",
               f"  source commit {commit}",
               f"  dist          {dist}",
               f"  size limit    {size_limit:,} B per part"]
    warnings = []
    total_raw = total_zip = total_files = 0
    for est in estimates:
        plan = est.plan
        out.append("")
        if single:
            out.append(f"single ZIP  {plan.id}  ->  {plan.zip_name}")
        else:
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
        if size_limit is not None and est.estimated_zip >= size_limit:
            warnings.append(f"part {plan.index} ({plan.id}) is estimated at {est.estimated_zip:,} B, "
                            f"not below the {size_limit:,} B limit")
        total_raw += est.raw_bytes
        total_zip += est.estimated_zip
        total_files += len(plan.files)
    out.append("")
    out.append(f"total: {total_files} dist files, raw {_size(total_raw)}, estimated zip {_size(total_zip)}")
    if single:
        out.append(f"estimated single ZIP: {total_zip / 2 ** 30:.2f} GiB")
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
        own = generated if carries_generated(plan) else {}
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
    layout: str = LAYOUT_PARTS


def _is_within(path: Path, root: Path) -> bool:
    resolved = path.resolve()
    base = root.resolve()
    return resolved == base or base in resolved.parents


def sha256_name(spec: ReleaseSpec) -> str:
    return f"{PACKAGE}-{spec.version}.sha256"


def other_parts_of_version(out_dir: Path, spec: ReleaseSpec, keep) -> list:
    """ZIPs (finished or .partial) of this version in out_dir that are not in `keep`, of EITHER layout: the
    leftovers of a release with a different part count (a map added or removed within the same version) and the
    other layout's files (the multi-part parts when a single ZIP is built, the single ZIP when parts are built)."""
    if not out_dir.is_dir():
        return []
    pattern = re.compile(re.escape(PACKAGE) + "-" + re.escape(spec.version)
                         + r"(?:-part[0-9]+of[0-9]+-[a-z0-9]+)?\.zip(?:\.partial)?")
    keep = {p.name for p in keep}
    return sorted(p for p in out_dir.iterdir() if pattern.fullmatch(p.name) and p.name not in keep)


SUPERSEDED_SUFFIX = ".superseded"


class _Stash:
    """The previous release, moved out of the way under '<name>.superseded' while the new set is built and put in
    place: restore() gives every file back (a failed rebuild), discard() deletes the copies (a finished one).

    Renaming instead of deleting is what makes --force a no-op on failure. A previous file that is locked (7-Zip, a
    virus scan, an upload still reading it) fails its rename BEFORE any new file takes its place, so nothing is
    half-replaced; and a file that cannot be deleted at the very end (discard) is a leftover named *.superseded,
    which is not a release ZIP and so can neither be mistaken for part of the new set by verify_set nor block a
    later build. A hard kill between two renames can leave *.superseded files behind; they are safe to delete."""

    def __init__(self):
        self.moved = []  # (original, aside), in the order they were moved

    def aside(self, path: Path) -> None:
        aside = Path(str(path) + SUPERSEDED_SUFFIX)
        try:
            os.replace(path, aside)
        except OSError as exc:
            raise PackagingError(f"cannot move the previous file {path.name} out of the way ({exc}); is it open in "
                                 "another program? Close it and run again: nothing was changed") from exc
        self.moved.append((path, aside))

    def restore(self) -> list:
        """Move everything back, newest first. Returns the *.superseded files that could not be moved back."""
        stuck = []
        for original, aside in reversed(self.moved):
            try:
                os.replace(aside, original)
            except OSError:
                stuck.append(aside)
        self.moved = []
        return stuck

    def discard(self) -> list:
        """Delete the moved copies. Returns the ones that could not be deleted."""
        stuck = []
        for _original, aside in self.moved:
            try:
                aside.unlink()
            except FileNotFoundError:
                pass
            except OSError:
                stuck.append(aside)
        self.moved = []
        return stuck


def build_release(spec: ReleaseSpec, dist: Path, out_dir: Path, repo: Path, *, force: bool = False,
                  size_limit: int = SIZE_LIMIT, probe_bytes: int = PROBE_BYTES, commit: str | None = None,
                  log=print, single: bool = False) -> BuildResult:
    """Write the parts (or, with `single`, the one ZIP holding every dist file) as .partial files, check each,
    then put everything in place. Without `force` any existing release of this version, of either layout, finished
    or .partial, is refused. With `force` NOTHING of the previous release is deleted or replaced while the new set
    is incomplete: the previous files (finished or stale .partial, either layout, any part count) are renamed to
    '<name>.superseded' (see _Stash), the new set takes their place, and only then are the renamed copies deleted.
    Any failure in between, including Ctrl-C and a rename that a locked file refuses, removes the new files and
    gives the previous ones back, so a failed rebuild is a no-op. The one exception is a stale .partial file with
    the same name as a file this build writes: it is renamed out of the way first (the build must create that
    name) and given back on failure. `size_limit` gates every part of the multi-part layout; the single layout has
    no size gate (Pixeldrain takes multi-GB files, GitHub-style channels are not the target)."""
    if _is_within(out_dir, dist):
        raise PackagingError(f"the output directory must not be inside dist: {out_dir}")
    files = scan_dist(dist)
    parts = partition_single(files, spec) if single else partition_dist(files, spec)
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
    stash = _Stash()
    placed = []  # new files already renamed into place
    sizes, digests = [], []
    try:
        for stale in partials + [sha_partial]:  # the build creates these names; stale debris of them steps aside
            if stale.is_file():
                stash.aside(stale)
        for plan, partial in zip(parts, partials):
            log((f"writing single ZIP ({plan.id}): " if single else f"writing part {plan.index} of {plan.count} "
                 f"({plan.id}): ") + f"{len(plan.files)} files, {plan.raw_bytes:,} raw bytes")
            write_part(plan, parts, spec, commit, generated, decisions, partial, probe_bytes)
            size = partial.stat().st_size
            if not single and size >= size_limit:
                raise PartTooLarge(f"{plan.zip_name} is {size:,} B, not below the {size_limit:,} B limit")
            verify_zip(partial, expected_name=plan.zip_name)
            sizes.append(size)
            digests.append(sha256_file(partial))
        sha_partial.write_text("".join(f"{d}  {p.zip_name}\n" for d, p in zip(digests, parts)),
                               encoding="ascii", newline="\n")
        # The new set is complete and verified. Now the previous release makes room: finished files first, so a
        # locked one stops the swap while nothing new is in place yet, then the stale files of other part counts
        # and of the other layout.
        for old in [p for p in finals + [sha_final] if p.is_file()] + [p for p in other_counts if p.is_file()]:
            stash.aside(old)
        for partial, final in zip(partials, finals):
            os.replace(partial, final)
            placed.append(final)
        os.replace(sha_partial, sha_final)
        placed.append(sha_final)
    except BaseException:
        for leftover in partials + [sha_partial] + placed:
            try:
                leftover.unlink(missing_ok=True)
            except OSError:
                pass  # best effort: restore() below overwrites a placed file, and the original error matters more
        had_previous = bool(stash.moved)
        stuck = stash.restore()
        if stuck:
            log("WARNING: could not give the previous release back; these copies hold it, rename them to their "
                "original names: " + ", ".join(p.name for p in stuck))
        elif had_previous:
            log("the new set was not completed; the previous release was left as it was")
        raise
    for stuck_file in stash.discard():
        log(f"WARNING: the new release is complete, but the superseded file {stuck_file.name} could not be deleted "
            "(is it open in another program?). It is not part of the release and can be deleted by hand.")
    return BuildResult(out_dir, sha_final, parts, finals, sizes, digests,
                       LAYOUT_SINGLE if single else LAYOUT_PARTS)


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
                head = b""
                with archive.open(info) as stream:
                    for block in iter(lambda: stream.read(CHUNK), b""):
                        if len(head) < len(AMPB_MAGIC):
                            head = (head + block)[:len(AMPB_MAGIC)]
                        digest.update(block)
                        count += len(block)
                if count != info.file_size:
                    raise VerificationError(f"{name}: {info.filename} size differs from the ZIP directory")
                if BUNDLE_ENTRY_RE.fullmatch(info.filename) and head != AMPB_MAGIC:
                    raise VerificationError(f"{name}: plaintext bundle {info.filename} (not an AMPB container)")
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
    parts: list  # VerifiedPart; exactly one (id 'full') in the single layout
    layout: str = LAYOUT_PARTS


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
    carries its three bundles, its key file and its database entry; nothing of map X's bundles, key file or db/ folder
    is in any other part."""
    ids = [p.id for p in parts]
    if len(set(ids)) != len(ids) or ids[0] != CORE_ID or CORE_ID in ids[1:]:
        raise VerificationError(f"part ids must be unique with '{CORE_ID}' first: {ids}")
    paths = {p.id: {e["path"] for e in p.manifest["entries"]} for p in parts}
    for part in parts[1:]:
        key = f"arena_{part.id}"
        required = [f"{PLUGIN_ROOT}/bundles/{key}_{kind}.bundle" for kind in ("scenes", "navmesh", "preset")]
        required.append(f"{PLUGIN_ROOT}/keys/{key}.json")
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
                             or name.startswith(f"{PLUGIN_ROOT}/bundles/{key}_")
                             or name == f"{PLUGIN_ROOT}/keys/{key}.json")
            if foreign:
                raise VerificationError(f"{part.zip_name} carries files that belong to part '{other.id}': {foreign}")


MAP_DATA_RE = re.compile(re.escape(f"{PLUGIN_ROOT}/bundles/") + r"(arena_[a-z0-9]+)_(?:scenes|navmesh|preset)\.bundle"
                         + "|" + re.escape(f"{SERVER_ROOT}/db/") + r"(arena_[a-z0-9]+)/base\.json"
                         + "|" + re.escape(f"{PLUGIN_ROOT}/keys/") + r"(arena_[a-z0-9]+)\.json")


def check_single(part, expected_keys=()) -> None:
    """The partition invariant of the single layout: the one archive is the core AND every map, so every released
    map (`expected_keys`, e.g. 'arena_equator') carries its three bundles, its key file and its database entry, and so
    does every other map the archive mentions (recognised by any bundle, key file or db/<key>/base.json entry). At least one map must be
    present, and the generated README and notices must be there. The released map list is NOT recorded in the ZIP,
    so it has to come from the caller: deriving it from the archive's own entries would let a ZIP that lost a whole
    map (all three bundles and the database entry) pass, because nothing in it would mention that map any more."""
    entries = {e["path"]: e for e in part.manifest["entries"]}
    found = {m.group(1) or m.group(2) or m.group(3) for m in map(MAP_DATA_RE.fullmatch, entries) if m}
    if not found:
        raise VerificationError(f"{part.zip_name}: single ZIP carries no map (no bundles, key files or db/<key>/base.json)")
    for key in sorted(found | set(expected_keys)):
        required = [f"{PLUGIN_ROOT}/bundles/{key}_{kind}.bundle" for kind in ("scenes", "navmesh", "preset")]
        required.append(f"{PLUGIN_ROOT}/keys/{key}.json")
        required.append(f"{SERVER_ROOT}/db/{key}/base.json")
        missing = [name for name in required if name not in entries]
        if missing:
            raise VerificationError(f"{part.zip_name}: map {key} lacks {missing}")
    for name in (README_NAME, CLIENT_NOTICES_NAME, SERVER_NOTICES_NAME):
        if name not in entries or entries[name]["origin"] != "generated":
            raise VerificationError(f"{part.zip_name}: generated entry missing: {name}")


def verify_set(directory: Path, version: str | None = None, log=print, size_limit: int | None = None,
               maps=None) -> VerifiedSet:
    """Re-hash the ZIP(s) against the .sha256 and check each manifest against its content and the others. The
    layout comes from the .sha256: exactly one line naming <package>-<version>.zip is the single layout (manifest
    part id 'full', index 1 of 1, no size gate, see check_single); anything else is the multi-part layout with
    the per-part size gate and the partition invariant (see check_partition). Leftover ZIPs of the OTHER layout
    for this version, which the .sha256 does not list, are an error in both. `maps` is the list of map keys the
    single ZIP must carry (default: the released maps of default_maps()); the multi-part layout names its maps in
    its part file names and does not use it."""
    limit = SIZE_LIMIT if size_limit is None else size_limit
    expected_maps = [key for key, _display in default_maps()] if maps is None else list(maps)
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
    single_name = single_zip_name(version)
    single = names == [single_name]
    layout = LAYOUT_SINGLE if single else LAYOUT_PARTS
    part_re = re.compile(re.escape(PACKAGE) + "-" + re.escape(version) + r"-part([0-9]+)of([0-9]+)-([a-z0-9]+)\.zip")
    stray = sorted(p.name for p in directory.iterdir()
                   if (part_re.fullmatch(p.name) or p.name == single_name) and p.name not in names)
    if stray:
        raise VerificationError(f"ZIP files present but not listed in {sha_path.name}: {stray}")
    verified = []
    for position, (digest, zip_name) in enumerate(expected, 1):
        if single:
            part_id, count = FULL_ID, 1
        else:
            match = part_re.fullmatch(zip_name)
            if not match or int(match.group(1)) != position or int(match.group(2)) != len(expected):
                raise VerificationError(f"{sha_path.name}: {zip_name} is not part {position} of {len(expected)}")
            part_id, count = match.group(3), len(expected)
        path = directory / zip_name
        if not path.is_file():
            raise VerificationError(f"missing part: {zip_name}")
        actual = sha256_file(path)
        if actual != digest:
            raise VerificationError(f"SHA-256 mismatch: {zip_name} (listed {digest}, actual {actual})")
        size = path.stat().st_size
        if not single and size >= limit:
            raise VerificationError(f"{zip_name} is {size:,} B, not below the {limit:,} B limit")
        manifest = verify_zip(path)
        if (manifest["part"]["id"] != part_id or manifest["part"]["index"] != position
                or manifest["part"]["of"] != count):
            raise VerificationError(f"{zip_name}: manifest part id/index/count disagree with the file name")
        verified.append(VerifiedPart(position, part_id, zip_name, path, digest, size, manifest))
        log(f"verified single ZIP: {zip_name}" if single else f"verified part {position} of {len(expected)}: {zip_name}")
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
    if single:
        check_single(verified[0], expected_maps)
    else:
        check_partition(verified)
    return VerifiedSet(version, sha_path, verified, layout)


def check_scratch(scratch: Path, dist: Path) -> None:
    """Preconditions of the extract-check scratch directory. run() calls this BEFORE building or verifying so a
    bad path does not surface only after minutes of work and a complete, unchecked set left behind."""
    if scratch.exists() and (not scratch.is_dir() or any(scratch.iterdir())):
        raise PackagingError(f"extract-check scratch directory must be empty or absent: {scratch}")
    if _is_within(scratch, dist):
        raise PackagingError(f"the extract-check scratch directory must not be inside dist: {scratch}")


def extract_check(verified: VerifiedSet, dist: Path, scratch: Path, log=print) -> int:
    """Extract all parts (the one ZIP, in the single layout) into an empty scratch directory and compare it byte
    for byte with dist/. The generated entries (README, notices, parts/<id>.json) are ignored in both layouts."""
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
    parser.add_argument("--single", action="store_true",
                        help="build ONE ZIP holding every dist file instead of the multi-part set (the chosen "
                             "release form; no size gate). Combines with --plan; not with --verify-only, which "
                             "detects the layout itself")
    parser.add_argument("--force", action="store_true",
                        help="replace an existing release of this version, of either layout; the old files are "
                             "only renamed once the new set is complete and are restored if anything fails, so a "
                             "failed rebuild changes nothing")
    parser.add_argument("--allow-dirty", action="store_true",
                        help="skip the clean git tree gate (the manifests then record '<commit>+dirty')")
    parser.add_argument("--verify-only", metavar="DIR",
                        help="verify the ZIP(s) in DIR instead of building (single or multi-part, detected "
                             "from the .sha256)")
    parser.add_argument("--extract-check", metavar="SCRATCH",
                        help="after building / verifying, extract the ZIP (or all parts) into this EMPTY "
                             "directory and compare with dist")
    args = parser.parse_args(argv)
    if args.plan and (args.verify_only or args.extract_check or args.force):
        parser.error("--plan writes nothing and cannot be combined with --verify-only, --extract-check or --force")
    if args.verify_only and (args.force or args.out or args.single):
        parser.error("--verify-only reads an existing directory and detects its layout from the .sha256; "
                     "--out, --force and --single do not apply")
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
        shape = "single ZIP" if verified.layout == LAYOUT_SINGLE else f"{len(verified.parts)} parts"
        print(f"RELEASE_ZIPS_VERIFIED {verified.version} ({shape})")
        warn_unidentified_source(verified)
        if args.extract_check:
            extract_check(verified, dist, Path(args.extract_check))
        return 0
    spec = load_spec(repo, args.version, default_maps())
    if args.plan:
        files = scan_dist(dist)
        parts = partition_single(files, spec) if args.single else partition_dist(files, spec)
        commit = source_commit(repo)
        decisions = {f.rel: decide_file(f) for f in files}
        estimates = estimate_parts(spec, parts, decisions, generated_core_entries(spec, parts, repo), commit)
        print(format_plan(spec, dist, commit, estimates, decisions, None if args.single else SIZE_LIMIT))
        return 0
    if not args.allow_dirty:
        require_clean_tree(repo)
    out = Path(args.out) if args.out else repo / "work" / "releases" / spec.version
    result = build_release(spec, dist, out, repo, force=args.force, single=args.single,
                           commit=resolve_commit(repo, args.allow_dirty))
    for plan, path, size, digest in zip(result.parts, result.zips, result.sizes, result.digests):
        print(f"{path.name}  {size:,} B  {digest}")
    if result.layout == LAYOUT_SINGLE:
        print(f"single ZIP: {result.sizes[0] / 2 ** 30:.2f} GiB ({result.sizes[0]:,} B); the 2**31 per-ZIP size "
              "gate does not apply to this layout (Pixeldrain takes multi-GB files)")
    print(f"wrote {result.sha256_path}")
    if args.extract_check:
        extract_check(verify_set(out, spec.version, maps=[m.key for m in spec.maps]), dist,
                      Path(args.extract_check))
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
