#nullable disable
using System;
using System.Collections.Generic;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Which nodes of the recovered acoustic graph may keep a negative local scale. Unity-free on purpose (plain numbers and
    /// ids), so the xunit project links this file; <see cref="NativeSpatialAudio"/> applies it while it builds the inactive
    /// hierarchy.
    ///
    /// What vanilla and the source do: Arena authors some AudioTriggerArea volumes mirrored (Iceberg: ten, in the rooms
    /// corridor_2_1 and coffe_hall; a scale of -1.467 on X or -7.17 on Z). A mirrored node is created with that scale and EFT
    /// reads the volume only through the transform: <c>Collider.bounds</c> (the room bounds, <c>SpatialAudioRoom.GetRoomBounds</c>),
    /// the physics trigger shape that raises the enter and exit events, and <c>AIUtility.PointInOABB</c>, which maps the point
    /// into the box's local space with <c>InverseTransformPoint</c> and compares it with the unsigned half size around the
    /// local centre, so the sign cancels. Portals are different: <c>BaseSpatialAudioPortal</c> multiplies the collider size by
    /// the SIGNED <c>localScale</c>, so a mirrored portal would flip its size and its normal.
    ///
    /// What the port does: the exporter (tools/extract/extract_spatial_audio.py, <c>mirrored_volume_nodes</c>) lets exactly one node
    /// class keep a negative sign and proves each such volume against Arena's own records (the cached OBB with the signed
    /// extent, and the room bounds Unity computed in the editor, and demands a collider centre of 0 on the mirrored axis, so the
    /// reflection maps the box onto itself and the sign cannot move it: all ten of Iceberg's are a unit box centred on the pivot
    /// whose shape comes from the scale); this class is the same rule for the client. Everything else keeps the old strict rule:
    /// no negative scale, no zero scale on a mirrored volume.
    ///
    /// Host check still open (docs/progress/iceberg-release.md): a player standing inside a mirrored area must be classified
    /// into its room (ListenerCurrentRoom), proving the trigger shape.
    /// </summary>
    internal static class AcousticVolumeRules
    {
        /// <summary>
        /// The node ids allowed to be mirrored: the own node of an AudioTriggerArea, when no room, portal, group or system
        /// component sits on that node and no other node has it as a parent (nothing below a mirrored node is reflected with it).
        /// </summary>
        internal static HashSet<long> MirroredVolumeNodes(IEnumerable<long> areaNodes, IEnumerable<long> otherComponentNodes,
            IEnumerable<long> parentNodes)
        {
            if (areaNodes == null) throw new ArgumentNullException(nameof(areaNodes));
            var allowed = new HashSet<long>(areaNodes);
            if (otherComponentNodes != null) allowed.ExceptWith(otherComponentNodes);
            if (parentNodes != null) allowed.ExceptWith(parentNodes);
            return allowed;
        }

        /// <summary>The reason a node scale is refused, or null when it is accepted. A mirrored volume needs all three axes
        /// non-zero (a flat volume cannot contain a player, and the exporter refuses it too).</summary>
        internal static string ScaleProblem(float x, float y, float z, bool mirroredVolume)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) || float.IsNaN(z) || float.IsInfinity(z))
                return "nonfinite node scale";
            if (x >= 0 && y >= 0 && z >= 0) return null;
            if (!mirroredVolume) return "negative node scale";
            if (x == 0 || y == 0 || z == 0) return "degenerate mirrored volume";
            return null;
        }
    }
}
