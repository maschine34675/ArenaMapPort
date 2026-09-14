// ArenaMapPort WP2 — thin loader over WP1's intermediate package format (SPEC.md §C). Holds the
// parsed manifest.json and resolves file paths for meshes/materials/textures/scenes relative to
// the package root. Deliberately loose (JObject navigation, not strict POCOs) so a schema field
// WP1 documents but this pass doesn't need doesn't break import.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ArenaMapPort.Editor.Common
{
    public class ArenaPackage
    {
        public readonly string Root;
        public readonly JObject Manifest;

        public ArenaPackage(string root)
        {
            Root = root.Replace('\\', '/').TrimEnd('/');
            string manifestPath = Path.Combine(Root, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("ArenaMapPort package manifest not found: " + manifestPath);
            Manifest = JObject.Parse(File.ReadAllText(manifestPath));

            var errors = (JArray)Manifest["errors"];
            if (errors != null && errors.Count > 0)
                throw new IOException("Package manifest.json has non-empty errors[] (" + errors.Count + ") - refusing to import a known-bad extraction: " + Root);
        }

        public string ResolvePath(string relFile)
        {
            return Path.Combine(Root, relFile).Replace('\\', '/');
        }

        public List<string> SceneNames
        {
            get { return ((JArray)Manifest["scenes"]).Select(t => (string)t).ToList(); }
        }

        public JObject Meshes { get { return (JObject)Manifest["meshes"]; } }
        public JObject Materials { get { return (JObject)Manifest["materials"]; } }
        public JObject Textures { get { return (JObject)Manifest["textures"]; } }

        public JObject LoadMaterialJson(string matId)
        {
            var entry = (JObject)Materials[matId];
            string file = ResolvePath((string)entry["file"]);
            return JObject.Parse(File.ReadAllText(file));
        }

        public JObject LoadSceneJson(string sceneName)
        {
            string file = ResolvePath("scenes/" + sceneName + ".json");
            return JObject.Parse(File.ReadAllText(file));
        }

        public string MeshBinPath(string meshId)
        {
            var entry = (JObject)Meshes[meshId];
            return ResolvePath((string)entry["file"]);
        }

        public string TexturePngPath(string texId)
        {
            var entry = (JObject)Textures[texId];
            return ResolvePath((string)entry["file"]);
        }

        public bool TextureIsNormalMap(string texId)
        {
            return (bool)((JObject)Textures[texId])["is_normal_map"];
        }

        public string TextureColorSpace(string texId)
        {
            return (string)((JObject)Textures[texId])["color_space"];
        }

        // impl-texture-dimensions §2: one of "Tex2D" (default -- matches every package that
        // predates this §C-compatible manifest field), "Cube", "Tex3D", "Tex2DArray". Defaulting
        // to "Tex2D" when the key is absent keeps this package reader compatible with an older
        // extractor run that never wrote the field, per §C's own "extend compatibly" instruction.
        public string TextureDimension(string texId)
        {
            var entry = (JObject)Textures[texId];
            var tok = entry["dimension"];
            return tok != null && tok.Type != JTokenType.Null ? (string)tok : "Tex2D";
        }

        public int TextureCubeFaceWidth(string texId)
        {
            return (int)((JObject)Textures[texId])["cube_face_width"];
        }

        public int TextureCubeFaceHeight(string texId)
        {
            return (int)((JObject)Textures[texId])["cube_face_height"];
        }

        public string MaterialShaderName(string matId)
        {
            return (string)((JObject)Materials[matId])["shader"];
        }
    }
}
