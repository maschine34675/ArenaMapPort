from local_output import output_path
import sys, os, json, time
sys.path.insert(0, os.path.dirname(__file__))
from common import AUTOSERVICE
import census as C

def main():
    env = C.build_env(AUTOSERVICE)
    out = {}
    t0=time.time(); out["object_counts_per_file"], out["object_counts_total"] = C.object_counts(env, AUTOSERVICE); print("object_counts", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["static_batching"] = C.static_batching_stats(env, AUTOSERVICE); print("static_batching", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["mesh_features"] = C.mesh_feature_stats(env, AUTOSERVICE); print("mesh_features", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["textures"] = C.texture_stats(env, AUTOSERVICE, "D:/ArenaMapPort/analysis/tex_samples_autoservice"); print("textures", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["materials"] = C.material_stats(env, AUTOSERVICE); print("materials", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["layers"] = C.layer_distribution(env, AUTOSERVICE); print("layers", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["components"] = C.component_census(env, AUTOSERVICE, None); print("components", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["lightmap_settings"] = C.lightmap_settings(env); print("lightmap_settings", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["spawn_zone_anchors"] = C.spawn_zone_anchors(env, AUTOSERVICE); print("spawn_zone_anchors", time.time()-t0, file=sys.stderr)
    t0=time.time(); out["navmesh"] = C.navmesh_summary(env, AUTOSERVICE); print("navmesh", time.time()-t0, file=sys.stderr)
    with open(output_path("autoservice_census.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2, default=str, ensure_ascii=False)
    print("DONE", file=sys.stderr)

if __name__ == "__main__":
    main()
