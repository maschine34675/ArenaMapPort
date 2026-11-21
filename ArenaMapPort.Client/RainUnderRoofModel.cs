#nullable disable
using System;

namespace ArenaMapPort.Client
{
    /// <summary>How an <c>AsyncGPUReadback</c> of a render texture is laid out compared with the texture's own texel coordinates.</summary>
    internal enum RainProbeOrientation
    {
        /// <summary>The probe was not read, or its data fits neither layout: the readback cannot be trusted.</summary>
        Unusable,
        /// <summary>Row 0 of the readback is the texture's row 0 (the layout <c>ReadPixels</c> and the F8 readback use).</summary>
        AsWritten,
        /// <summary>Row 0 of the readback is the texture's last row.</summary>
        FlippedY,
    }

    /// <summary>Why <see cref="RainUnderRoofState.Decide"/> gave no answer (the flag then stays at vanilla's value).</summary>
    internal enum RainNotReady
    {
        None,
        NoPhoto,
        NoOrientation,
        OutsidePhoto,
        NoResult,
        StaleResult,
    }

    /// <summary>The conversion parameters of ONE <c>DepthPhotograper.Render()</c>, captured right after it (a later render may change every field).</summary>
    internal readonly struct RainPhoto
    {
        internal readonly float StartX, StartZ, ScaleX, ScaleZ, TopY, FarClip;
        internal readonly int Dim;
        internal readonly bool ReversedZ;

        internal RainPhoto(float startX, float startZ, float scaleX, float scaleZ, int dim, float topY, float farClip, bool reversedZ)
        {
            StartX = startX; StartZ = startZ; ScaleX = scaleX; ScaleZ = scaleZ;
            Dim = dim; TopY = topY; FarClip = farClip; ReversedZ = reversedZ;
        }

        /// <summary>A photo can be read only with a sane dimension, a positive scale and a positive depth range.</summary>
        internal bool IsUsable =>
            Dim >= 16 && Dim <= 8192 && ScaleX > 0f && ScaleZ > 0f && FarClip > 0f
            && !float.IsNaN(TopY) && !float.IsInfinity(TopY) && !float.IsNaN(StartX) && !float.IsNaN(StartZ)
            && !float.IsInfinity(ScaleX) && !float.IsInfinity(ScaleZ) && !float.IsInfinity(FarClip);

        /// <summary>Metres per texel along x.</summary>
        internal float TexelMetres => 1f / (ScaleX * Dim);
    }

    /// <summary>One small readback request: the texel rectangle to ask for (in the orientation the readback uses) and the render it belongs to.</summary>
    internal readonly struct RainWindow
    {
        internal readonly int X, Y, Width, Height;
        internal readonly int CenterPx, CenterPy;
        internal readonly float CamX, CamZ;
        internal readonly int Generation;
        internal readonly long Sequence;

        internal RainWindow(int x, int y, int width, int height, int centerPx, int centerPy, float camX, float camZ, int generation, long sequence)
        {
            X = x; Y = y; Width = width; Height = height; CenterPx = centerPx; CenterPy = centerPy;
            CamX = camX; CamZ = camZ; Generation = generation; Sequence = sequence;
        }

        internal int Count => Width * Height;
    }

    /// <summary>
    /// The Unity-free core of <c>RainUnderRoofRepairs</c> (linked into the server tests): the depth-to-height conversion that the F8 readback
    /// and the rain check both use, the texel mapping, the readback window and the rule that turns the photo's top surface into the flag.
    /// </summary>
    internal static class RainUnderRoofModel
    {
        /// <summary>
        /// A surface counts as cover when it is higher than the camera MINUS 0.1 m, so a surface at head height or a little below it still
        /// counts: that leans towards "under roof" (the harmless error). The depth-to-height conversion ignores the 0.1 m near plane and
        /// reports surfaces up to 0.1 m too high, which leans the same way.
        /// </summary>
        internal const float CoverMargin = -0.1f;

        /// <summary>Half width of the readback window in metres: the highest texel inside it is taken, which leans towards "roof".</summary>
        internal const float WindowRadiusMetres = 0.25f;

