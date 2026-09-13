import sys, os, json, time
from collections import Counter, defaultdict
sys.path.insert(0, os.path.dirname(__file__))
import UnityPy
from common import BASE, EQUATOR, AUTOSERVICE, scene_files

def build_env(scene_dict):
    paths = scene_files(scene_dict)
    paths.append(f"{BASE}/globalgamemanagers.assets")
    paths.append(f"{BASE}/resources.assets")
    t0 = time.time()
    env = UnityPy.load(*paths)
    print(f"  loaded {len(paths)} files in {time.time()-t0:.2f}s", file=sys.stderr)
    return env

def scene_file_names(scene_dict):
    names = set()
    for idx in scene_dict:
        names.add(f"level{idx}")
        names.add(f"sharedassets{idx}.assets")
    return names

def scene_exact_paths(scene_dict):
    """Exact, full-path keys for our own explicitly-loaded scene files, as produced by
    common.scene_files(). Deliberately NOT matched by basename: UnityPy's texture/PPtr
    streaming resolution can lazily register a SECOND, bare-basename-keyed copy of an
    already-loaded .assets file into env.files as a side effect of decoding a Texture2D
    or dereferencing a cross-file PPtr (observed for sharedassets62.assets while decoding
    texture samples). Matching by basename would silently double-count that duplicate;
    matching the exact original full path does not, regardless of what got lazily loaded
    later for external dependency resolution."""
    paths = set()
    for idx in scene_dict:
        paths.add(f"{BASE}/level{idx}")
        paths.add(f"{BASE}/sharedassets{idx}.assets")
    return paths

def is_scene_file(fname, scene_dict):
    return fname in scene_exact_paths(scene_dict)

# ---------- MonoScript pool ----------
def build_monoscript_pool(env):
    pool = {}
    for f in env.files.values():
        if hasattr(f, "objects"):
            for obj in list(f.objects.values()):
                if obj.type.name == "MonoScript":
                    try:
                        t = obj.read_typetree()
                        cls = t.get("m_ClassName")
                        ns = t.get("m_Namespace")
                        full = f"{ns}.{cls}" if ns else cls
                        pool[obj.path_id] = full
                    except Exception:
                        pass
    return pool

def resolve_script_name(mb_obj, monoscript_pool=None):
    """Resolve a MonoBehaviour's script class via the safe parse_monobehaviour_head()
    (which uses check_read=False and therefore tolerates unknown custom field layouts),
    then dereferences the m_Script PPtr directly (works across files within the same
    UnityPy Environment, regardless of path_id collisions between files)."""
    try:
        mb = mb_obj.parse_monobehaviour_head()
    except Exception as e:
        return None, f"head_parse_failed({e})"
    sptr = mb.m_Script
    if sptr is None or sptr.path_id == 0:
        return None, "null_script"
    try:
        st = sptr.read_typetree()
    except Exception as e:
        return None, f"script_deref_failed({e})"
    if st is None:
        return None, "external_not_loaded"
    cls = st.get("m_ClassName")
    ns = st.get("m_Namespace")
    return (f"{ns}.{cls}" if ns else cls), "ok"

def object_counts(env, scene_dict):
    """Per scene-file object type counts, restricted to our scene files only."""
    names = scene_file_names(scene_dict)
    per_file = {}
    total = Counter()
    for fname, f in list(env.files.items()):
        if is_scene_file(fname, scene_dict) and hasattr(f, "objects"):
            c = Counter()
            for obj in list(f.objects.values()):
                c[obj.type.name] += 1
            per_file[fname] = dict(c.most_common())
            total.update(c)
    return per_file, dict(total.most_common())

def static_batching_stats(env, scene_dict):
    names = scene_file_names(scene_dict)
    total_renderers = 0
    batched_renderers = 0
    combined_mesh_names = Counter()
    batched_mesh_ref_names = Counter()
    examples = []
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "MeshRenderer":
                continue
            total_renderers += 1
            try:
                tree = obj.read_typetree()
            except Exception:
                continue
            sbi = tree.get("m_StaticBatchInfo", {})
            sub_count = sbi.get("subMeshCount", 0)
            if sub_count and sub_count > 0:
                batched_renderers += 1
                if len(examples) < 5:
                    examples.append({"firstSubMesh": sbi.get("firstSubMesh"), "subMeshCount": sub_count, "gameobject_pathid": tree.get("m_GameObject",{}).get("m_PathID")})
    # Mesh name census for "Combined Mesh"
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "Mesh":
                continue
            try:
                nm = obj.read_typetree().get("m_Name", "")
            except Exception:
                continue
            if "Combined Mesh" in nm or "combined" in nm.lower():
                combined_mesh_names[nm] += 1
    return {
        "total_MeshRenderer": total_renderers,
        "batched_MeshRenderer": batched_renderers,
        "combined_mesh_names": dict(combined_mesh_names.most_common(20)),
        "examples": examples,
    }

