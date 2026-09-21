using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaMapPort.Client
{
    /// <summary>Runtime render-mesh partition; derived vertex channels interpolate the untouched source mesh.</summary>
    internal static class IndoorSurfaceMesh
    {
        static readonly Dictionary<Mesh, Mesh> Originals = new Dictionary<Mesh, Mesh>();

        // The cover cache outlives a live reclassification. It must never retain a disposable render
        // copy, including when CoverIncludesGlass/F8 rescan while the repair is active.
        internal static Mesh StableSource(Mesh mesh) =>
            mesh == null ? null : Originals.TryGetValue(mesh, out var original) ? original : mesh;

        internal static void Release(Split split)
        {
            if (split == null) return;
            if (split.Filter != null && split.Filter.sharedMesh == split.Copy)
                split.Filter.sharedMesh = split.Original;
            if (!ReferenceEquals(split.Copy, null))
            {
                Originals.Remove(split.Copy);
                if (split.Copy != null) UnityEngine.Object.Destroy(split.Copy);
            }
        }
        internal sealed class Split
        {
            internal MeshFilter Filter;
            internal Mesh Original, Copy;
            internal Material[] Materials;
            internal bool[] Selected;
            internal int Triangles;
        }

        // True means a building floor was examined, including a wholly outdoor/uncovered one. The
        // caller must not fall back to whole-renderer scaling in that case (also protects low LODs).
        internal static bool TryPrepare(MeshRenderer renderer, Material[] materials,
            Func<IndoorSurfaceGeometry.Point, bool> covered, out Split split)
        {
            split = null;
            var bounds = renderer.bounds;
            if (materials.Length < 2 || bounds.size.y < 2 || bounds.size.y > 12
                || Mathf.Max(bounds.size.x, bounds.size.z) < 4
                || bounds.size.x * bounds.size.z < 64) return false;
            var eligible = new bool[materials.Length];
            bool any = false;
            for (int s = 0; s < eligible.Length; s++)
            {
                eligible[s] = IsFloorShader(materials[s]?.shader?.name);
                any |= eligible[s];
            }
            if (!any) return false;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter?.sharedMesh;
            // A claimed mixed shell must never fall through to whole-renderer scaling when its
            // geometry cannot be inspected, or when it contains too little actual floor.
            if (mesh == null || !mesh.isReadable || mesh.subMeshCount != materials.Length) return true;
            // These are static building meshes. Do not reinterpret animated/skinned vertex data.
            if (mesh.blendShapeCount != 0 || mesh.HasVertexAttribute(VertexAttribute.BlendWeight)
                || mesh.HasVertexAttribute(VertexAttribute.BlendIndices)) return true;
            var indices = new int[materials.Length][];
            for (int s = 0; s < indices.Length; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) return true;
                indices[s] = mesh.GetTriangles(s);
            }
            var local = mesh.vertices;
            var world = new IndoorSurfaceGeometry.Point[local.Length];
            var matrix = renderer.localToWorldMatrix;
            for (int i = 0; i < local.Length; i++)
            {
                var p = matrix.MultiplyPoint3x4(local[i]);
                world[i] = new IndoorSurfaceGeometry.Point(p.x, p.y, p.z);
            }
            var partition = IndoorSurfaceGeometry.Split(world, indices, eligible,
                bounds.min.y, matrix.determinant < 0, covered);
            if (partition == null) return true;
            if (partition.Triangles == 0) return true;
            Mesh copy = null;
            try
            {
                copy = UnityEngine.Object.Instantiate(mesh);
                copy.name = mesh.name + " (ArenaMapPort IndoorSurface mesh)";
                if (partition.AddedVertices.Length > 0) AppendVertexChannels(mesh, copy, partition.AddedVertices);
                var output = new List<Material>(materials);
                var selected = new List<bool>(new bool[materials.Length]);
                int added = 0;
                foreach (var triangles in partition.Covered) if (triangles.Length > 0) added++;
                copy.subMeshCount = mesh.subMeshCount + added;
                for (int s = 0; s < indices.Length; s++)
                {
                    copy.SetTriangles(partition.Retained[s], s, false);
                    if (partition.Covered[s].Length == 0) continue;
                    copy.SetTriangles(partition.Covered[s], output.Count, false);
                    output.Add(materials[s]); selected.Add(true);
                }
                split = new Split { Filter = filter, Original = mesh, Copy = copy,
                    Materials = output.ToArray(), Selected = selected.ToArray(), Triangles = partition.Triangles };
                Originals.Add(copy, mesh);
                return true;
            }
            catch
            {
                if (copy != null) UnityEngine.Object.Destroy(copy);
                throw;
            }
        }

        static void AppendVertexChannels(Mesh source, Mesh copy, IndoorSurfaceGeometry.AddedVertex[] added)
        {
            // Clear the clone's layout before increasing vertex count. Source data, collision mesh,
            // and bounds stay untouched. No normal/tangent recalculation may alter authored shading.
            int originalCount = source.vertexCount;
            copy.Clear(false);
            copy.indexFormat = originalCount + added.Length > ushort.MaxValue
                ? IndexFormat.UInt32 : source.indexFormat;
            copy.vertices = Append(source.vertices, originalCount, added, Blend);
            var normals = source.normals;
            if (normals.Length > 0) copy.normals = Append(normals, originalCount, added, Blend);
            var tangents = source.tangents;
            if (tangents.Length > 0) copy.tangents = Append(tangents, originalCount, added, Blend);
            var colors = source.colors;
            if (colors.Length > 0) copy.colors = Append(colors, originalCount, added, Blend);
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!source.HasVertexAttribute(attribute)) continue;
                int dimension = source.GetVertexAttributeDimension(attribute);
                if (dimension <= 2)
                {
                    // Unity's managed SetUVs has no one-component overload. Its unused second
                    // component is zero; every authored component still interpolates identically.
                    var uv = new List<Vector2>(); source.GetUVs(channel, uv);
                    copy.SetUVs(channel, new List<Vector2>(Append(uv, originalCount, added, Blend)));
                }
                else if (dimension == 3)
                {
                    var uv = new List<Vector3>(); source.GetUVs(channel, uv);
                    copy.SetUVs(channel, new List<Vector3>(Append(uv, originalCount, added, Blend)));
                }
                else
                {
                    var uv = new List<Vector4>(); source.GetUVs(channel, uv);
                    copy.SetUVs(channel, new List<Vector4>(Append(uv, originalCount, added, Blend)));
                }
            }
            copy.bounds = source.bounds;
        }

        static T[] Append<T>(IReadOnlyList<T> source, int originalCount,
            IndoorSurfaceGeometry.AddedVertex[] added, Func<T, T, T, IndoorSurfaceGeometry.AddedVertex, T> blend)
        {
            if (source.Count != originalCount) throw new InvalidOperationException("Incomplete indoor mesh vertex channel");
            var output = new T[originalCount + added.Length];
            for (int i = 0; i < originalCount; i++) output[i] = source[i];
            for (int i = 0; i < added.Length; i++)
            {
                var v = added[i];
                output[originalCount + i] = blend(source[v.A], source[v.IndexB], source[v.IndexC], v);
            }
            return output;
        }

        static Vector2 Blend(Vector2 a, Vector2 b, Vector2 c, IndoorSurfaceGeometry.AddedVertex v) => new Vector2(
            (float)v.Interpolate(a.x, b.x, c.x), (float)v.Interpolate(a.y, b.y, c.y));
        static Vector3 Blend(Vector3 a, Vector3 b, Vector3 c, IndoorSurfaceGeometry.AddedVertex v) => new Vector3(
            (float)v.Interpolate(a.x, b.x, c.x), (float)v.Interpolate(a.y, b.y, c.y), (float)v.Interpolate(a.z, b.z, c.z));
        static Vector4 Blend(Vector4 a, Vector4 b, Vector4 c, IndoorSurfaceGeometry.AddedVertex v) => new Vector4(
            (float)v.Interpolate(a.x, b.x, c.x), (float)v.Interpolate(a.y, b.y, c.y),
            (float)v.Interpolate(a.z, b.z, c.z), (float)v.Interpolate(a.w, b.w, c.w));
        static Color Blend(Color a, Color b, Color c, IndoorSurfaceGeometry.AddedVertex v) => new Color(
            (float)v.Interpolate(a.r, b.r, c.r), (float)v.Interpolate(a.g, b.g, c.g),
            (float)v.Interpolate(a.b, b.b, c.b), (float)v.Interpolate(a.a, b.a, c.a));

        internal static bool IsFloorShader(string name) => name != null
            && name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) < 0
            && name.StartsWith("p0/Reflective/", StringComparison.Ordinal);
    }
}