        /// <summary>Upper bound of the window radius in texels (7 x 7 = 49 floats).</summary>
        internal const int MaxWindowRadiusTexels = 3;

        /// <summary>A result whose camera position is farther than this from the camera now is not used (respawn, teleport).</summary>
        internal const float StaleMetres = 2f;

        /// <summary>Edge length of the orientation probe texture.</summary>
        internal const int ProbeSize = 4;

        /// <summary>The raw value that means "nothing photographed" is the cleared depth: 0 under reversed-Z, 1 otherwise.</summary>
        internal static bool IsNothing(float raw, bool reversedZ) =>
            reversedZ ? raw <= 1e-4f : raw >= 1f - 1e-4f;

        /// <summary>World height of the photographed surface (an orthographic depth is linear; the 0.1 m near plane is ignored, the margin covers it).</summary>
        internal static float Height(float raw, float camTopY, float farClip, bool reversedZ) =>
            reversedZ ? camTopY - (1f - raw) * farClip : camTopY - raw * farClip;

        /// <summary>The texel of a world point, clamped to the photo (the F8 readback's rule).</summary>
        internal static void TexelClamped(float startX, float startZ, float scaleX, float scaleZ, int dim, float worldX, float worldZ,
            out int px, out int py)
        {
            px = Clamp((int)Math.Floor((worldX - startX) * scaleX * dim), 0, dim - 1);
            py = Clamp((int)Math.Floor((worldZ - startZ) * scaleZ * dim), 0, dim - 1);
        }

        /// <summary>The texel of a world point, false when the point lies outside the photo.</summary>
        internal static bool TryTexel(in RainPhoto photo, float worldX, float worldZ, out int px, out int py)
        {
            float u = (worldX - photo.StartX) * photo.ScaleX, v = (worldZ - photo.StartZ) * photo.ScaleZ;
            px = 0; py = 0;
            if (float.IsNaN(u) || float.IsNaN(v) || u < 0f || u >= 1f || v < 0f || v >= 1f) return false;
            px = Clamp((int)Math.Floor(u * photo.Dim), 0, photo.Dim - 1);
            py = Clamp((int)Math.Floor(v * photo.Dim), 0, photo.Dim - 1);
            return true;
        }

        /// <summary>Window radius in texels: <see cref="WindowRadiusMetres"/>, at least one texel, at most <see cref="MaxWindowRadiusTexels"/>.</summary>
        internal static int RadiusTexels(in RainPhoto photo)
        {
            float metres = photo.TexelMetres;
            if (!(metres > 0f)) return 1;
            return Clamp((int)Math.Ceiling(WindowRadiusMetres / metres), 1, MaxWindowRadiusTexels);
        }

        /// <summary>
        /// The readback rectangle around the camera's texel. In the flipped layout the same logical rows are asked for from the other end of the
        /// texture; the order of values inside the window never matters (only the maximum is used).
        /// </summary>
        internal static bool TryPlanWindow(in RainPhoto photo, RainProbeOrientation orientation, float camX, float camZ,
            int generation, long sequence, out RainWindow window)
        {
            window = default;
            if (!photo.IsUsable || orientation == RainProbeOrientation.Unusable) return false;
            if (!TryTexel(photo, camX, camZ, out int px, out int py)) return false;
            int r = RadiusTexels(photo);
            int x0 = Math.Max(0, px - r), x1 = Math.Min(photo.Dim - 1, px + r);
            int y0 = Math.Max(0, py - r), y1 = Math.Min(photo.Dim - 1, py + r);
            int readY = orientation == RainProbeOrientation.FlippedY ? photo.Dim - 1 - y1 : y0;
            window = new RainWindow(x0, readY, x1 - x0 + 1, y1 - y0 + 1, px, py, camX, camZ, generation, sequence);
            return true;
        }

