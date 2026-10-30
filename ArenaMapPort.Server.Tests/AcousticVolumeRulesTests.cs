#nullable disable
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    /// <summary>
    /// The mirrored trigger volume rule of the native acoustic graph (docs/progress/iceberg-release.md, "Spatial acoustics"; the
    /// exporter side is tools/extract/extract_spatial_audio.py <c>mirrored_volume_nodes</c> and tools/tests/test_spatial_audio_iceberg.py).
    /// AcousticVolumeRules is Unity-free and linked into this project; NativeSpatialAudio sits on Unity and EFT types, so its use of the
    /// rule is read as text, like the other per-map audio files.
    /// </summary>
    public sealed class AcousticVolumeRulesTests
    {
        static IEnumerable<long> Ids(params long[] ids) => ids;

        [Fact]
        public void OnlyTheOwnNodeOfAnAreaMayBeMirrored()
        {
            // Nodes 1 and 2 hold areas; 3 is a room node; 4 a portal; 10 the group and system node.
            var allowed = AcousticVolumeRules.MirroredVolumeNodes(Ids(1, 2), Ids(3, 4, 10), Ids(3));
            Assert.Equal(new long[] { 1, 2 }, allowed.OrderBy(x => x));
            Assert.DoesNotContain(3L, allowed);
            Assert.DoesNotContain(4L, allowed);
            Assert.DoesNotContain(10L, allowed);
        }

        [Fact]
        public void AnAreaNodeThatAlsoCarriesARoomPortalGroupOrSystemStaysStrict()
        {
            // The synthetic graphs of the Python tests put the area and the room on one node; that node is not a leaf volume.
            Assert.Empty(AcousticVolumeRules.MirroredVolumeNodes(Ids(1), Ids(1), Ids()));
            Assert.Empty(AcousticVolumeRules.MirroredVolumeNodes(Ids(7), Ids(7, 8), Ids()));
        }

        [Fact]
        public void AnAreaNodeWithChildrenStaysStrict()
        {
            // Nothing below a mirrored node is reflected with it: a node that is somebody's parent is never allowed.
            Assert.Empty(AcousticVolumeRules.MirroredVolumeNodes(Ids(5), Ids(), Ids(5)));
            Assert.Equal(new long[] { 6 }, AcousticVolumeRules.MirroredVolumeNodes(Ids(5, 6), Ids(), Ids(5)));
        }

        [Fact]
        public void NoAreasMeansNoMirroredNodesAndNullListsAreTolerated()
        {
            Assert.Empty(AcousticVolumeRules.MirroredVolumeNodes(Ids(), Ids(1, 2), Ids(3)));
            Assert.Equal(new long[] { 9 }, AcousticVolumeRules.MirroredVolumeNodes(Ids(9), null, null));
            Assert.Throws<ArgumentNullException>(() => AcousticVolumeRules.MirroredVolumeNodes(null, Ids(), Ids()));
        }

        [Theory]
        [InlineData(1f, 1f, 1f)]
        [InlineData(0f, 3f, 5f)]   // a zero-depth portal plane is authored data and stays valid
        [InlineData(1.467f, 5.239f, 5.634f)]
        public void ANonNegativeScaleIsAlwaysAccepted(float x, float y, float z)
        {
            Assert.Null(AcousticVolumeRules.ScaleProblem(x, y, z, false));
            Assert.Null(AcousticVolumeRules.ScaleProblem(x, y, z, true));
        }

        [Theory]
        [InlineData(-1.467f, 5.239f, 5.634f)] // Iceberg corridor_2_1 area (node 28 of the graph)
        [InlineData(19.507f, 3.213f, -7.17f)] // Iceberg coffe_hall area (node 296)
        [InlineData(-1f, -1f, 1f)]
        [InlineData(-1f, -1f, -1f)]
        public void ANegativeScaleIsRefusedUnlessTheNodeIsAMirroredVolume(float x, float y, float z)
        {
            Assert.Equal("negative node scale", AcousticVolumeRules.ScaleProblem(x, y, z, false));
            Assert.Null(AcousticVolumeRules.ScaleProblem(x, y, z, true));
        }

        [Theory]
        [InlineData(-1f, 0f, 1f)]
        [InlineData(0f, 2f, -3f)]
        public void AMirroredVolumeCannotBeFlat(float x, float y, float z)
        {
            Assert.Equal("degenerate mirrored volume", AcousticVolumeRules.ScaleProblem(x, y, z, true));
            Assert.Equal("negative node scale", AcousticVolumeRules.ScaleProblem(x, y, z, false));
        }

        [Theory]
        [InlineData(float.NaN, 1f, 1f)]
        [InlineData(1f, float.PositiveInfinity, 1f)]
        [InlineData(1f, 1f, float.NegativeInfinity)]
        public void ANonFiniteScaleIsRefusedInEveryCase(float x, float y, float z)
        {
            Assert.Equal("nonfinite node scale", AcousticVolumeRules.ScaleProblem(x, y, z, false));
            Assert.Equal("nonfinite node scale", AcousticVolumeRules.ScaleProblem(x, y, z, true));
        }

        // ---- source pins: NativeSpatialAudio applies exactly this rule -------------------------------------------------

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

        [Fact]
        public void BuildGraphAppliesTheRuleToEveryNode()
        {
            var code = Code("ArenaMapPort.Client/NativeSpatialAudio.cs");
            // The mirrored set is built from the graph's own areas, from every other component kind and from the parent ids ...
            Assert.Matches(@"AcousticVolumeRules\.MirroredVolumeNodes\(\s*data\[""areas""\]\.Select\(a => \(long\)a\[""node_id""\]\),", code);
            Assert.Contains(@"new[] { ""rooms"", ""portals"", ""groups"", ""systems"" }", code);
            Assert.Contains(@"data[""nodes""].Where(n => n[""parent_id""].Type != JTokenType.Null).Select(n => (long)n[""parent_id""])", code);
            // ... and every node's scale goes through ScaleProblem with its own membership; the old unconditional check is gone.
            Assert.Matches(@"AcousticVolumeRules\.ScaleProblem\(scale\.x, scale\.y, scale\.z, mirrored\.Contains\(\(long\)node\[""id""\]\)\);\s*Require\(scaleProblem == null, scaleProblem\);", code);
            Assert.DoesNotContain(@"""negative node scale""", code);
        }

        [Fact]
        public void TheRuleFileStaysFreeOfUnityEftAndLogging()
        {
            var code = Code("ArenaMapPort.Client/AcousticVolumeRules.cs");
            foreach (var token in new[] { "UnityEngine", "GameObject", "Vector3", "Plugin.Log", "Debug.", "Newtonsoft", "BepInEx", "Singleton<" })
                Assert.False(code.Contains(token, StringComparison.Ordinal), "AcousticVolumeRules must not use " + token);
        }

        [Fact]
        public void NoOtherClientFileRelaxesTheNegativeScaleCheck()
        {
            // The mirrored exception lives in this rule only: the graph builder is the one place that creates acoustic nodes.
            foreach (var file in new[] { "ArenaMapPort.Client/RoomReverb.cs", "ArenaMapPort.Client/RoomShotAcoustics.cs", "ArenaMapPort.Client/PrecipitationAudio.cs" })
                Assert.DoesNotContain("AcousticVolumeRules", Code(file));
        }
    }
}