def mesh_feature_stats(env, scene_dict):
    names = scene_file_names(scene_dict)
    vertex_counts = []
    tri_counts = []
    channel_signatures = Counter()
    index_formats = Counter()
    submesh_counts = Counter()
    skinned_count = 0
    blendshape_meshes = 0
    total_meshes = 0
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "Mesh":
                continue
            total_meshes += 1
            try:
                t = obj.read_typetree()
            except Exception:
                continue
            vd = t.get("m_VertexData", {})
            vc = vd.get("m_VertexCount", 0)
            vertex_counts.append(vc)
            chans = vd.get("m_Channels", [])
            sig = tuple(1 if c.get("dimension",0) > 0 else 0 for c in chans)
            channel_signatures[sig] += 1
            index_formats[t.get("m_IndexFormat")] += 1
            subs = t.get("m_SubMeshes", [])
            submesh_counts[len(subs)] += 1
            tris = sum(s.get("indexCount",0) for s in subs) // 3
            tri_counts.append(tris)
            if t.get("m_BindPose") and len(t.get("m_BindPose")) > 0:
                skinned_count += 1
            shapes = t.get("m_Shapes", {})
            if shapes.get("shapes"):
                blendshape_meshes += 1
    return {
        "total_meshes": total_meshes,
        "total_vertices": sum(vertex_counts),
        "total_triangles": sum(tri_counts),
        "avg_vertices": (sum(vertex_counts)/len(vertex_counts)) if vertex_counts else 0,
        "channel_signature_top": [(list(k), v) for k, v in channel_signatures.most_common(15)],
        "index_format_dist": dict(index_formats),
        "submesh_count_dist": dict(sorted(submesh_counts.items())),
        "skinned_mesh_count": skinned_count,
        "blendshape_mesh_count": blendshape_meshes,
    }

def texture_stats(env, scene_dict, decode_out_dir):
    names = scene_file_names(scene_dict)
    fmt_dist = Counter()
    dim_dist = Counter()
    total = 0
    samples_by_fmt = {}
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "Texture2D":
                continue
            total += 1
            try:
                t = obj.read_typetree()
            except Exception:
                continue
            fmt = t.get("m_TextureFormat")
            fmt_dist[fmt] += 1
            dim_dist[(t.get("m_Width"), t.get("m_Height"))] += 1
            if fmt not in samples_by_fmt:
                samples_by_fmt[fmt] = obj
    os.makedirs(decode_out_dir, exist_ok=True)
    decode_results = []
    for fmt, obj in list(samples_by_fmt.items())[:5]:
        try:
            data = obj.read()
            img = data.image
            outp = os.path.join(decode_out_dir, f"tex_fmt{fmt}_{data.m_Name}.png")
            img.save(outp)
            decode_results.append({"format": fmt, "name": data.m_Name, "size": img.size, "ok": True, "path": outp})
        except Exception as e:
            decode_results.append({"format": fmt, "ok": False, "error": str(e)})
    return {
        "total_textures": total,
        "format_dist": {str(k): v for k, v in fmt_dist.most_common()},
        "dimension_dist_top": [(f"{w}x{h}", n) for (w,h), n in dim_dist.most_common(15)],
        "decode_tests": decode_results,
    }

def material_stats(env, scene_dict):
    names = scene_file_names(scene_dict)
    shader_dist = Counter()
    shader_props_sample = {}
    keyword_counter = Counter()
    total = 0
    # Pass 1: collect all Material ObjectReaders into a plain list BEFORE any
    # PPtr.deref()/obj.read() call, since those can lazily register additional
    # (duplicate-keyed) files into env.files and corrupt a live iteration over it.
    material_objs = []
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name == "Material":
                material_objs.append(obj)
    # Pass 2: process the static list (safe against later env.files/objects mutation).
    if True:
        for obj in material_objs:
            total += 1
            try:
                t = obj.read_typetree()
            except Exception:
                continue
            shader_ptr = t.get("m_Shader", {})
            shader_name = None
            try:
                raw = obj.read()
                shader_obj = raw.m_Shader
                if shader_obj is not None:
                    sh_tree = shader_obj.read_typetree()
                    pf = sh_tree.get("m_ParsedForm", {})
                    shader_name = pf.get("m_Name") or sh_tree.get("m_Name") or None
            except Exception:
                shader_name = None
            if not shader_name:
                shader_name = f"UNRESOLVED({shader_ptr})"
            shader_dist[shader_name] += 1
            if shader_name not in shader_props_sample:
                sp = t.get("m_SavedProperties", {})
                shader_props_sample[shader_name] = {
                    "texenv_names": [x[0] for x in sp.get("m_TexEnvs", [])][:15],
                    "float_names": [x[0] for x in sp.get("m_Floats", [])][:15],
                    "color_names": [x[0] for x in sp.get("m_Colors", [])][:15],
                }
            for kw in (t.get("m_ValidKeywords") or []):
                keyword_counter[kw] += 1
            for kw in (t.get("m_ShaderKeywords") or "").split():
                keyword_counter[kw] += 1
    return {
        "total_materials": total,
        "shader_dist": {str(k): v for k, v in shader_dist.most_common(40)},
        "shader_props_sample": shader_props_sample,
        "keyword_dist": dict(keyword_counter.most_common(30)),
    }

