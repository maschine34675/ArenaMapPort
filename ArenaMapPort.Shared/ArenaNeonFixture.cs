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

        public ArenaNeonFixture(string name, bool magenta, float x, float bottom, float z,
            float height, float normalX, float normalZ, float range, float intensity)
        {
            Name = name; Magenta = magenta; X = x; Bottom = bottom; Z = z;
            Height = height; NormalX = normalX; NormalZ = normalZ;
            Range = range; Intensity = intensity;
        }
    }

    public static class ArenaNeonLayout
    {
        // New authored strip mounts just outside the original active sign/column/shutter bounds.
        // Mirrors tools/maps/equator.neon.json; no source meshes or textures included.
        public static readonly ArenaNeonFixture[] Equator =
        {
            new("south-sign-left", true, -1.15f, 3.4f, -19.33f, 6.4f, 0, 1, 12, 1.8f),
            new("south-sign-right", true, .58f, 3.4f, -19.33f, 6.4f, 0, 1, 12, 1.8f),
            new("north-sign-left", true, -1.18f, 3.4f, 25.69f, 6.4f, 0, -1, 12, 1.8f),
            new("north-sign-right", true, .55f, 3.4f, 25.69f, 6.4f, 0, -1, 12, 1.8f),
            new("south-column-west", false, -9.115f, .65f, -21.44f, 3.4f, 0, 1, 9, 3),
            new("south-column-east", false, 8.522f, .65f, -21.44f, 3.4f, 0, 1, 9, 3),
            new("north-column-west", false, -9.784f, .65f, 26.67f, 3.4f, 0, -1, 9, 3),
            new("north-column-east", false, 8.856f, .65f, 26.79f, 3.4f, 0, -1, 9, 3),
            new("techlight-left", true, 9.71f, .45f, 34.38f, 2.5f, -1, 0, 7, 2.4f),
            new("techlight-right", true, 9.71f, .45f, 38.66f, 2.5f, -1, 0, 7, 2.4f),
            new("side-column-west", false, -21.51f, .65f, 7.041f, 3.4f, 1, 0, 9, 3),
            new("side-column-east", false, 20.82f, .65f, 5.978f, 3.4f, -1, 0, 9, 3),
        };
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
    }
}
