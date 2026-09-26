using System;
using System.Collections.Generic;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// A closed planar region in XY described by an unordered bag of boundary edges
    /// (outer loop + any number of hole loops). Orientation and edge order do not matter:
    /// inside/outside is decided with the even-odd rule, so openings are handled automatically.
    /// </summary>
    public sealed class RegionXY
    {
        private readonly List<double> _e = new List<double>(); // x1,y1,x2,y2 per edge

        public double MinX { get; private set; } = double.MaxValue;
        public double MinY { get; private set; } = double.MaxValue;
        public double MaxX { get; private set; } = double.MinValue;
        public double MaxY { get; private set; } = double.MinValue;

        public int EdgeCount => _e.Count / 4;

        public bool IsEmpty => EdgeCount < 3 || MaxX - MinX < 1.0e-9 || MaxY - MinY < 1.0e-9;

        public void AddEdge(double x1, double y1, double x2, double y2)
        {
            if (Math.Abs(x1 - x2) < 1.0e-12 && Math.Abs(y1 - y2) < 1.0e-12) return;
            _e.Add(x1); _e.Add(y1); _e.Add(x2); _e.Add(y2);
            Grow(x1, y1);
            Grow(x2, y2);
        }

        /// <summary>Adds a polyline (open or closed); consecutive points become edges.</summary>
        public void AddPolyline(IList<double[]> pts, bool close)
        {
            if (pts == null || pts.Count < 2) return;
            for (var i = 0; i + 1 < pts.Count; i++)
                AddEdge(pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1]);
            if (close)
                AddEdge(pts[pts.Count - 1][0], pts[pts.Count - 1][1], pts[0][0], pts[0][1]);
        }

        private void Grow(double x, double y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }

        internal List<double> RawEdges => _e;
    }

    /// <summary>
    /// 2D clipping helpers (XY only, Revit internal feet). No Revit API types so the maths is portable.
    /// </summary>
    public static class PolygonClipper
    {
        /// <summary>
        /// Clips the infinite line P(t) = (px,py) + t*(dx,dy) (d must be unit length) against the region
        /// using the even-odd rule. Returns sorted, disjoint [t0,t1] intervals that lie inside the region.
        /// Vertices exactly on the line are handled with a half-open crossing rule so they are never counted twice.
        /// </summary>
        public static List<double[]> ClipLine(RegionXY region, double px, double py, double dx, double dy)
        {
            var result = new List<double[]>();
            if (region == null || region.IsEmpty) return result;

            // Normal to the line; signed distance s = (Q - P) . n
            var nx = -dy;
            var ny = dx;
            var e = region.RawEdges;
            var ts = new List<double>();

            for (var i = 0; i < e.Count; i += 4)
            {
                var ax = e[i] - px; var ay = e[i + 1] - py;
                var bx = e[i + 2] - px; var by = e[i + 3] - py;
                var sa = ax * nx + ay * ny;
                var sb = bx * nx + by * ny;
                var aPos = sa > 0;
                var bPos = sb > 0;
                if (aPos == bPos) continue; // no crossing (half-open rule: s == 0 counts as "not positive")

                var ta = ax * dx + ay * dy;
                var tb = bx * dx + by * dy;
                var denom = sa - sb;
                if (Math.Abs(denom) < 1.0e-300) continue;
                ts.Add(ta + (tb - ta) * (sa / denom));
            }

            if (ts.Count < 2) return result;
            ts.Sort();

            for (var i = 0; i + 1 < ts.Count; i += 2)
            {
                var t0 = ts[i];
                var t1 = ts[i + 1];
                if (t1 - t0 > FpfOptions.GeometryTolerance)
                    result.Add(new[] { t0, t1 });
            }

            return result;
        }

        /// <summary>
        /// Intersects the inside intervals with a dash pattern. <paramref name="dashes"/> alternates
        /// dash, gap, dash, gap ... (absolute lengths). Dash phase starts at t = 0.
        /// A zero-length dash (dot) is emitted as a short dash of <paramref name="dotLength"/> centred on the dot.
        /// Empty or single-entry dash lists mean a continuous line.
        /// </summary>
        public static void ApplyDashes(List<double[]> inside, IList<double> dashes, double dotLength,
            List<double[]> output)
        {
            if (inside == null || inside.Count == 0) return;

            double period = 0;
            if (dashes != null)
                foreach (var d in dashes) period += Math.Abs(d);

            if (dashes == null || dashes.Count < 2 || period < 1.0e-9)
            {
                output.AddRange(inside);
                return;
            }

            foreach (var iv in inside)
            {
                var t0 = iv[0];
                var t1 = iv[1];
                var m = Math.Floor(t0 / period);
                // Guard against pathological counts
                var maxPeriods = (t1 - t0) / period + 2;
                if (maxPeriods > 1.0e7) { output.Add(iv); continue; }

                for (var start = m * period; start < t1; start += period)
                {
                    var pos = start;
                    for (var k = 0; k < dashes.Count; k++)
                    {
                        var len = Math.Abs(dashes[k]);
                        if ((k & 1) == 0)
                        {
                            double a, b;
                            if (len < 1.0e-9)
                            {
                                a = pos - dotLength * 0.5;
                                b = pos + dotLength * 0.5;
                            }
                            else
                            {
                                a = pos;
                                b = pos + len;
                            }

                            var ca = Math.Max(a, t0);
                            var cb = Math.Min(b, t1);
                            if (cb - ca > FpfOptions.GeometryTolerance)
                                output.Add(new[] { ca, cb });
                        }
                        pos += len;
                    }
                }
            }
        }
    }
}
