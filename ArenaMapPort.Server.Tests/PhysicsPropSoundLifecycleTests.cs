#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xunit;

namespace ArenaMapPort.Server.Tests
{
    [Collection("MapPort client lifecycle")]
    public sealed class PhysicsPropSoundLifecycleTests : IDisposable
    {
        public PhysicsPropSoundLifecycleTests() { SceneManager.Scenes.Clear(); Time.time = 100; }
        public void Dispose() => SceneManager.Scenes.Clear();

        public static IEnumerable<object[]> OwnedScenes => ArenaMapRegistry.All.SelectMany(map =>
            new[] { false, true }.SelectMany(loaded => new[] { false, true }.Select(cached =>
                new object[] { map.AllSceneNames.First(name => name != map.ScriptsSceneName), loaded, cached })));

        [Theory]
        [MemberData(nameof(OwnedScenes))]
        public void GeometryOutlivingScriptsNeverTouchesReleasedAudio(string sceneName, bool loaded, bool cached)
        {
            var prop = Prop(new Scene(sceneName) { isLoaded = loaded });
            prop.AfterSetup = () => throw new InvalidOperationException("weaponsounds.bundle has been released");
            SoundBank bank = cached ? new SoundBank() : null;
            float timer = -1;
            Assert.False(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, true, ref bank, ref timer));
            Assert.Equal(0, prop.SetupCalls);
            Assert.Equal(100.5f, timer);
        }

        [Theory]
        [InlineData("Factory_Day_Scripts")]
        [InlineData("SomeOtherModsScene")]
        [InlineData(null)]
        public void UnownedOrInvalidSceneRetainsNativeCallback(string name)
        {
            var prop = Prop(new Scene(name));
            SoundBank bank = null;
            float timer = -1;
            Assert.True(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Equal(0, prop.SetupCalls);
            Assert.Equal(-1, timer);
        }

        [Fact]
        public void LiveMapStillResolvesAndPlaysItsValidBank()
        {
            var map = ArenaMapRegistry.All[0];
            SceneManager.Scenes.Add(new Scene(map.ScriptsSceneName));
            var prop = Prop(new Scene(map.AllSceneNames.First()));
            SoundBank bank = null;
            prop.AfterSetup = () => bank = new SoundBank();
            float timer = -1;
            Assert.True(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Equal(1, prop.SetupCalls);
            Assert.NotNull(bank);
            Assert.Equal(-1, timer);
        }

        [Fact]
        public void LiveMissingBankStillReportsDegradedSoundAndKeepsCollisionTimer()
        {
            SceneManager.Scenes.Add(new Scene(ArenaMapRegistry.All[0].ScriptsSceneName));
            var prop = Prop(new Scene(ArenaMapRegistry.All[0].ScriptsSceneName));
            prop.SoundType = 73029;
            int warnings = Plugin.Log.Warnings.Count;
            SoundBank bank = null;
            float timer = -1;
            Assert.False(PhysicsPropSoundRepairs.OnCollisionHandlerPrefix(prop, false, ref bank, ref timer));
            Assert.Equal(1, prop.SetupCalls);
            Assert.Equal(100.5f, timer);
            Assert.Equal(warnings + 1, Plugin.Log.Warnings.Count);
            Assert.Contains("WeaponSounds has no impact SoundBank", Plugin.Log.Warnings.Last());
        }

        static SmallPhysicsObject Prop(Scene scene) => new() { gameObject = new GameObject("late prop") { scene = scene } };
    }
}

// Native API boundaries only. The prefix and scene-ownership policy are linked production source.
public sealed class SoundBank { }
public sealed class SmallPhysicsObject
{
    public GameObject gameObject;
    public string name => gameObject.name;
    public int SoundType, SetupCalls;
    public Action AfterSetup;
    public void SetupSoundBank() { SetupCalls++; AfterSetup?.Invoke(); }
    public void OnCollisionHandler(bool resetSound) { }
}
namespace UnityEngine { public static class Time { public static float time; } }
