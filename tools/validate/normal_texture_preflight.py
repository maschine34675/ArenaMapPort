"""Offline pre-flight of the normal-texture import policy (docs/MAP-RECIPE.md, "Before the first Unity build").

Unity's TextureImportDriver.ImportAll sends every normal-flagged texture of a package through InspectNormalSource and
NormalTextureImportPolicy.Classify and throws on the first one the policy does not know; Yard's first build lost four
minutes to that, one texture at a time. This tool runs the same checks on a package directory without Unity:

  * the set is exactly the one ImportAll routes through InspectNormalSource: manifest textures with is_normal_map true,
    without native_volume and without generated_terrain_array;
  * the PNG checks of InspectNormalSource (RGBA8: bit depth 8, colour type 6; decoded size equals the manifest's);
  * the pixel statistics of NormalTextureImportPolicy.Pixels, computed here with numpy (Unity's loop is far too slow
    to mirror in PowerShell);
  * the usage list exactly as CollectNormalTextureUsages builds it (every material of the manifest, tex_envs slots
    whose texture_id is a normal-flagged texture, keyword DXT1_NM, the material's shader name).

The VERDICT is never computed here: the rows go to normal_texture_preflight.ps1, which compiles the production
NormalTextureImportPolicy.cs with Add-Type and calls its real Usage.Add and Classify. If pwsh or the policy file is
missing this tool fails; there is no Python mirror to fall back to (a mirror is how the policy and its checker drift).

Limits: the pixels are decoded with PIL, which predicts Unity's LoadImage result and does not measure it (no difference
has been seen); and a malformed manifest is read more leniently than Unity reads it (a texture without is_normal_map is
skipped here and throws there, a JSON null native_volume counts as a volume there). The seven accepted packages have
neither; validate_extract.py is the check of the manifest's shape.

Usage: python tools/validate/normal_texture_preflight.py <package-dir> [--json rows.json]
Exit 0 only when nothing is rejected; the last line is NORMAL_TEXTURE_PREFLIGHT_OK <n> textures or
NORMAL_TEXTURE_PREFLIGHT_FAILED <k> of <n>.
"""
import argparse
import json
import shutil
import struct
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
POLICY = REPO / "UnityProject" / "Assets" / "ArenaMapPort" / "Editor" / "Import" / "NormalTextureImportPolicy.cs"
BRIDGE = Path(__file__).with_name("normal_texture_preflight.ps1")


class PreflightError(RuntimeError):
    """The tool itself cannot run (not a texture verdict)."""


def png_header_problem(data):
    """InspectNormalSource's byte check: 33 bytes at least, PNG signature, bit depth 8 (byte 24), colour type 6 (byte 25)."""
    if len(data) < 33 or data[:4] != b"\x89PNG" or data[24] != 8 or data[25] != 6:
        return "Normal source is not an RGBA8 PNG"
    return None


def png_size(data):
    """Width and height from the IHDR chunk (bytes 16..24, big endian)."""
    return struct.unpack(">II", data[16:24])


def pixel_stats(path):
    """The statistics NormalTextureImportPolicy.Pixels accumulates, keyed by its property names."""
    import numpy as np
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = None
    with Image.open(path) as im:
        a = np.asarray(im.convert("RGBA"))
    r, g, b, al = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    return {
        "Count": int(a.shape[0] * a.shape[1]),
        "MinR": int(r.min()), "MaxR": int(r.max()),
        "MinB": int(b.min()), "MinA": int(al.min()),
        "MaxGreenBlueDifference": int(np.abs(g.astype(np.int16) - b.astype(np.int16)).max()),
        "BelowHalfBlue": int((b < 128).sum()),
    }, (int(a.shape[1]), int(a.shape[0]))


def normal_texture_ids(textures):
    return sorted(t for t, m in textures.items()
                  if m.get("is_normal_map") is True and m.get("native_volume") is None
                  and m.get("generated_terrain_array") is not True)


def collect_usages(pkg, manifest):
    """CollectNormalTextureUsages: texture id -> list of slots, in material then tex_envs order."""
    textures = manifest["textures"]
    usages = {}
    for mat_id, entry in manifest["materials"].items():
        mat = json.loads((pkg / entry["file"]).read_text(encoding="utf-8"))
        envs = mat.get("tex_envs")
        if not isinstance(envs, dict):
            continue
        dxt1 = "DXT1_NM" in (mat.get("keywords") or [])
        for prop, slot in envs.items():
            tid = slot.get("texture_id") if isinstance(slot, dict) else None
            if tid is None or tid not in textures or textures[tid].get("is_normal_map") is not True:
                continue
            usages.setdefault(tid, []).append({
                # ArenaPackage.MaterialShaderName reads the MANIFEST entry, not the material file.
                "shader": entry.get("shader"),
                "property": prop, "dxt1": dxt1, "material": entry.get("source_name") or mat_id})
    return usages