def layer_distribution(env, scene_dict):
    names = scene_file_names(scene_dict)
    dist = Counter()
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "GameObject":
                continue
            try:
                t = obj.read_typetree()
            except Exception:
                continue
            dist[t.get("m_Layer", -1)] += 1
    return dict(sorted(dist.items()))

def component_census(env, scene_dict, monoscript_pool):
    names = scene_file_names(scene_dict)
    builtin_types = ["Light","LODGroup","BoxCollider","SphereCollider","CapsuleCollider","MeshCollider",
                      "Rigidbody","ReflectionProbe","LightProbeGroup","ParticleSystem","AudioSource",
                      "Terrain","TerrainCollider","OcclusionPortal","OcclusionArea","WindZone","NavMeshAgent","NavMeshObstacle"]
    builtin_counts = Counter()
    light_types = Counter()
    light_modes = Counter()
    collider_convex = Counter()
    mono_class_census = Counter()
    null_script_count = 0
    unresolved_count = 0
    lightmap_index_dist = Counter()
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            tn = obj.type.name
            if tn in builtin_types:
                builtin_counts[tn] += 1
                if tn == "Light":
                    try:
                        t = obj.read_typetree()
                        light_types[t.get("m_Type")] += 1
                        light_modes[t.get("m_Lightmapping")] += 1
                    except Exception:
                        pass
                elif tn == "MeshCollider":
                    try:
                        t = obj.read_typetree()
                        collider_convex[bool(t.get("m_Convex"))] += 1
                    except Exception:
                        pass
            elif tn == "MonoBehaviour":
                name, status = resolve_script_name(obj, monoscript_pool)
                if status == "null_script":
                    null_script_count += 1
                elif name:
                    mono_class_census[name] += 1
                else:
                    unresolved_count += 1
            elif tn == "MeshRenderer" or tn == "SkinnedMeshRenderer":
                try:
                    t = obj.read_typetree()
                    lmi = t.get("m_LightmapIndex")
                    if lmi is not None:
                        lightmap_index_dist[lmi] += 1
                except Exception:
                    pass
    return {
        "builtin_component_counts": dict(builtin_counts.most_common()),
        "light_type_dist": dict(light_types),
        "light_lightmapping_mode_dist": dict(light_modes),
        "meshcollider_convex_dist": dict(collider_convex),
        "monobehaviour_class_census": dict(mono_class_census.most_common(400)),
        "monobehaviour_null_script_count": null_script_count,
        "monobehaviour_unresolved_count": unresolved_count,
        "renderer_lightmap_index_dist_top": dict(lightmap_index_dist.most_common(20)),
    }

def lightmap_settings(env):
    for f in env.files.values():
        if not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name == "LightmapSettings":
                try:
                    t = obj.read_typetree()
                except Exception:
                    return None
                lms = t.get("m_Lightmaps", [])
                return {"lightmap_count": len(lms), "raw_sample": lms[:2]}
    return None

# ---------- Spawn / zone anchors with world transform ----------
def _quat_mul(a, b):
    ax,ay,az,aw = a; bx,by,bz,bw = b
    return (
        aw*bx + ax*bw + ay*bz - az*by,
        aw*by - ax*bz + ay*bw + az*bx,
        aw*bz + ax*by - ay*bx + az*bw,
        aw*bw - ax*bx - ay*by - az*bz,
    )

def _quat_rotate(q, v):
    qx,qy,qz,qw = q
    vx,vy,vz = v
    # t = 2 * cross(q.xyz, v)
    tx = 2*(qy*vz - qz*vy)
    ty = 2*(qz*vx - qx*vz)
    tz = 2*(qx*vy - qy*vx)
    # v' = v + qw*t + cross(q.xyz, t)
    rx = vx + qw*tx + (qy*tz - qz*ty)
    ry = vy + qw*ty + (qz*tx - qx*tz)
    rz = vz + qw*tz + (qx*ty - qy*tx)
    return (rx, ry, rz)

