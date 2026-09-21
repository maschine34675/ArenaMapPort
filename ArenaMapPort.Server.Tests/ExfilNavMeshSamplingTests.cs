using ArenaMapPort.Client;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    public sealed class ExfilNavMeshSamplingTests
    {
        [Fact]
        public void ReachableAuthoredFloorWinsBeforeWideQueryCanChooseAnotherStorey()
        {
            // Reproduce the host observation: the old 6 m query chose y=2.6,
            // while a small query found the reachable floor near authored y=-2.9.
            var calls = new List<float>();
            bool Sample(float radius, out float sampledHeight)
            {
                calls.Add(radius);
                sampledHeight = radius <= 2f ? -2.9f : 2.6f;
                return true;
            }
            Assert.True(Sample(6f, out float oldHeight));
            Assert.Equal(2.6f, oldHeight);
            calls.Clear();

            Assert.True(ExfilNavMeshSampling.TrySample<float>(Sample, out float height, out float usedRadius));
            Assert.Equal(-2.9f, height);
            Assert.Equal(1f, usedRadius);
            Assert.Equal(new[] { 1f }, calls);
            Assert.NotEqual(oldHeight, height);
        }

        [Fact]
        public void ApproximateAuthoredExitStillUsesTheLegacyWideFallbackWhenNeeded()
        {
            var calls = new List<float>();
            bool Sample(float radius, out float sampledHeight)
            {
                calls.Add(radius);
                sampledHeight = -1.2f;
                return radius >= 4f;
            }

            Assert.True(ExfilNavMeshSampling.TrySample<float>(Sample, out float height, out float usedRadius));
            Assert.Equal(-1.2f, height);
            Assert.Equal(6f, usedRadius);
            Assert.Equal(new[] { 1f, 6f }, calls);
        }

        [Fact]
        public void MissingSurfaceReportsFailureAfterBothBoundedQueries()
        {
            var calls = new List<float>();
            bool Sample(float radius, out float sampledHeight)
            {
                calls.Add(radius);
                sampledHeight = default;
                return false;
            }

            Assert.False(ExfilNavMeshSampling.TrySample<float>(Sample, out _, out float usedRadius));
            Assert.Equal(6f, usedRadius);
            Assert.Equal(new[] { 1f, 6f }, calls);
        }
    }
}