        /// <summary>The highest photographed surface among the window's samples; NaN when every sample is "nothing photographed" (open sky).</summary>
        internal static float TopHeight(in RainPhoto photo, float[] values, int count)
        {
            float top = float.NaN;
            if (values == null) return top;
            int n = Math.Min(count, values.Length);
            for (int i = 0; i < n; i++)
            {
                float raw = values[i];
                if (float.IsNaN(raw) || float.IsInfinity(raw) || IsNothing(raw, photo.ReversedZ)) continue;
                float h = Height(raw, photo.TopY, photo.FarClip, photo.ReversedZ);
                if (float.IsNaN(h) || float.IsInfinity(h)) continue;
                if (float.IsNaN(top) || h > top) top = h;
            }
            return top;
        }

        /// <summary>Cover counts when a surface was photographed and lies higher than the camera plus <paramref name="margin"/> (negative: leans to roof).</summary>
        internal static bool IsCovered(float topHeight, float cameraY, float margin) =>
            !float.IsNaN(topHeight) && !float.IsNaN(cameraY) && topHeight > cameraY + margin;

        /// <summary>The values a probe texel must hold: texel (x, y) = (y * 4 + x + 1) / 100, so every texel is different and the layout is unambiguous.</summary>
        internal static float ProbeValue(int x, int y) => (y * ProbeSize + x + 1) / 100f;

        /// <summary>Region the probe asks for through the very overload the repair uses: x 1..2 of row <see cref="ProbeRegionY"/> (width 2, height 1).</summary>
        internal const int ProbeRegionX = 1, ProbeRegionWidth = 2, ProbeRegionY = 0, ProbeRegionHeight = 1;

        /// <summary>
        /// Reads the region probe: a request for region row <see cref="ProbeRegionY"/> returned logical row 0 (the region's y is the texture's own
        /// row, <see cref="RainProbeOrientation.AsWritten"/>) or the last logical row (the region's y counts native rows, which are stored flipped,
        /// <see cref="RainProbeOrientation.FlippedY"/>); anything else, a wrong column or a wrong length is Unusable.
        /// </summary>
        internal static RainProbeOrientation ClassifyRegionProbe(float[] data)
        {
            if (data == null || data.Length < ProbeRegionWidth * ProbeRegionHeight) return RainProbeOrientation.Unusable;
            if (Matches(data, 0)) return RainProbeOrientation.AsWritten;
            if (Matches(data, ProbeSize - 1)) return RainProbeOrientation.FlippedY;
            return RainProbeOrientation.Unusable;
        }

        static bool Matches(float[] data, int logicalRow)
        {
            for (int i = 0; i < ProbeRegionWidth; i++)
                if (Math.Abs(data[i] - ProbeValue(ProbeRegionX + i, logicalRow)) > 1e-4f) return false;
            return true;
        }

