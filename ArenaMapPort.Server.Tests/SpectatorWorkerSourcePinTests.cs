#nullable disable
using System.Text.RegularExpressions;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    /// <summary>
    /// Source pins for the crowd loader's worker thread (docs/progress/spectators-allmaps.md, "Load cost"). ArenaSpectators.cs is Unity glue and
    /// cannot run here, so the rules the worker must keep are read from the text: the code that runs off the main thread (LoadPackage,
    /// TextureOrder, Prepared, the texture package and its prefetch) calls no Unity API and never logs, Plugin.Awake wires the early start
    /// exactly once, and nothing that can throw runs in Create before its try block (a crowd problem must never reach EFT's load chain).
    /// </summary>
    public sealed class SpectatorWorkerSourcePinTests
    {
        static string Text(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, relative);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            throw new FileNotFoundException(relative);
        }

        static string Code(string relative) => Regex.Replace(Text(relative), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

        static string Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, "missing: " + start);
            int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            Assert.True(b > a, "missing end marker: " + end);
            return text.Substring(a, b - a);
        }

        const string Crowd = "ArenaMapPort.Client/ArenaSpectators.cs";
        const string Package = "ArenaMapPort.Client/SpectatorTexturePackage.cs";

        // Unity types and the plugin logger: none of them may appear in code that runs on a worker thread.
        static readonly string[] Forbidden =
        {
            "UnityEngine", "GameObject", "Texture2D", "Cubemap", "Material", "Mesh(", "Object.", "Time.", "Mathf", "Resources.", "SceneManager",
            "Application.", "Plugin.Log", "Debug.", "Vector3", "Quaternion", "Singleton<"
        };

        static void AssertNoUnityOrLogging(string label, string code)
        {
            foreach (var token in Forbidden)
                Assert.False(code.Contains(token, StringComparison.Ordinal), label + " must not use " + token + " (it runs off the main thread)");
        }

        [Fact]
        public void TheWorkerSideOfTheCrowdLoaderUsesNoUnityApiAndNeverLogs()
        {
            string code = Code(Crowd);
            AssertNoUnityOrLogging("LoadPackage", Between(code, "static Loaded LoadPackage(", "static string[] TextureOrder("));
            AssertNoUnityOrLogging("TextureOrder", Between(code, "static string[] TextureOrder(", "sealed class Prepared"));
            AssertNoUnityOrLogging("Prepared", Between(code, "sealed class Prepared", "sealed class Loaded"));
            AssertNoUnityOrLogging("ValidateContainer", Between(code, "static void ValidateContainer(", "void Build("));
        }

        [Fact]
        public void TheTexturePackageAndItsPrefetchWorkerAreUnityFree()
        {
            string code = Code(Package);
            Assert.DoesNotContain("using UnityEngine", code);
            AssertNoUnityOrLogging("SpectatorTexturePackage.cs", code);
        }

        [Fact]
        public void ThePluginWiresTheEarlyStartExactlyOnce()
        {
            string code = Code("ArenaMapPort.Client/Plugin.cs");
            Assert.Equal(1, Regex.Matches(code, @"ArenaSpectators\.Install\(\);").Count);
        }

        [Fact]
        public void CreateStartsAndTakesThePreparedPackageInsideItsTry()
        {
            string code = Code(Crowd);
            string create = Between(code, "internal static void Create()", "static Loaded LoadPackage(");
            int tryAt = create.IndexOf("try", StringComparison.Ordinal);
            Assert.True(tryAt > 0);
            foreach (var call in new[] { "TakePrepared(", "PrepareInBackground(", "prepared.Join(" })
            {
                int at = create.IndexOf(call, StringComparison.Ordinal);
                Assert.True(at > tryAt, call + " must run inside Create's try block");
            }
        }

        [Fact]
        public void TheGatherersOfTheWorkerNeverTouchTheConfigOrTheScene()
        {
            string code = Code(Crowd);
            string work = Between(code, "sealed class Prepared", "sealed class Loaded");
            Assert.DoesNotContain("Settings", work);
            Assert.DoesNotContain("GetRootGameObjects", work);
            Assert.DoesNotContain("FindObjectsOfType", work);
        }
    }
}
