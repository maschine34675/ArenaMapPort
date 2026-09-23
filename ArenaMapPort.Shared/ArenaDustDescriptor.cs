namespace ArenaMapPort.Shared
{
    /// <summary>Original dust generator parameters and padded bounds; generated mesh data stays imported.</summary>
    public sealed class ArenaDustDescriptor
    {
        public string Scene { get; }
        public string Path { get; }
        public float TimeSpeed { get; }
        public float Size { get; }
        public int QuadTileSetSide { get; }
        public float UvTilePadding { get; }
        public float CenterX { get; }
        public float CenterY { get; }
        public float CenterZ { get; }
        public float ExtentX { get; }
        public float ExtentY { get; }
        public float ExtentZ { get; }

        public ArenaDustDescriptor(string Scene, string Path, float TimeSpeed, float Size,
            int QuadTileSetSide, float UvTilePadding, float CenterX, float CenterY, float CenterZ,
            float ExtentX, float ExtentY, float ExtentZ)
        {
            this.Scene = Scene;
            this.Path = Path;
            this.TimeSpeed = TimeSpeed;
            this.Size = Size;
            this.QuadTileSetSide = QuadTileSetSide;
            this.UvTilePadding = UvTilePadding;
            this.CenterX = CenterX;
            this.CenterY = CenterY;
            this.CenterZ = CenterZ;
            this.ExtentX = ExtentX;
            this.ExtentY = ExtentY;
            this.ExtentZ = ExtentZ;
        }
    }
}
