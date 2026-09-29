using System;
using System.Collections.Generic;
using FloorPoint = ArenaMapPort.Client.IndoorSurfaceGeometry.Point;

namespace ArenaMapPort.Client
{
    /// <summary>Read-only triangle support for small floor details. Bounds alone cannot exclude holes.</summary>
    internal sealed class FloorDetailSupport
    {
        internal readonly struct Face
        {
            internal readonly FloorPoint A, B, C;
            internal readonly double Area;
            internal Face(FloorPoint a, FloorPoint b, FloorPoint c, double area) { A = a; B = b; C = c; Area = area; }
        }

        const double CellSize = 4;
        readonly Dictionary<(int x, int z), List<Face>> _cells = new Dictionary<(int, int), List<Face>>();
        readonly List<Face> _largeFaces = new List<Face>();
        internal int Count { get; private set; }

        // Accept either winding (including mirrored static meshes), but never vertical geometry.
        internal static bool TryFace(FloorPoint a, FloorPoint b, FloorPoint c, out Face face)
        {
            face = default;
            if (!a.Finite || !b.Finite || !c.Finite) return false;
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-8 || Math.Abs(ny) < 0.85 * length) return false;
            face = new Face(a, b, c, Math.Abs(ny) * 0.5);
            return true;
        }

        internal void Add(Face face)
        {
            Count++;
            int x0 = Cell(Math.Min(face.A.X, Math.Min(face.B.X, face.C.X)) - 1e-5);
            int x1 = Cell(Math.Max(face.A.X, Math.Max(face.B.X, face.C.X)) + 1e-5);
            int z0 = Cell(Math.Min(face.A.Z, Math.Min(face.B.Z, face.C.Z)) - 1e-5);
            int z1 = Cell(Math.Max(face.A.Z, Math.Max(face.B.Z, face.C.Z)) + 1e-5);
            if (((long)x1 - x0 + 1) * ((long)z1 - z0 + 1) > 4096)
            { _largeFaces.Add(face); return; }
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list)) _cells.Add((x, z), list = new List<Face>());
                    list.Add(face);
                }
        }

        // Clamping avoids integer overflow for malformed extreme coordinates; ordinary map geometry
        // is many orders of magnitude smaller. Large faces use the bounded fallback above.
        static int Cell(double value) => (int)Math.Max(-1000000, Math.Min(1000000, Math.Floor(value / CellSize)));

        internal bool Contains(FloorPoint p)
        {
            if (!p.Finite) return false;
            return (_cells.TryGetValue((Cell(p.X), Cell(p.Z)), out var local) && Contains(local, p))
                || Contains(_largeFaces, p);
        }

        static bool Contains(List<Face> faces, FloorPoint p)
        {
            foreach (var f in faces)
            {
                if (p.X < Math.Min(f.A.X, Math.Min(f.B.X, f.C.X)) - 1e-5
                    || p.X > Math.Max(f.A.X, Math.Max(f.B.X, f.C.X)) + 1e-5
                    || p.Z < Math.Min(f.A.Z, Math.Min(f.B.Z, f.C.Z)) - 1e-5
                    || p.Z > Math.Max(f.A.Z, Math.Max(f.B.Z, f.C.Z)) + 1e-5) continue;
                double det = (f.B.Z - f.C.Z) * (f.A.X - f.C.X) + (f.C.X - f.B.X) * (f.A.Z - f.C.Z);
                double wa = ((f.B.Z - f.C.Z) * (p.X - f.C.X) + (f.C.X - f.B.X) * (p.Z - f.C.Z)) / det;
                double wb = ((f.C.Z - f.A.Z) * (p.X - f.C.X) + (f.A.X - f.C.X) * (p.Z - f.C.Z)) / det;
                double wc = 1 - wa - wb;
                if (wa < -1e-5 || wb < -1e-5 || wc < -1e-5) continue;
                double height = wa * f.A.Y + wb * f.B.Y + wc * f.C.Y;
                if (p.Y >= height - 0.05 && p.Y <= height + 0.2) return true;
            }
            return false;
        }

        internal bool Supports(IReadOnlyList<Face> details)
        {
            double total = 0, supported = 0;
            foreach (var f in details)
            {
                total += f.Area;
                var center = new FloorPoint((f.A.X + f.B.X + f.C.X) / 3,
                    (f.A.Y + f.B.Y + f.C.Y) / 3, (f.A.Z + f.B.Z + f.C.Z) / 3);
                // Inset corner probes tolerate seams and the intentionally ragged tile outlines.
                if (Contains(center) && Contains(Midpoint(f.A, center))
                    && Contains(Midpoint(f.B, center)) && Contains(Midpoint(f.C, center))) supported += f.Area;
            }
            return total > 1e-6 && supported >= total * 0.9;
        }

        static FloorPoint Midpoint(FloorPoint a, FloorPoint b) => new FloorPoint((a.X + b.X) * .5, (a.Y + b.Y) * .5, (a.Z + b.Z) * .5);
    }
}
