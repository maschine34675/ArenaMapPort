using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaMapPort.Editor.NavMesh
{
    /// <summary>Read an existing bundle and export its native triangulation for local cartography.</summary>
    public static class ExportMapTriangulation
    {
        [Serializable] private sealed class Receipt
        {
            public string bundleSha256;
            public Vector3[] vertices;
            public int[] indices;
            public int[] areas;
        }

        public static void Run()
        {
            AssetBundle bundle = null;
            NavMeshDataInstance handle = default;
            try
            {
                var args = Environment.GetCommandLineArgs();
                string Argument(string name)
                {
                    int index = Array.IndexOf(args, name);
                    if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
                    return Path.GetFullPath(args[index + 1]);
                }
                string input = Argument("-mapNavMeshBundle");
                string output = Argument("-mapTriangulationOutput");
                if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output equals input");
                if (UnityEngine.AI.NavMesh.CalculateTriangulation().vertices.Length != 0)
                    throw new InvalidOperationException("Run in a fresh batch editor without an existing NavMesh");
                bundle = AssetBundle.LoadFromFile(input);
                if (bundle == null) throw new InvalidDataException("Cannot load NavMesh bundle");
                var data = bundle.LoadAllAssets<NavMeshData>();
                if (data.Length != 1) throw new InvalidDataException("Expected one NavMeshData");
                handle = UnityEngine.AI.NavMesh.AddNavMeshData(data[0]);
                if (!handle.valid) throw new InvalidOperationException("NavMesh registration failed");
                var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
                if (tri.vertices.Length == 0 || tri.indices.Length == 0) throw new InvalidDataException("Empty triangulation");
                string hash;
                using (var stream = File.OpenRead(input))
                using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, JsonUtility.ToJson(new Receipt
                {
                    bundleSha256 = hash, vertices = tri.vertices, indices = tri.indices, areas = tri.areas
                }));
                Debug.Log("[ArenaMapPort][MapTriangulation] exported " + tri.vertices.Length + " vertices / " + tri.indices.Length / 3 + " triangles -> " + output);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (handle.valid) handle.Remove();
                if (bundle != null) bundle.Unload(true);
            }
        }
    }
}
