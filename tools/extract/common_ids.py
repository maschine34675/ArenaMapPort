"""Small, dependency-free helpers shared by the ArenaMapPort extractor.

Content-stable id derivation (per docs/SPEC.md §C intro), and the handful of
Unity enum -> package-schema-string mappings the extractor needs (§C.6).
Kept separate from extract_equator.py so it can be unit-imported without
pulling in UnityPy.
"""
from __future__ import annotations

import hashlib


def content_id(source_file_basename: str, path_id: int, object_type: str) -> str:
    """sha1(f"{source_file_basename}:{path_id}:{object_type}")[:16] -- stable
    across re-runs (content/identity-derived, not run-order-derived), per
    docs/SPEC.md §C."""
    key = f"{source_file_basename}:{path_id}:{object_type}"
    return hashlib.sha1(key.encode("utf-8")).hexdigest()[:16]


# ---- Unity enum -> §C.6 schema string maps -------------------------------

# Renderer.m_CastShadows (UnityEngine.Rendering.ShadowCastingMode)
CAST_SHADOWS = {0: "Off", 1: "On", 2: "TwoSided", 3: "ShadowsOnly"}

# Renderer.m_LightProbeUsage (UnityEngine.Rendering.LightProbeUsage)
LIGHT_PROBE_USAGE = {0: "Off", 1: "BlendProbes", 2: "UseProxyVolume"}

# Renderer.m_ReflectionProbeUsage -- schema only has two buckets (§C.6)
def reflection_probe_usage(v) -> str:
    return "Off" if v == 0 else "BlendProbes"


# Light.m_Type (UnityEngine.LightType) -- confirmed via direct probe + Method
# note 6 of analysis/arena-census.md: 0=Spot, 1=Directional, 2=Point.
LIGHT_TYPE = {0: "Spot", 1: "Directional", 2: "Point"}

# LightShadows (Light.m_Shadows.m_Type)
SHADOW_TYPE = {0: "None", 1: "Hard", 2: "Soft"}

# PhysicMaterialCombine (PhysicMaterial.m_FrictionCombine / m_BounceCombine)
PHYSIC_COMBINE = {0: "Average", 1: "Minimum", 2: "Maximum", 3: "Multiply"}

# Texture2D.m_ColorSpace -- confirmed empirically (probe_fields.py this pass):
# a known normal-map texture ("..._N") reads 0, a known diffuse/albedo texture
# reads 1. 0 = Linear, 1 = sRGB/Gamma.
COLOR_SPACE = {0: "Linear", 1: "sRGB"}

# Texture2D.m_TextureFormat (UnityEngine.TextureFormat) -- codes confirmed by
# analysis/arena-census.md Method note 5.
TEXTURE_FORMAT_NAMES = {
    1: "Alpha8",
    3: "RGB24",
    10: "DXT1",
    12: "DXT5",
    25: "BC7",
    28: "DXT1Crunched",
}


def texture_format_name(code) -> str:
    return TEXTURE_FORMAT_NAMES.get(code, f"Format{code}")


# GameObject.m_Tag -- confirmed empirically this pass (probe_fields.py /
# explore_roots.py): 0 == "Untagged" (engine-reserved), custom project tags
# (TagManager.tags[], analysis/arena_gm.json) start at a fixed base of 20000,
# i.e. m_Tag == 20000 + index. NOT string-based for this Unity 6000.3 build
# (contradicts an unverified inference in arena-census.md §7 -- ground-truth
# probe wins, see docs/progress/wp1.md).
TAG_USER_BASE = 20000


def resolve_tag(m_tag: int, tags_array: list[str]) -> tuple[str, bool]:
    """Returns (tag_name, ok). ok is False for an out-of-range/unknown index
    (logged by the caller as a manifest warning, never fatal -- no in-scope
    geometry is known to use an unresolvable tag, arena-census.md §7)."""
    if m_tag == 0:
        return "Untagged", True
    idx = m_tag - TAG_USER_BASE
    if 0 <= idx < len(tags_array):
        return tags_array[idx], True
    return f"Tag_{m_tag}", False


class HardExtractError(Exception):
    """A §B.WP1 'hard extractor failure' -- m_MeshCompression != 0 or an
    UNRESOLVED shader. Caller aborts the whole run (non-zero exit), per spec:
    'halt and report the id', not a silently wrong/empty asset."""