def world_transform_of_transform(tobj, max_depth=64):
    """Walk m_Father chain to compute an approximate world position/rotation.
    Scale composition ignores parent rotation skew (adequate for spawn-point census)."""
    pos = (0.0,0.0,0.0)
    rot = (0.0,0.0,0.0,1.0)
    scale = (1.0,1.0,1.0)
    cur = tobj
    depth = 0
    chain = []
    while cur is not None and depth < max_depth:
        t = cur.read_typetree()
        lp = t["m_LocalPosition"]; lr = t["m_LocalRotation"]; ls = t["m_LocalScale"]
        chain.append((lp, lr, ls))
        father = t.get("m_Father", {})
        if not father or father.get("m_PathID", 0) == 0:
            cur = None
        else:
            try:
                if father.get("m_FileID", 0) == 0:
                    cur = cur.assets_file.objects[father["m_PathID"]]
                else:
                    ext = cur.assets_file.externals[father["m_FileID"] - 1]
                    cur = None  # cross-file parent chains not resolved (rare for gameplay markers)
            except Exception:
                cur = None
        depth += 1
    # compose from root down to leaf
    for lp, lr, ls in reversed(chain):
        lp_t = (lp["x"], lp["y"], lp["z"])
        lr_t = (lr["x"], lr["y"], lr["z"], lr["w"])
        ls_t = (ls["x"], ls["y"], ls["z"])
        scaled = (lp_t[0]*scale[0], lp_t[1]*scale[1], lp_t[2]*scale[2])
        rotated = _quat_rotate(rot, scaled)
        pos = (pos[0]+rotated[0], pos[1]+rotated[1], pos[2]+rotated[2])
        rot = _quat_mul(rot, lr_t)
        scale = (scale[0]*ls_t[0], scale[1]*ls_t[1], scale[2]*ls_t[2])
    return pos, rot

SPAWN_ZONE_CLASS_KEYWORDS = ["SpawnPoint", "SpawnPointMarker", "BotZone", "AICorePoint", "PatrolPoint",
                              "AIMinePoint", "AdditionalNavmeshBlock", "SpawnMarker", "ZoneMarker", "BossLocationSpawn"]

def spawn_zone_anchors(env, scene_dict, max_per_class=500):
    names = scene_file_names(scene_dict)
    found = defaultdict(list)
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "MonoBehaviour":
                continue
            cls_name, status = resolve_script_name(obj)
            if status != "ok" or not cls_name:
                continue
            short = cls_name.rsplit(".", 1)[-1]
            if not any(kw.lower() in short.lower() for kw in SPAWN_ZONE_CLASS_KEYWORDS):
                continue
            if len(found[cls_name]) >= max_per_class:
                continue
            try:
                mb = obj.parse_monobehaviour_head()
                go = mb.m_GameObject.deref()
                go_data = go.read()
                transform_ptr = go_data.m_Components[0]
                tobj = transform_ptr.deref()
                pos, rot = world_transform_of_transform(tobj)
            except Exception as e:
                pos, rot = None, None
            found[cls_name].append({"path_id": obj.path_id, "file": os.path.basename(fname), "world_pos": pos})
    summary = {cls: {"count": len(items), "sample_positions": [it["world_pos"] for it in items[:8]],
                      "sample_files": list({it["file"] for it in items})[:5]}
               for cls, items in found.items()}
    return summary

# ---------- NavMeshData ----------
def navmesh_summary(env, scene_dict):
    names = scene_file_names(scene_dict)
    out = []
    for fname, f in list(env.files.items()):
        if not is_scene_file(fname, scene_dict) or not hasattr(f, "objects"):
            continue
        for obj in list(f.objects.values()):
            if obj.type.name != "NavMeshData":
                continue
            try:
                t = obj.read_typetree()
            except Exception as e:
                out.append({"file": os.path.basename(fname), "error": str(e)})
                continue
            hm = t.get("m_HeightMeshes", [])
            offmesh = t.get("m_OffMeshLinks", [])
            tiles = t.get("m_NavMeshTiles", [])
            out.append({
                "file": os.path.basename(fname),
                "name": t.get("m_Name"),
                "tile_count": len(tiles) if isinstance(tiles, list) else None,
                "build_settings": t.get("m_NavMeshBuildSettings"),
                "source_bounds": t.get("m_SourceBounds"),
                "position": t.get("m_Position"),
                "rotation": t.get("m_Rotation"),
                "agent_type_id": t.get("m_AgentTypeID"),
                "heightmeshes_count": len(hm) if isinstance(hm, list) else None,
                "offmesh_links_count": len(offmesh) if isinstance(offmesh, list) else None,
            })
    return out
