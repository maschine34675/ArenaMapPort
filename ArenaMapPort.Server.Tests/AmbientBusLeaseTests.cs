#nullable disable
using ArenaMapPort.Client;
using UnityEngine;
using UnityEngine.Audio;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    [Collection("MapPort client lifecycle")]
    public sealed class AmbientBusLeaseTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SharedSceneryAndRainOwnersRestoreOnlyAfterLastRelease(bool rainLeavesFirst)
        {
            var mixer = new AudioMixer();
            var scenery = AmbientBusLease.Acquire(mixer, "AmbientInVolume");
            var rain = AmbientBusLease.Acquire(mixer, "AmbientInVolume");
            Assert.Equal(0f, mixer.Values["AmbientInVolume"]);
            (rainLeavesFirst ? rain : scenery).Dispose();
            Assert.Equal(0f, mixer.Values["AmbientInVolume"]);
            (rainLeavesFirst ? scenery : rain).Dispose();
            Assert.Equal(-80f, mixer.Values["AmbientInVolume"]);
            scenery.Dispose(); rain.Dispose(); // No negative reference count or second restoration.
            Assert.Equal(2, mixer.Writes.Count);
        }

        [Fact]
        public void OtherMapAndOtherBusNeverShareAnOwner()
        {
            var mixer = new AudioMixer(); var other = new AudioMixer();
            using var inside = AmbientBusLease.Acquire(mixer, "AmbientInVolume");
            using var outside = AmbientBusLease.Acquire(mixer, "AmbientOutVolume");
            using var otherInside = AmbientBusLease.Acquire(other, "AmbientInVolume");
            inside.Dispose();
            Assert.Equal(-80f, mixer.Values["AmbientInVolume"]);
            Assert.Equal(0f, mixer.Values["AmbientOutVolume"]);
            Assert.Equal(0f, other.Values["AmbientInVolume"]);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-12f)]
        public void ExistingMixIsNeitherRaisedNorRestoredOver(float original)
        {
            var mixer = new AudioMixer(); mixer.Values["AmbientInVolume"] = original;
            using (var lease = AmbientBusLease.Acquire(mixer, "AmbientInVolume"))
                Assert.Equal(original == 0f, lease.IsNeutral);
            Assert.Equal(original, mixer.Values["AmbientInVolume"]);
            Assert.Empty(mixer.Writes);
        }

        [Fact]
        public void ExternalWriterWinsEvenIfTheMixLaterReturnsToZero()
        {
            var mixer = new AudioMixer();
            using var lease = AmbientBusLease.Acquire(mixer, "AmbientOutVolume");
            mixer.Values["AmbientOutVolume"] = -6;
            Assert.False(lease.IsNeutral);
            mixer.Values["AmbientOutVolume"] = 0;
            lease.Dispose();
            Assert.Equal(0, mixer.Values["AmbientOutVolume"]);
            Assert.Single(mixer.Writes);
        }

        [Fact]
        public void ExternalWriterIsPreservedWithoutAnIntermediatePoll()
        {
            var mixer = new AudioMixer();
            var lease = AmbientBusLease.Acquire(mixer, "AmbientOutVolume");
            mixer.Values["AmbientOutVolume"] = -10;
            lease.Dispose();
            Assert.Equal(-10, mixer.Values["AmbientOutVolume"]);
        }

        [Fact]
        public void ExplicitAudioResetReopensOnlyTheNativeResetValue()
        {
            var mixer = new AudioMixer();
            using var lease = AmbientBusLease.Acquire(mixer, "AmbientOutVolume");
            mixer.Values["AmbientOutVolume"] = -80;
            AudioSettings.ResetForTest();
            Assert.True(lease.IsNeutral);
            Assert.Equal(0, mixer.Values["AmbientOutVolume"]);
            mixer.Values["AmbientOutVolume"] = -7;
            AudioSettings.ResetForTest();
            Assert.False(lease.IsNeutral);
            lease.Dispose();
            Assert.Equal(-7, mixer.Values["AmbientOutVolume"]);
        }

        [Fact]
        public void FailedAcquisitionLeavesNoPhantomOwner()
        {
            var mixer = new AudioMixer { RejectWrites = true };
            Assert.Throws<InvalidOperationException>(() => AmbientBusLease.Acquire(mixer, "AmbientInVolume"));
            mixer.RejectWrites = false;
            using (var lease = AmbientBusLease.Acquire(mixer, "AmbientInVolume")) Assert.True(lease.IsNeutral);
            Assert.Equal(-80, mixer.Values["AmbientInVolume"]);
            Assert.Throws<ArgumentException>(() => AmbientBusLease.Acquire(mixer, "MainVolume"));
            Assert.False(mixer.Values.ContainsKey("MainVolume"));
        }
    }
}

// Endpoint doubles measure ownership writes; they do not simulate Unity's audio engine.
namespace UnityEngine.Audio
{
    public sealed class AudioMixer
    {
        public readonly Dictionary<string, float> Values = new() { ["AmbientInVolume"] = -80, ["AmbientOutVolume"] = -80 };
        public readonly List<(string Key, float Value)> Writes = new();
        public bool RejectWrites;
        public bool GetFloat(string key, out float value) => Values.TryGetValue(key, out value);
        public bool SetFloat(string key, float value)
        {
            if (RejectWrites || !Values.ContainsKey(key)) return false;
            Values[key] = value; Writes.Add((key, value)); return true;
        }
    }
}
namespace UnityEngine
{
    public static class Mathf { public static bool Approximately(float a, float b) => Math.Abs(a - b) < 0.00001f; }
    public static class AudioSettings
    {
        public static event Action<bool> OnAudioConfigurationChanged;
        public static void ResetForTest() => OnAudioConfigurationChanged?.Invoke(true);
    }
}
