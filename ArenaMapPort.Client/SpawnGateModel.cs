#nullable disable
using System;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The pure rules behind <see cref="SpawnGates"/> (no Unity types, linked into the server tests): which gate part
    /// slides which way when a booth opens, how a NavMesh carver box is sized from a part's world bounds, when a held
    /// gate releases itself, and how a NavMesh probe across the doorway is judged. The numbers are design data: the
    /// slide distance is the leaf width of Equator's sliding respawn doors, which equals the depth of the frame pocket
    /// next to each leaf (work/extract/equator-native-v5, leaf x 0..1.38, frame x up to 2.82). Since 2026-10-23 the slide is
    /// only the fallback for a leaf WITHOUT an Arena pose: Arena's own motion (its DotAnimator script, travel 1.278 to
    /// 1.294 m, 5 to 7 s) is in the descriptor's DoorPoses for all ten Equator leaves and is driven by SpawnGates through
    /// DoorPoseModel, so this inference moves no leaf of the shipped maps.
    /// </summary>
    internal static class SpawnGateModel
    {
        /// <summary>Name prefix of Arena's sliding respawn-booth door assemblies and their two leaves.</summary>
        internal const string DoorPrefix = "Door_Sliding_Respawn";

        /// <summary>How far a sliding leaf WITHOUT an Arena pose moves along its local x axis when the booth opens (metres).</summary>
        internal const float SlideDistance = 1.38f;

        /// <summary>Extra width per horizontal side of a carver box, so the agent radius (0.3 m on the ported bakes)
        /// cannot squeeze past the edge of a thin leaf.</summary>
        internal const float CarveMargin = 0.2f;

        /// <summary>Extra height of a carver box above the part, so the carve covers the walkable surface fully.</summary>
        internal const float CarveHeightMargin = 0.5f;

        /// <summary>Distance from the gate's centre to each probe point across its thin axis (metres).</summary>
        internal const float ProbeReach = 1.5f;

        /// <summary>Search radius when snapping a probe point to the NavMesh (metres).</summary>
        internal const float ProbeSampleRadius = 1.5f;

        /// <summary>Unity's NavMeshPathStatus.PathComplete as an int, so this file stays free of Unity types.</summary>
        internal const int PathComplete = 0;

        /// <summary>+1 for the left leaf (slides towards +x into its frame pocket), -1 for the right leaf, 0 for any
        /// other gate part (those switch off without a motion: AirPit's bus doors). Used for a leaf that has no pose row.</summary>
        internal static int SlideDirection(string partName)
        {
            if (partName == DoorPrefix + "_L") return 1;
            if (partName == DoorPrefix + "_R") return -1;
            return 0;
        }

        /// <summary>Gates are held closed only when the feature is on and either a caller asked for it (ArenaMode) or the
        /// developer test hold is on.</summary>
        internal static bool ShouldHold(bool featureEnabled, bool requested, bool testHold) =>
            featureEnabled && (requested || testHold);

        /// <summary>A held gate (descriptor HeldGates) keeps its parts closed at raid start only while the feature is on
        /// and carving is on (an uncarved closed leaf on a mesh that runs through it is the shutter trap), and until a
        /// Release that follows a hold opened it this raid.</summary>
        internal static bool HeldGateStaysClosed(bool featureEnabled, bool carveEnabled, bool openedThisRaid) =>
            featureEnabled && carveEnabled && !openedThisRaid;

        /// <summary>A timed hold releases once the raid clock since GameStatus.Started reaches the hold length; a hold
        /// length of zero or less never releases by time.</summary>
        internal static bool AutoReleaseDue(float secondsSinceStarted, float holdSeconds) =>
            holdSeconds > 0f && secondsSinceStarted >= holdSeconds;

        /// <summary>Carver box (centre and size) for a gate part from its world-space AABB: the horizontal sides grow by
        /// <see cref="CarveMargin"/>, the top by <see cref="CarveHeightMargin"/>, the bottom stays at the part's floor line
        /// so the carve starts at the walkable surface.</summary>
        internal static void CarverBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
            out float centreX, out float centreY, out float centreZ, out float sizeX, out float sizeY, out float sizeZ)
        {
            if (maxX < minX || maxY < minY || maxZ < minZ) throw new ArgumentException("inverted bounds");
            sizeX = (maxX - minX) + 2f * CarveMargin;
            sizeZ = (maxZ - minZ) + 2f * CarveMargin;
            sizeY = (maxY - minY) + CarveHeightMargin;
            centreX = (minX + maxX) * 0.5f;
            centreZ = (minZ + maxZ) * 0.5f;
            centreY = minY + sizeY * 0.5f;
        }

        /// <summary>The horizontal axis a gate is thin on, which the probe path has to cross: 0 for x, 2 for z.</summary>
        internal static int ThinAxis(float sizeX, float sizeZ) => sizeX <= sizeZ ? 0 : 2;

        /// <summary>An enclosure's verdict from its probed doorways (the parts under one barrier root or door assembly):
        /// while held, an enclosure contradicts the hold only when none of its probed doorways is sealed (the carve took
        /// nowhere); a doorway that stays passable behind a sealed row lies inside the box (AirPit's bus pocket has an
        /// authored-open inner row behind the closed outer row, GPU-2 2026-10-17). After a release every doorway must be
        /// open again.</summary>
        internal static bool EnclosureContradicts(bool expectSealed, int probedDoorways, int sealedDoorways) =>
            expectSealed ? probedDoorways > 0 && sealedDoorways == 0 : sealedDoorways > 0;

        /// <summary>A carved doorway yields a Partial or Invalid NavMesh path between the two probe points; a complete
        /// path means the gate is NOT sealed for bots.</summary>
        internal static bool Sealed(int pathStatus) => pathStatus != PathComplete;
    }
}
