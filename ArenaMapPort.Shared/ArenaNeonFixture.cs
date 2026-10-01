using System;
#nullable enable

namespace ArenaMapPort.Shared
{
    /// <summary>Original optional light installation; world-space mounts, not imported Arena lights.</summary>
    public sealed class ArenaNeonFixture
    {
        public string Name { get; }
        public bool Magenta { get; }
        public float X { get; }
        public float Bottom { get; }
        public float Z { get; }
        public float Height { get; }
        public float NormalX { get; }
        public float NormalZ { get; }
        public float Range { get; }
        public float Intensity { get; }
        public int FlickerSeed { get; }

        public ArenaNeonFixture(string name, bool magenta, float x, float bottom, float z,
            float height, float normalX, float normalZ, float range, float intensity, int flickerSeed = 0)
        {
            Name = name; Magenta = magenta; X = x; Bottom = bottom; Z = z;
            Height = height; NormalX = normalX; NormalZ = normalZ;
            Range = range; Intensity = intensity; FlickerSeed = flickerSeed;
        }
    }

    public static class ArenaNeonLayout
    {
        // New authored strip mounts just outside the original active sign/column/shutter bounds.
        // Mirrors tools/maps/equator.neon.json; no source meshes or textures included.
        public static readonly ArenaNeonFixture[] Equator =
        {
            new("south-sign-left", true, -1.15f, 3.4f, -19.33f, 6.4f, 0, 1, 12, 1.8f, flickerSeed: 47),
            new("south-sign-right", true, .58f, 3.4f, -19.33f, 6.4f, 0, 1, 12, 1.8f),
            new("north-sign-left", true, -1.18f, 3.4f, 25.69f, 6.4f, 0, -1, 12, 1.8f),
            new("north-sign-right", true, .55f, 3.4f, 25.69f, 6.4f, 0, -1, 12, 1.8f),
            new("south-column-west", false, -9.115f, .65f, -21.44f, 3.4f, 0, 1, 9, 3),
            new("south-column-east", false, 8.522f, .65f, -21.44f, 3.4f, 0, 1, 9, 3),
            new("north-column-west", false, -9.784f, .65f, 26.67f, 3.4f, 0, -1, 9, 3, flickerSeed: 11),
            new("north-column-east", false, 8.856f, .65f, 26.79f, 3.4f, 0, -1, 9, 3),
            new("techlight-left", true, 9.71f, .45f, 34.38f, 2.5f, -1, 0, 7, 2.4f),
            new("techlight-right", true, 9.71f, .45f, 38.66f, 2.5f, -1, 0, 7, 2.4f, flickerSeed: 29),
            new("side-column-west", false, -21.51f, .65f, 7.041f, 3.4f, 1, 0, 9, 3),
            new("side-column-east", false, 20.82f, .65f, 5.978f, 3.4f, -1, 0, 9, 3),
        };

        // Decorative native chemlight instances, separate from the indexed strip installation.
        // Checked against the accepted source floor triangles (y = 0.03087).
        public static readonly ArenaNightAccent[] EquatorAccents =
        {
            new("south-west", AccentColor.Amber, -7.8f, -18.5f, 25),
            new("south-east", AccentColor.Green, 6.9f, -18.5f, -30),
            new("north-west", AccentColor.Magenta, -8.1f, 23.5f, 65),
            new("north-east", AccentColor.Amber, 7.4f, 23.5f, -15),
            new("west-lane", AccentColor.Cyan, -18.7f, 7, 40),
            new("east-lane", AccentColor.Magenta, 18, 6, -50),
            new("west-cover", AccentColor.Green, -12.5f, 4, 10),
            new("east-cover", AccentColor.Cyan, 11.7f, -4, 70),
            new("techlight", AccentColor.Amber, 8.3f, 34.7f, -25),
        };
    }

    public enum AccentColor { Cyan, Magenta, Amber, Green }

    public sealed class ArenaNightAccent
    {
        public string Name { get; }
        public AccentColor Color { get; }
        public float X { get; }
        public float Z { get; }
        public float Yaw { get; }
        public ArenaNightAccent(string name, AccentColor color, float x, float z, float yaw)
        { Name = name; Color = color; X = x; Z = z; Yaw = yaw; }
    }

