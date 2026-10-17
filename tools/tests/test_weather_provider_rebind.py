"""Execute WeatherRig's actual reactivation method without Unity or deployment.

Only the native collaborators are modeled. The method body is extracted verbatim
from production, including both rebind branches. Compiled omission controls must
fail the behavioral assertion rather than a source-string/compilation check.
"""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "ArenaMapPort.Client" / "WeatherRig.cs"


def reactivation_method(source):
    signature = "static void ReestablishSingletons(HarvestedRig rig)"
    if source.count(signature) != 1:
        raise AssertionError("Expected exactly one production reactivation method")
    start = source.index(signature)
    end = source.index("\n        static void OnSceneUnloaded", start)
    return source[start:end]


HARNESS = r"""
using System;

interface ITODSky { }
sealed class TOD_Sky : ITODSky { public Components Components = new(); }
sealed class Components { public Transform DomeTransform = new(); }
sealed class Transform { public Vector3 position, localScale; }
readonly record struct Vector3(float x, float y, float z)
{
    public static Vector3 zero => new(0, 0, 0);
    public static Vector3 one => new(1, 1, 1);
    public static Vector3 operator *(Vector3 value, float n) => new(value.x*n, value.y*n, value.z*n);
}
sealed class TODSkySimple : ITODSky
{
    public int CameraCalls;
    public void OnCameraCreated() { CameraCalls++; }
}
static class Singleton<T> { public static T Instance; public static void Create(T instance) { Instance = instance; } }
static class TODSkyProvider { public static ITODSky Instance; public static void RegisterInstance(ITODSky value) { Instance = value; } }
sealed class RainController { public void CameraChanged() { } }
sealed class WeatherController
{
    public static WeatherController Instance;
    public RainController RainController = new();
    public void OnCameraChanged() { }
}
sealed class CameraManager
{
    public static bool Exist;
    public static CameraManager Instance;
    public event Action OnCameraChanged;
    public void Changed() { OnCameraChanged?.Invoke(); }
}
sealed class Subscriptions
{
    public int BindCalls;
    public CameraManager Manager;
    public Action Weather, Rain;
    public void Bind(CameraManager manager, Action weather, Action rain)
    { BindCalls++; Manager = manager; Weather = weather; Rain = rain; }
}
enum RigKind { Full, Simple }
sealed class HarvestedRig
{
    public RigKind Kind;
    public TOD_Sky Sky;
    public TODSkySimple SimpleSky;
    public WeatherController Controller;
    public Subscriptions CameraSubscriptions = new();
}
static class Program
{
    __PRODUCTION_METHOD__

    static int _assertions;
    static void Check(bool value, string label)
    { if (!value) throw new Exception("ASSERT: " + label); _assertions++; }

    public static void Main()
    {
        var full = new HarvestedRig { Kind = RigKind.Full, Sky = new(), Controller = new() };
        var simple = new HarvestedRig { Kind = RigKind.Simple, SimpleSky = new() };
        var foreignFull = new TOD_Sky();
        var foreignSimple = new TODSkySimple();
        CameraManager.Exist = true;
        CameraManager.Instance = new();

        // Full donor used, vanilla Simple sky replaces it, donor reused: the
        // provider and singleton must agree even though native Awake did not run.
        foreach (ITODSky previous in new ITODSky[] { foreignSimple, foreignFull, null, full.Sky })
        {
            TODSkyProvider.Instance = previous;
            Singleton<TOD_Sky>.Instance = foreignFull;
            full.Sky.Components.DomeTransform.position = new(4, 5, 6);
            ReestablishSingletons(full);
            Check(ReferenceEquals(TODSkyProvider.Instance, full.Sky), "full-provider-is-owned");
            Check(ReferenceEquals(Singleton<TOD_Sky>.Instance, full.Sky), "full-singleton-is-owned");
            Check(ReferenceEquals(WeatherController.Instance, full.Controller), "weather-controller-is-owned");
            Check(full.Sky.Components.DomeTransform.position == Vector3.zero, "dome-origin-preserved");
            Check(full.Sky.Components.DomeTransform.localScale == Vector3.one*1000f, "dome-scale-preserved");
            Check(ReferenceEquals(full.CameraSubscriptions.Manager, CameraManager.Instance), "full-camera-manager");
            Check(ReferenceEquals(full.CameraSubscriptions.Weather.Target, full.Controller), "full-weather-handler");
            Check(ReferenceEquals(full.CameraSubscriptions.Rain.Target, full.Controller.RainController), "full-rain-handler");
        }

        // Provider restoration is not conditional on a currently available camera.
        CameraManager.Exist = false;
        TODSkyProvider.Instance = foreignSimple;
        int bindings = full.CameraSubscriptions.BindCalls;
        ReestablishSingletons(full);
        Check(ReferenceEquals(TODSkyProvider.Instance, full.Sky), "full-without-camera-provider");
        Check(bindings == full.CameraSubscriptions.BindCalls, "no-full-camera-bind-without-camera");

        CameraManager.Exist = true;
        full.Controller.RainController = null;
        ReestablishSingletons(full);
        Check(full.CameraSubscriptions.Rain == null, "missing-rain-handler-kept-null");

        foreach (ITODSky previous in new ITODSky[] { foreignFull, foreignSimple, null, simple.SimpleSky })
        {
            TODSkyProvider.Instance = previous;
            Singleton<TODSkySimple>.Instance = foreignSimple;
            ReestablishSingletons(simple);
            Check(ReferenceEquals(TODSkyProvider.Instance, simple.SimpleSky), "simple-provider-is-owned");
            Check(ReferenceEquals(Singleton<TODSkySimple>.Instance, simple.SimpleSky), "simple-singleton-is-owned");
            int calls = simple.SimpleSky.CameraCalls;
            CameraManager.Instance.Changed();
            Check(simple.SimpleSky.CameraCalls == calls + 1, "simple-camera-not-double-subscribed");
            Check(ReferenceEquals(WeatherController.Instance, full.Controller), "simple-does-not-replace-weather-controller");
        }
        CameraManager.Exist = false;
        TODSkyProvider.Instance = foreignFull;
        ReestablishSingletons(simple);
        Check(ReferenceEquals(TODSkyProvider.Instance, simple.SimpleSky), "simple-without-camera-provider");

        // Alternating cached owners follows the selected rig, never a foreign or
        // previous donor reference. Native unload is outside this method's scope.
        CameraManager.Exist = true;
        CameraManager.Instance = new();
        ReestablishSingletons(full);
        Check(ReferenceEquals(TODSkyProvider.Instance, full.Sky), "simple-to-full-provider");
        ReestablishSingletons(simple);
        Check(ReferenceEquals(TODSkyProvider.Instance, simple.SimpleSky), "full-to-simple-provider");
        int beforeNewCamera = simple.SimpleSky.CameraCalls;
        CameraManager.Instance.Changed();
        Check(simple.SimpleSky.CameraCalls == beforeNewCamera + 1, "simple-replacement-camera-bound");
        Console.WriteLine("WEATHER_PROVIDER_RUNTIME_PASSED assertions=" + _assertions);
    }
}
"""