        /// <summary>
        /// Whether the photo carries cover at all (RainCoverRepairs.PhotoCarriesCover). Without cover an empty photo reads as open sky, which would
        /// give drops indoors, so the check stays off: only the RoofDepthBlack mode, with a finished scan that found cover meshes (or a cover-all
        /// switch), engages it.
        /// </summary>
        internal static bool CarriesCover(bool roofDepthBlack, bool coverAll, bool scanComputed, int coverDraws, out string why)
        {
            why = null;
            if (!roofDepthBlack) { why = "the cover mode draws no cover into the photo"; return false; }
            if (coverAll) return true;
            if (!scanComputed || coverDraws <= 0)
            {
                why = scanComputed ? "the scan found 0 cover meshes" : "the cover scan has not finished";
                return false;
            }
            return true;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>
    /// What the repair knows about the current photo: its parameters, the newest accepted window result and the request bookkeeping. One instance per
    /// plugin; <see cref="BeginPhoto"/> (a render) and <see cref="Reset"/> (raid end, key off) bump the generation, so no result of an older photo
    /// can be used. No Unity types, no allocation after construction.
    /// </summary>
    internal sealed class RainUnderRoofState
    {
        long _nextSequence = 1;
        long _acceptedSequence;
        int _lastRequestPx = -1, _lastRequestPy = -1, _lastRequestGeneration = -1;

        internal int Generation { get; private set; }
        internal bool HasPhoto { get; private set; }
        internal RainPhoto Photo { get; private set; }
        internal RainProbeOrientation Orientation { get; set; }

        internal bool HasResult { get; private set; }
        internal float ResultTop { get; private set; }
        internal float ResultCamX { get; private set; }
        internal float ResultCamZ { get; private set; }

        /// <summary>A request of this generation failed or was rejected: the camera's texel is asked for again on the next frame.</summary>
        internal void ForgetRequest(int generation)
        {
            if (generation == Generation) _lastRequestPx = _lastRequestPy = -1;
        }

        /// <summary>A new photo replaces the previous one; every result and request of the old photo is dropped.</summary>
        internal void BeginPhoto(in RainPhoto photo)
        {
            Generation++;
            Photo = photo;
            HasPhoto = photo.IsUsable;
            ClearResult();
        }

        /// <summary>No photo, no result (raid end, the key switched off): in-flight requests of the old generation are ignored on arrival.</summary>
        internal void Reset()
        {
            Generation++;
            HasPhoto = false;
            ClearResult();
        }

        void ClearResult()
        {
            HasResult = false;
            ResultTop = float.NaN;
            _acceptedSequence = 0;
            _lastRequestPx = _lastRequestPy = _lastRequestGeneration = -1;
        }

        /// <summary>True (and the rectangle to ask for) when the camera stands on a texel that has not been asked for in this generation.</summary>
        internal bool TryBeginRequest(float camX, float camZ, out RainWindow window)
        {
            window = default;
            if (!HasPhoto || Orientation == RainProbeOrientation.Unusable) return false;
            if (!RainUnderRoofModel.TryTexel(Photo, camX, camZ, out int px, out int py)) return false;
            if (px == _lastRequestPx && py == _lastRequestPy && Generation == _lastRequestGeneration) return false;
            if (!RainUnderRoofModel.TryPlanWindow(Photo, Orientation, camX, camZ, Generation, _nextSequence, out window)) return false;
            _nextSequence++;
            _lastRequestPx = px; _lastRequestPy = py; _lastRequestGeneration = Generation;
            return true;
        }

        /// <summary>Takes a finished readback. False when it belongs to an older photo or arrives behind a newer result.</summary>
        internal bool Submit(in RainWindow window, float[] values, int count)
        {
            if (!HasPhoto || window.Generation != Generation || window.Sequence <= _acceptedSequence) return false;
            if (values == null || count < window.Count || values.Length < window.Count) return false;
            ResultTop = RainUnderRoofModel.TopHeight(Photo, values, window.Count);
            ResultCamX = window.CamX; ResultCamZ = window.CamZ;
            _acceptedSequence = window.Sequence;
            HasResult = true;
            return true;
        }

        /// <summary>
        /// True when the flag can be answered; <paramref name="underRain"/> is then "no cover above the camera". When false the caller leaves
        /// vanilla's value (false, "under roof") alone.
        /// </summary>
        internal bool TryDecide(float camX, float camY, float camZ, out bool underRain, out float top, out RainNotReady why)
        {
            underRain = false; top = float.NaN; why = RainNotReady.None;
            if (!HasPhoto) { why = RainNotReady.NoPhoto; return false; }
            if (Orientation == RainProbeOrientation.Unusable) { why = RainNotReady.NoOrientation; return false; }
            if (!RainUnderRoofModel.TryTexel(Photo, camX, camZ, out _, out _)) { why = RainNotReady.OutsidePhoto; return false; }
            if (!HasResult) { why = RainNotReady.NoResult; return false; }
            float dx = camX - ResultCamX, dz = camZ - ResultCamZ;
            if (dx * dx + dz * dz > RainUnderRoofModel.StaleMetres * RainUnderRoofModel.StaleMetres)
            {
                why = RainNotReady.StaleResult;
                return false;
            }
            top = ResultTop;
            underRain = !RainUnderRoofModel.IsCovered(top, camY, RainUnderRoofModel.CoverMargin);
            return true;
        }
    }
}