    /// <summary>
    /// Stateless, occasional faulty-neon bursts. Frequency changes the gaps between bursts,
    /// never their duration; the same clock value always produces the same brightness.
    /// </summary>
    public static class ArenaNeonFlicker
    {
        public static float Evaluate(double elapsedSeconds, int seed, float strength, float frequency)
        {
            if (seed <= 0 || elapsedSeconds < 4d || double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds)
                || strength <= 0f || frequency <= 0f || float.IsNaN(strength) || float.IsInfinity(strength)
                || float.IsNaN(frequency) || float.IsInfinity(frequency)) return 1f;

            double amount = Math.Min(strength, 1f);
            double period = 20d / Math.Min(frequency, 3f);
            double age = elapsedSeconds - 4d;
            // Remainder avoids multiplying a very large cycle back into elapsed time. Only
            // the hash key wraps (after billions of cycles), not a running accumulator.
            uint cycle = (uint)(Math.Floor(age / period) % 4294967296d);
            uint key = Mix(unchecked((uint)seed + cycle * 0x9E3779B9u));
            double start = (2d + 14d * Unit(key)) / Math.Min(frequency, 3f);
            double phase = age % period - start;
            double firstLength = .12d + .10d * Unit(Mix(unchecked(key + 1u)));
            double dip = Pulse(phase, firstLength);
            if ((key & 1u) != 0)
            {
                double gap = .07d + .08d * Unit(Mix(unchecked(key + 2u)));
                double secondLength = .07d + .09d * Unit(Mix(unchecked(key + 3u)));
                double secondDepth = .5d + .35d * Unit(Mix(unchecked(key + 4u)));
                dip = Math.Max(dip, secondDepth * Pulse(phase - firstLength - gap, secondLength));
            }
            return (float)(1d - amount * dip);
        }

        static double Pulse(double phase, double length)
        {
            if (phase <= 0d || phase >= length) return 0d;
            const double edge = .018d;
            // Flat center gives a real blackout at full strength; short eased edges avoid
            // a single-frame spike while preserving a recognizable electrical dropout.
            double ramp = Math.Min(1d, Math.Min(phase, length - phase) / edge);
            return ramp * ramp * (3d - 2d * ramp);
        }

        static double Unit(uint value) => (value & 0x00FFFFFFu) / 16777215d;

        static uint Mix(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                return value ^ (value >> 16);
            }
        }
    }

    /// <summary>Unknown time is deliberately off; camera distance only bounds added light cost.</summary>
    public static class ArenaNeonPolicy
    {
        public static bool IsActive(bool enabled, string? mapKey, int? selectedHour) =>
            enabled && mapKey == ArenaEquatorLocation.Key && selectedHour.HasValue &&
            selectedHour == ArenaMapRegistry.Equator.FixedNightHour;

        public static float DistanceFade(float distance)
        {
            if (float.IsNaN(distance) || float.IsInfinity(distance)) return 0;
            return Math.Max(0, Math.Min(1, (50f - distance) / 15f));
        }

        public static float AccentDistanceFade(float distance)
        {
            if (float.IsNaN(distance) || float.IsInfinity(distance)) return 0;
            return Math.Max(0, Math.Min(1, (30f - distance) / 10f));
        }

        public static bool IsFloorRenderer(string name) => name != null &&
            (name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("asphalt", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("parquet", StringComparison.OrdinalIgnoreCase) >= 0 ||
             name.IndexOf("lounge", StringComparison.OrdinalIgnoreCase) >= 0);

        // Native SMap shader: GBuffer1.a = SpecMap.r * _Specularness, despite its name.
        // Start with the material owner's saved baseline, never the already modified value.
        public static float WetFloorSmoothness(bool enabled, string? mapKey, int? hour,
            float configured, float strength)
        {
            if (!IsActive(enabled, mapKey, hour) || float.IsNaN(configured) || float.IsInfinity(configured) ||
                float.IsNaN(strength) || float.IsInfinity(strength) || strength <= 0f) return configured;
            return configured + (Math.Max(configured, 2.2f) - configured) * Math.Min(strength, 1f);
        }
    }
}
