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
        /// <summary>Even-odd point-in test for a region described by boundary edges.</summary>
        public static bool PointInsideRegion(double x, double y, RegionXY region)
        {
            if (region == null || region.IsEmpty) return false;
            var inside = false;
            var e = region.RawEdges;
            for (var i = 0; i < e.Count; i += 4)
            {
                var x1 = e[i];
                var y1 = e[i + 1];
                var x2 = e[i + 2];
                var y2 = e[i + 3];
                if (Math.Abs(y2 - y1) < 1.0e-12) continue;
                var crosses = (y1 > y) != (y2 > y);
                if (!crosses) continue;
                var xHit = x1 + (y - y1) * (x2 - x1) / (y2 - y1);
                if (xHit > x - 1.0e-9) inside = !inside;
            }
            return inside;
        }

        public static bool PointInsideAnyRegion(double x, double y, IList<RegionXY> regions)
        {
            if (regions == null) return false;
            for (var i = 0; i < regions.Count; i++)
                if (PointInsideRegion(x, y, regions[i]))
                    return true;
            return false;
        }
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
                // Half-open rule: a point exactly on the line is not "positive", so a vertex is
                // counted once. A tiny epsilon stops near-misses on shape-edited meshes flipping parity.
                var aPos = sa > 1.0e-9;
                var bPos = sb > 1.0e-9;
                if (aPos == bPos) continue;

                var ta = ax * dx + ay * dy;
                var tb = bx * dx + by * dy;
                var denom = sa - sb;
                if (Math.Abs(denom) < 1.0e-300) continue;
                ts.Add(ta + (tb - ta) * (sa / denom));
            }

            if (ts.Count < 2) return result;
            ts.Sort();
            var w = 0;
            for (var r = 0; r < ts.Count; r++)
            {
                if (w > 0 && Math.Abs(ts[r] - ts[w - 1]) < 1.0e-8) continue;
                ts[w++] = ts[r];
            }
            if (w < ts.Count) ts.RemoveRange(w, ts.Count - w);

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

        /// <summary>True when (x, y) is inside the region by the same even-odd rule as <see cref="ClipLine"/>.</summary>
        public static bool ContainsPoint(RegionXY region, double x, double y)
        {
            var ivs = ClipLine(region, x, y, 1, 0);
            for (var i = 0; i < ivs.Count; i++)
                if (ivs[i][0] <= 1.0e-8 && ivs[i][1] >= -1.0e-8) return true;
            return false;
        }

        /// <summary>
        /// Merges overlapping or nearly-touching intervals. <paramref name="bridge"/> (same units as the
        /// interval parameter) joins cracks left between triangulated faces without closing real openings.
        /// </summary>
        public static List<double[]> UnionIntervals(List<double[]> intervals, double bridge)
        {
            var merged = new List<double[]>();
            if (intervals == null || intervals.Count == 0) return merged;
            intervals.Sort((a, b) => a[0].CompareTo(b[0]));
            var a0 = intervals[0][0];
            var a1 = intervals[0][1];
            for (var i = 1; i < intervals.Count; i++)
            {
                var b0 = intervals[i][0];
                var b1 = intervals[i][1];
                if (b0 <= a1 + bridge)
                {
                    if (b1 > a1) a1 = b1;
                }
                else
                {
                    merged.Add(new[] { a0, a1 });
                    a0 = b0;
                    a1 = b1;
                }
            }
            merged.Add(new[] { a0, a1 });
            return merged;
        }

        /// <summary>Returns <paramref name="source"/> with <paramref name="cuts"/> removed. Both lists are treated as sorted unions.</summary>
        public static List<double[]> SubtractIntervals(List<double[]> source, List<double[]> cuts)
        {
            if (source == null || source.Count == 0) return new List<double[]>();
            if (cuts == null || cuts.Count == 0) return source;
            var cutU = UnionIntervals(cuts, 0);
            var result = new List<double[]>();
            foreach (var s in source)
            {
                var start = s[0];
                var end = s[1];
                for (var i = 0; i < cutU.Count; i++)
                {
                    var c0 = cutU[i][0];
                    var c1 = cutU[i][1];
                    if (c1 <= start) continue;
                    if (c0 >= end) break;
                    if (c0 > start + FpfOptions.GeometryTolerance)
                        result.Add(new[] { start, Math.Min(c0, end) });
                    if (c1 > start) start = c1;
                    if (start >= end - FpfOptions.GeometryTolerance) break;
                }
                if (end - start > FpfOptions.GeometryTolerance)
                    result.Add(new[] { start, end });
            }
            return result;
        }

        /// <summary>
        /// Overlap of segment CD with AB when the two are almost collinear and CD lies within
        /// <paramref name="tol"/> of AB. <paramref name="lo"/> and <paramref name="hi"/> are distances along AB from A.
        /// </summary>
        public static bool CollinearOverlap(double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy, double tol,
            out double lo, out double hi)
        {
            lo = hi = 0;
            var abx = bx - ax;
            var aby = by - ay;
            var len = Math.Sqrt(abx * abx + aby * aby);
            if (len < 1.0e-9) return false;
            var cdx = dx - cx;
            var cdy = dy - cy;
            var clen = Math.Sqrt(cdx * cdx + cdy * cdy);
            if (clen < 1.0e-9) return false;
            // About 2 degrees: a brick joint that merely meets the edge is not removed.
            if (Math.Abs(abx * cdy - aby * cdx) > 0.035 * len * clen) return false;
            var distC = Math.Abs((cx - ax) * aby - (cy - ay) * abx) / len;
            var distD = Math.Abs((dx - ax) * aby - (dy - ay) * abx) / len;
            if (distC > tol || distD > tol) return false;
            var tc = ((cx - ax) * abx + (cy - ay) * aby) / len;
            var td = ((dx - ax) * abx + (dy - ay) * aby) / len;
            lo = Math.Max(0, Math.Min(tc, td));
            hi = Math.Min(len, Math.Max(tc, td));
            return hi - lo > FpfOptions.GeometryTolerance;
        }
    }
}