def build_rows(pkg):
    manifest = json.loads((pkg / "manifest.json").read_text(encoding="utf-8"))
    textures = manifest["textures"]
    usages = collect_usages(pkg, manifest)
    rows = []
    for tid in normal_texture_ids(textures):
        meta = textures[tid]
        row = {"id": tid, "source_name": meta.get("source_name"), "format_src": meta.get("format_src"),
               "color_space": meta.get("color_space"), "dimension": meta.get("dimension") or "Tex2D",
               "width": meta.get("width") or 0, "height": meta.get("height") or 0,
               "usage": usages.get(tid), "stats": None, "early": None}
        path = pkg / meta["file"]
        try:
            data = path.read_bytes()
        except OSError as e:
            row["early"] = "Normal source unreadable: %s" % e
            rows.append(row)
            continue
        problem = png_header_problem(data)
        if problem is None and png_size(data) != (row["width"], row["height"]):
            problem = "Normal source dimensions/decode mismatch (png %dx%d, manifest %sx%s)" % (
                png_size(data) + (row["width"], row["height"]))
        if problem:
            row["early"] = problem + ": " + tid
        else:
            row["stats"], _ = pixel_stats(path)
        rows.append(row)
    return rows


def require_runtime():
    """What the verdict needs, checked before any PNG is decoded (a machine without pwsh should not wait for that)."""
    pwsh = shutil.which("pwsh")
    if pwsh is None:
        raise PreflightError("pwsh (PowerShell 7) is not on PATH; the production C# policy cannot be loaded, and there is no Python fallback")
    if not POLICY.is_file():
        raise PreflightError("policy file missing: %s" % POLICY)
    if not BRIDGE.is_file():
        raise PreflightError("bridge script missing: %s" % BRIDGE)
    return pwsh


def run_policy(rows):
    pwsh = require_runtime()
    todo = [r for r in rows if r["early"] is None]
    with tempfile.TemporaryDirectory(prefix="ampreflight") as tmp:
        tmp = Path(tmp)
        (tmp / "in.json").write_text(json.dumps({"textures": todo}), encoding="utf-8")
        # The policy file is named explicitly, so the file checked above is the file the bridge compiles.
        proc = subprocess.run([pwsh, "-NoProfile", "-File", str(BRIDGE), "-InputJson", str(tmp / "in.json"),
                               "-OutputJson", str(tmp / "out.json"), "-PolicyPath", str(POLICY)],
                              capture_output=True, text=True, encoding="utf-8")
        if proc.returncode != 0 or not (tmp / "out.json").is_file():
            raise PreflightError("the PowerShell bridge failed (exit %d):\n%s%s" % (proc.returncode, proc.stdout, proc.stderr))
        verdicts = {v["id"]: v for v in json.loads((tmp / "out.json").read_text(encoding="utf-8"))}
    for r in rows:
        if r["early"] is not None:
            r["plan"], r["message"] = None, r["early"]
        else:
            v = verdicts[r["id"]]
            r["plan"], r["compression"], r["srgb"], r["message"] = v["plan"], v["compression"], v["srgb"], v["message"]
    return rows


def format_rejection(r):
    head = "REJECTED %s source_name=%s format=%s colorSpace=%s size=%sx%s" % (
        r["id"], r["source_name"], r["format_src"], r["color_space"], r["width"], r["height"])
    lines = [head, "    message: %s" % r["message"], "    stats: %s" % (json.dumps(r["stats"]) if r["stats"] else "n/a")]
    if not r["usage"]:
        lines.append("    slots: none (no material binds it)")
    for s in r["usage"] or []:
        lines.append("    slot: shader=%s property=%s dxt1_nm=%s material=%s" % (s["shader"], s["property"], s["dxt1"], s["material"]))
    return "\n".join(lines)


def check_package(pkg):
    require_runtime()
    return run_policy(build_rows(Path(pkg)))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("package")
    ap.add_argument("--json", help="write the rows (inputs and verdicts) to this file")
    args = ap.parse_args(argv)
    pkg = Path(args.package)
    if not (pkg / "manifest.json").is_file():
        print("no manifest.json in %s" % pkg, file=sys.stderr)
        return 2
    try:
        rows = check_package(pkg)
    except PreflightError as e:
        print("NORMAL_TEXTURE_PREFLIGHT_ERROR %s" % e, file=sys.stderr)
        return 2
    rejected = [r for r in rows if r["plan"] is None]
    for r in rejected:
        print(format_rejection(r))
    plans = Counter("%s/%s" % (r["plan"], r["compression"]) for r in rows if r["plan"])
    print("plans: %s; rejected: %d; normal-flagged textures: %d" % (
        ", ".join("%s=%d" % kv for kv in sorted(plans.items())) or "none", len(rejected), len(rows)))
    if args.json:
        Path(args.json).write_text(json.dumps(rows, indent=1), encoding="utf-8")
    if rejected:
        print("NORMAL_TEXTURE_PREFLIGHT_FAILED %d of %d" % (len(rejected), len(rows)))
        return 1
    print("NORMAL_TEXTURE_PREFLIGHT_OK %d textures" % len(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main())
