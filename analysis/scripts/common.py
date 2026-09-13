import os
import UnityPy

BASE = "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data"

EQUATOR = {51:'main',52:'TeamFight_AI',53:'DesignStuff',54:'Scripts',55:'Lighting',56:'Sound',57:'TeamFight_DesignStuff',58:'TeamFight',59:'TeamFight_Culling'}
AUTOSERVICE = {62:'main',63:'AI',65:'DesignStuff',69:'Scripts',243:'Scripts_Winter',112:'Sound',136:'TeamFight_DesignStuff',66:'Light',135:'Culling'}

def scene_files(scene_dict, base=BASE):
    """Return list of existing file paths for level<N> and sharedassets<N>.assets (+ resS/resource are auto-picked-up by UnityPy if adjacent, no need to list explicitly)."""
    paths = []
    for idx in scene_dict:
        lvl = f"{base}/level{idx}"
        sa = f"{base}/sharedassets{idx}.assets"
        if os.path.exists(lvl):
            paths.append(lvl)
        if os.path.exists(sa):
            paths.append(sa)
    return paths

def load_env(scene_dict, base=BASE, extra=None):
    paths = scene_files(scene_dict, base)
    if extra:
        paths.extend(extra)
    return UnityPy.load(*paths)
