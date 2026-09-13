import sys, os, json, time
sys.path.insert(0, os.path.dirname(__file__))
from common import EQUATOR
import census as C

def main():
    env = C.build_env(EQUATOR)
    pool = C.build_monoscript_pool(env)
    out = {}
    t0=time.time(); out["object_counts_per_file"], out["object_counts_total"] = C.object_counts(env, EQUATOR); print("object_counts", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["static_batching"] = C.static_batching_stats(env, EQUATOR); print("static_batching", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["mesh_features"] = C.mesh_feature_stats(env, EQUATOR); print("mesh_features", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["textures"] = C.texture_stats(env, EQUATOR, "D:/ArenaMapPort/analysis/tex_samples_equator"); print("textures", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["materials"] = C.material_stats(env, EQUATOR); print("materials", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["layers"] = C.layer_distribution(env, EQUATOR); print("layers", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["components"] = C.component_census(env, EQUATOR, pool); print("components", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["lightmap_settings"] = C.lightmap_settings(env); print("lightmap_settings", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["spawn_zone_anchors"] = C.spawn_zone_anchors(env, EQUATOR); print("spawn_zone_anchors", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["navmesh"] = C.navmesh_summary(env, EQUATOR); print("navmesh", time.time()-t0, file=sys.stderr)
    with open("D:/ArenaMapPort/analysis/equator_census.json", "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2, default=str, ensure_ascii=False)
    print("DONE", file=sys.stderr)

if __name__ == "__main__":
    main()
