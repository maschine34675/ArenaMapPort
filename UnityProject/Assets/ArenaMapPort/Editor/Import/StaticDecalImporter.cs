using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaMapPort.Editor.Import
{
    // Source pixels refer to Arena's authored LastTexSize, which can be twice its shipped texture.
    // EFT GetUVStartEnd divides by the actual texture dimensions, so transfer the same normalized UVs.
    public static class StaticDecalImporter
    {
        public static void Attach(GameObject go, JObject data, Dictionary<string, Material> materials)
        {
            if ((int)data["schema_version"] != 1 ||
                !materials.TryGetValue((string)data["material_id"], out var material) || material == null)
                throw new InvalidDataException("StaticDecalImporter: missing original material/schema on " + go.name);
            var texture = material.GetTexture("_MainTex") as Texture2D;
            if (texture == null)
                throw new InvalidDataException("StaticDecalImporter: missing _MainTex on " + go.name);
            Vector2 authoredSize = Vec(data["texture_size"]);
            if (authoredSize.x <= 0 || authoredSize.y <= 0)
                throw new InvalidDataException("StaticDecalImporter: invalid authoring texture size");
            bool active = go.activeSelf;
            go.SetActive(false); // ExecuteInEditMode Awake must see a valid material, never the null default.
            try
            {
                var decal = go.AddComponent<StaticDeferredDecal>();
                decal._material = material;
                var scale = new Vector2(texture.width / authoredSize.x, texture.height / authoredSize.y);
                decal.LowerLeftPixel = Vector2.Scale(Vec(data["lower_left"]), scale);
                decal.UpperRightPixel = Vector2.Scale(Vec(data["upper_right"]), scale);
                decal.LastTexSize = new Vector2(texture.width, texture.height);
                decal._decalQueue = (int)data["queue"];
                decal._normalClip = (float)data["normal_clip"];
                decal._normalAlphaFadeoff = (float)data["normal_fade"];
                decal._edgeClip = (float)data["edge_clip"];
                decal._edgeAlphaFadeoff = (float)data["edge_fade"];
                var c = data["tint"];
                decal._tint = new Color((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
                decal.enabled = (bool)data["enabled"];
            }
            finally { go.SetActive(active); }
        }

        static Vector2 Vec(JToken v) => new Vector2((float)v[0], (float)v[1]);
    }
}
