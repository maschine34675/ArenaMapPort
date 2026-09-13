// ArenaMapPort WP2 — shared path constants + command-line parameter parsing (O7: "Tools must
// accept these as parameters" — every pipeline entry point reads package/output dirs from
// -executeMethod command-line args, falling back to O7's default S1 paths.)
using System;

namespace ArenaMapPort.Editor.Common
{
    public static class ArenaPaths
    {
        // O7 default paths.
        public const string DefaultPackageDirS1 = "D:/ArenaMapPort/work/extract/equator_s1";
        public const string DefaultOutputDirS1 = "D:/ArenaMapPort/work/build/s1";
        public const string DefaultPackageDirFull = "D:/ArenaMapPort/work/extract/equator";
        public const string DefaultOutputDirFull = "D:/ArenaMapPort/work/build/full";

        public const string ImportRoot = "Assets/ArenaImport";
        public const string MeshesDir = ImportRoot + "/Meshes";
        public const string TexturesDir = ImportRoot + "/Textures";
        public const string MaterialsDir = ImportRoot + "/Materials";
        public const string PhysicMaterialsDir = ImportRoot + "/PhysicMaterials";
        public const string ScenesDir = ImportRoot + "/Scenes";
        public const string NavMeshAssetDir = ImportRoot + "/NavMesh";

        public const string StandinShadersDir = "Assets/ArenaMapPort/StandinShaders";
        // O1: stand-in shaders are named "Hidden/ArenaMapPort/" + <original BSG shader name> —
        // never the bare original name, so the SDK Editor's own Shader.Find(original) can never
        // ambiguously resolve to our stand-in (that's the entire point of the prefix).
        public const string ShaderNamePrefix = "Hidden/ArenaMapPort/";

        public const string ScriptsSceneName = "Arena_Equator_Scripts";

        /// <summary>Reads a "-flagName value" pair out of Environment.GetCommandLineArgs(), as
        /// passed after -executeMethod on the Unity.exe command line. Falls back to
        /// defaultValue if the flag isn't present.</summary>
        public static string GetArg(string flagName, string defaultValue)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flagName, StringComparison.Ordinal))
                    return args[i + 1];
            }
            return defaultValue;
        }
    }
}
