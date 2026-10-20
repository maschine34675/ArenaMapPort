// ArenaMapPort WP2 — §B.WP2.2 mesh import. Reads meshes/<id>.mesh.bin (SPEC.md §C.2 binary
// layout) and builds a UnityEngine.Mesh in the exact order the spec requires: SetVertices ->
// subMeshCount (before any per-submesh call) -> SetNormals/SetTangents/SetColors/SetUVs ->
// SetIndices per submesh -> RecalculateBounds as an explicit final step.
using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Editor.Common;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaMapPort.Editor.Import
{
    public static class MeshImporter
    {
        public static Dictionary<string, Mesh> ImportAll(ArenaPackage pkg)
        {
            Directory.CreateDirectory(ArenaPaths.MeshesDir);
            var result = new Dictionary<string, Mesh>();
            // Every package reconstructs every mesh from its own mesh.bin (2026-10-20). A mesh id is
            // sha1(source file : path_id : type)[:16] (tools/extract/common_ids.py), which identifies a
            // source OBJECT, not its geometry: Arena's level files repeat path ids across maps, so the
            // same id names different meshes in different packages (Bay5 and Bowl share 798 ids and 228
            // of them differ, e.g. 1403b6e6f03151dd = Bay5's Military_sandbag_barrier_02_SHADOW_LOD2,
            // 64 vertices, and Bowl's City_skyscrapers_background_LOD0, 4228 vertices). Until this
            // change only packages with manifest source.map_key (the generic maps) refreshed; Bay5's
            // legacy package has none and reused whatever the last import left in the shared store:
            // the 2026-10-20 Bay5 rebuild after Bowl's import carried Bowl geometry in 228 mesh slots
            // and baked its NavMesh in a 780 x 450 x 840 m box (29,586 vertices instead of 22,117).
            // validate_bundle_roundtrip.py's submesh/vertex comparison is the check that catches it.
            bool refreshFromPackage = true;
            int refreshed = 0;
            // Batch asset creation so thousands of meshes need only one refresh.
            if (refreshFromPackage) AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var prop in pkg.Meshes.Properties())
                {
                    string meshId = prop.Name;
                    string assetPath = ArenaPaths.MeshesDir + "/" + meshId + ".asset";
                    Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                    if (existing != null && !refreshFromPackage)
                    {
                        result[meshId] = existing;
                        continue;
                    }
                    string binPath = pkg.MeshBinPath(meshId);
                    Mesh mesh = ImportOne(binPath, meshId);
                    if (existing != null)
                    {
                        EditorUtility.CopySerialized(mesh, existing);
                        UnityEngine.Object.DestroyImmediate(mesh);
                        EditorUtility.SetDirty(existing);
                        mesh = existing;
                        refreshed++;
                    }
                    else AssetDatabase.CreateAsset(mesh, assetPath);
                    result[meshId] = mesh;
                }
            }
            finally { if (refreshFromPackage) AssetDatabase.StopAssetEditing(); }
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaMapPort][MeshImporter] imported " + result.Count + " meshes -> " + ArenaPaths.MeshesDir);
            if (refreshFromPackage)
                Debug.Log("[ArenaMapPort][MeshImporter] read current package geometry for every mesh; refreshed " + refreshed + " existing asset(s) with stable references");
            return result;
        }

        public static Mesh ImportOne(string binPath, string meshId)
        {
            byte[] data = File.ReadAllBytes(binPath);
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                string magic = new string(br.ReadChars(4));
                if (magic != "AMPM")
                    throw new IOException("Bad mesh.bin magic '" + magic + "' in " + binPath);
                uint version = br.ReadUInt32();
                if (version != 1)
                    throw new IOException("Unsupported mesh.bin version " + version + " in " + binPath);
                uint vertexCount = br.ReadUInt32();
                ushort channelMask = br.ReadUInt16();
                byte indexFormat = br.ReadByte();
                byte submeshCount = br.ReadByte();

                int vc = (int)vertexCount;
                bool hasNormal = (channelMask & (1 << 1)) != 0;
                bool hasTangent = (channelMask & (1 << 2)) != 0;
                bool hasColor = (channelMask & (1 << 3)) != 0;
                bool[] hasUv = new bool[8];
                for (int i = 0; i < 8; i++) hasUv[i] = (channelMask & (1 << (4 + i))) != 0;

                Vector3[] positions = ReadVec3Array(br, vc);
                Vector3[] normals = hasNormal ? ReadVec3Array(br, vc) : null;
                Vector4[] tangents = hasTangent ? ReadVec4Array(br, vc) : null;
                Color[] colors = hasColor ? ReadColorArray(br, vc) : null;
                var uvs = new Vector2[8][];
                for (int i = 0; i < 8; i++)
                    if (hasUv[i]) uvs[i] = ReadVec2Array(br, vc);

                var subStarts = new uint[submeshCount];
                var subCounts = new uint[submeshCount];
                for (int i = 0; i < submeshCount; i++)
                {
                    subStarts[i] = br.ReadUInt32();
                    subCounts[i] = br.ReadUInt32();
                    br.ReadByte(); // topology - always 0/Triangles per §C.2, not otherwise used
                }

                uint totalIndices = 0;
                for (int i = 0; i < submeshCount; i++) totalIndices += subCounts[i];

                var indices = new int[totalIndices];
                if (indexFormat == 0)
                {
                    for (int i = 0; i < totalIndices; i++) indices[i] = br.ReadUInt16();
                }
                else
                {
                    for (int i = 0; i < totalIndices; i++) indices[i] = (int)br.ReadUInt32();
                }

                var mesh = new Mesh();
                mesh.name = meshId;
                // Explicit index format (§B.WP2.2 — never rely on Unity's auto-promotion).
                mesh.indexFormat = (vc > 65535 || indexFormat == 1) ? IndexFormat.UInt32 : IndexFormat.UInt16;

                mesh.SetVertices(positions);
                // Must be set before any per-submesh SetIndices call - a fresh Mesh defaults to
                // subMeshCount==1 and SetIndices throws past it (§B.WP2.2, review finding #1).
                mesh.subMeshCount = submeshCount;

                if (normals != null) mesh.SetNormals(normals);
                if (tangents != null) mesh.SetTangents(tangents);
                if (colors != null) mesh.SetColors(colors);
                for (int i = 0; i < 8; i++)
                    if (uvs[i] != null) mesh.SetUVs(i, uvs[i]);

                for (int i = 0; i < submeshCount; i++)
                {
                    int start = (int)subStarts[i];
                    int count = (int)subCounts[i];
                    var subIdx = new int[count];
                    Array.Copy(indices, start, subIdx, 0, count);
                    mesh.SetIndices(subIdx, MeshTopology.Triangles, i, false);
                }

                // Explicit final step, not an implicit reliance on SetIndices' calculateBounds
                // default (§B.WP2.2, review finding #11).
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static Vector3[] ReadVec3Array(BinaryReader br, int count)
        {
            var arr = new Vector3[count];
            for (int i = 0; i < count; i++)
                arr[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            return arr;
        }

        private static Vector4[] ReadVec4Array(BinaryReader br, int count)
        {
            var arr = new Vector4[count];
            for (int i = 0; i < count; i++)
                arr[i] = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            return arr;
        }

        private static Color[] ReadColorArray(BinaryReader br, int count)
        {
            var arr = new Color[count];
            for (int i = 0; i < count; i++)
                arr[i] = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            return arr;
        }

        private static Vector2[] ReadVec2Array(BinaryReader br, int count)
        {
            var arr = new Vector2[count];
            for (int i = 0; i < count; i++)
                arr[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
            return arr;
        }
    }
}