def run_reactivation(source):
    # Existing repository convention: isolated SDK-only project, no project
    # references, packages or live/staging targets; temporary files stay in TEMP.
    with tempfile.TemporaryDirectory(prefix="arena-weather-provider-") as directory:
        folder = Path(directory)
        (folder / "Program.cs").write_text(
            HARNESS.replace("__PRODUCTION_METHOD__", reactivation_method(source)), encoding="utf-8")
        project = folder / "Rebind.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                           '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                           '<Nullable>disable</Nullable></PropertyGroup></Project>', encoding="utf-8")
        (folder / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        return subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"],
                              cwd=folder, capture_output=True, text=True, timeout=60)


class WeatherProviderRebindTests(unittest.TestCase):
    def test_reactivation_replaces_provider_and_preserves_existing_collaborators(self):
        result = run_reactivation(SOURCE.read_text(encoding="utf-8"))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("WEATHER_PROVIDER_RUNTIME_PASSED assertions=55", result.stdout)

    def test_missing_full_provider_rebind_is_detected_behaviorally(self):
        source = SOURCE.read_text(encoding="utf-8")
        statement = "TODSkyProvider.RegisterInstance(rig.Sky);"
        self.assertEqual(source.count(statement), 1)
        result = run_reactivation(source.replace(statement, ""))
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ASSERT: full-provider-is-owned", result.stdout + result.stderr)
        self.assertNotIn("WEATHER_PROVIDER_RUNTIME_PASSED", result.stdout)

    def test_missing_simple_provider_rebind_is_detected_behaviorally(self):
        source = SOURCE.read_text(encoding="utf-8")
        statement = "TODSkyProvider.RegisterInstance(rig.SimpleSky);"
        self.assertEqual(source.count(statement), 1)
        result = run_reactivation(source.replace(statement, ""))
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("ASSERT: simple-provider-is-owned", result.stdout + result.stderr)
        self.assertNotIn("WEATHER_PROVIDER_RUNTIME_PASSED", result.stdout)


if __name__ == "__main__":
    outcome = unittest.main(exit=False).result
    if not outcome.wasSuccessful():
        sys.exit(1)
    print("WEATHER_PROVIDER_REBIND_CHECKS_PASSED")
