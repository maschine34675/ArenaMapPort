"""Hand-built typetree node lists for Unity.AI.Navigation.NavMeshModifier /
NavMeshModifierVolume -- the two MonoBehaviour types (com.unity.ai.navigation@1.1.5)
that carry NavMesh area-override/ignore-from-build/volume data in Arena's own source.

Copied, with attribution, from the 2026-09-25 navmesh-modifier feasibility spike
(<local-scratchpad>/navmesh-spike/navmesh_typetree.py), which verified this exact
field layout byte-exact (check_read=True) against all 369 NavMeshModifier + 192
NavMeshModifierVolume instances across Equator's and Bay5's real source data, and
falsified the shorter pre-2022.2 layout (a deliberately-wrong node list raised
"Expected to read 64 bytes, but only read 48 bytes" against the same objects) --
Arena's build (Unity 6000.3.6f1) uses the UNITY_2022_2_OR_NEWER field set. The
spike's build_navblock_nodes() (EFT's own AdditionalNavmeshBlock) and the
pre-2022.2 A/B-falsification layout are not needed by this extractor and are not
reproduced here; see that spike file for them if a future pass needs them.

Field order = C# declaration order (Unity's reflection-based typetree generator
serializes [SerializeField] members in source order); see the package sources this
mirrors:
  UnityProject/Library/PackageCache/com.unity.ai.navigation@1.1.5/Runtime/NavMeshModifier.cs
  UnityProject/Library/PackageCache/com.unity.ai.navigation@1.1.5/Runtime/NavMeshModifierVolume.cs
"""
from __future__ import annotations

from UnityPy.enums import ClassIDType
from UnityPy.files.ObjectReader import get_typetree_node

ALIGN = 0x4000  # kAlignBytes meta flag


def _flatten_node(node):
    out = [{
        "m_Level": node.m_Level,
        "m_Type": node.m_Type,
        "m_Name": node.m_Name,
        "m_ByteSize": node.m_ByteSize,
        "m_MetaFlag": node.m_MetaFlag,
    }]
    for child in node.m_Children:
        out.extend(_flatten_node(child))
    return out


def _bool(name, level):
    return {"m_Level": level, "m_Type": "bool", "m_Name": name, "m_ByteSize": 1, "m_MetaFlag": ALIGN}


def _int(name, level):
    return {"m_Level": level, "m_Type": "int", "m_Name": name, "m_ByteSize": 4, "m_MetaFlag": ALIGN}


def _vector3(name, level):
    return [
        {"m_Level": level, "m_Type": "Vector3f", "m_Name": name, "m_ByteSize": 12, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "float", "m_Name": "x", "m_ByteSize": 4, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "float", "m_Name": "y", "m_ByteSize": 4, "m_MetaFlag": 0},
        {"m_Level": level + 1, "m_Type": "float", "m_Name": "z", "m_ByteSize": 4, "m_MetaFlag": 0},
    ]


def _int_list(name, level):
    return [
        {"m_Level": level, "m_Type": "vector", "m_Name": name, "m_ByteSize": -1, "m_MetaFlag": ALIGN},
        {"m_Level": level + 1, "m_Type": "Array", "m_Name": "Array", "m_ByteSize": -1, "m_MetaFlag": ALIGN},
        {"m_Level": level + 2, "m_Type": "int", "m_Name": "size", "m_ByteSize": 4, "m_MetaFlag": 0},
        {"m_Level": level + 2, "m_Type": "int", "m_Name": "data", "m_ByteSize": 4, "m_MetaFlag": 0},
    ]


def build_modifier_nodes(version):
    """Unity.AI.Navigation.NavMeshModifier: m_OverrideArea, m_Area,
    m_OverrideGenerateLinks, m_GenerateLinks, m_IgnoreFromBuild, m_ApplyToChildren,
    m_AffectedAgents (List<int>, default [-1] == "All agent types")."""
    nodes = _flatten_node(get_typetree_node(ClassIDType.MonoBehaviour, version))
    nodes.append(_bool("m_OverrideArea", 1))
    nodes.append(_int("m_Area", 1))
    nodes.append(_bool("m_OverrideGenerateLinks", 1))
    nodes.append(_bool("m_GenerateLinks", 1))
    nodes.append(_bool("m_IgnoreFromBuild", 1))
    nodes.append(_bool("m_ApplyToChildren", 1))
    nodes.extend(_int_list("m_AffectedAgents", 1))
    return nodes


def build_volume_nodes(version):
    """Unity.AI.Navigation.NavMeshModifierVolume: m_Size, m_Center, m_Area,
    m_AffectedAgents."""
    nodes = _flatten_node(get_typetree_node(ClassIDType.MonoBehaviour, version))
    nodes.extend(_vector3("m_Size", 1))
    nodes.extend(_vector3("m_Center", 1))
    nodes.append(_int("m_Area", 1))
    nodes.extend(_int_list("m_AffectedAgents", 1))
    return nodes
