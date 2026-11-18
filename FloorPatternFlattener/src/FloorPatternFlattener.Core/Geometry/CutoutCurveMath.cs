using System;
using System.Collections.Generic;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>Planar curve sampling and loop chaining for cut-out sketches (internal feet, XY).</summary>
    public static class CutoutCurveMath
    {
        public const double DefaultChordMm = 5.0;
        public const int MinFullCircleSegments = 32;

        public const double MillimetresPerFoot = 304.8;

        public static double ChordToleranceFeet => Math.Max(
            DefaultChordMm / MillimetresPerFoot,
            FpfOptions.GeometryTolerance);

        public static IReadOnlyList<double[]> TessellateCircle(double cx, double cy, double radius, int segments)
        {
            var n = Math.Max(MinFullCircleSegments, segments);
            var pts = new List<double[]>(n);
            for (var i = 0; i < n; i++)
            {
                var t = 2.0 * Math.PI * i / n;
                pts.Add(new[] { cx + radius * Math.Cos(t), cy + radius * Math.Sin(t) });
            }
            return pts;
        }

        /// <summary>Arc from angle <paramref name="a0"/> to <paramref name="a1"/> (radians), inclusive stepping by chord.</summary>
        public static IReadOnlyList<double[]> TessellateArc(double cx, double cy, double radius, double a0, double a1,
            double chordTolFeet)
        {
            var pts = new List<double[]>();
            if (radius < 1.0e-12) return pts;
            chordTolFeet = Math.Max(chordTolFeet, 1.0e-9);
            var span = a1 - a0;
            if (Math.Abs(span) < 1.0e-12)
            {
                pts.Add(new[] { cx + radius * Math.Cos(a0), cy + radius * Math.Sin(a0) });
                return pts;
            }
            var fullCircle = Math.Abs(Math.Abs(span) - 2.0 * Math.PI) < 1.0e-6;
            if (fullCircle)
                return TessellateCircle(cx, cy, radius, MinFullCircleSegments);

            var arcLen = Math.Abs(span) * radius;
            var segs = Math.Max(2, (int)Math.Ceiling(arcLen / chordTolFeet));
            segs = Math.Max(segs, (int)Math.Ceiling(Math.Abs(span) / (2.0 * Math.PI) * MinFullCircleSegments));
            for (var i = 0; i <= segs; i++)
            {
                var t = a0 + span * i / segs;
                pts.Add(new[] { cx + radius * Math.Cos(t), cy + radius * Math.Sin(t) });
            }
            return pts;
        }

        /// <summary>Polyline edges chained by endpoint welding; returns one point list per closed loop found.</summary>
        public static List<List<double[]>> ChainPolylinesToLoops(IList<IList<double[]>> polylines, double weldFeet)
        {
            var loops = new List<List<double[]>>();
            if (polylines == null || polylines.Count == 0) return loops;
            var used = new bool[polylines.Count];
            weldFeet = Math.Max(weldFeet, 1.0e-9);

            for (var seed = 0; seed < polylines.Count; seed++)
            {
                if (used[seed]) continue;
                var seedLine = polylines[seed];
                if (seedLine == null || seedLine.Count < 2) { used[seed] = true; continue; }

                var loop = new List<double[]>();
                AppendWelded(loop, seedLine[0], weldFeet);
                for (var i = 1; i < seedLine.Count; i++)
                    AppendWelded(loop, seedLine[i], weldFeet);
                used[seed] = true;

                var extended = true;
                while (extended)
                {
                    extended = false;
                    var head = loop[0];
                    var tail = loop[loop.Count - 1];
                    for (var i = 0; i < polylines.Count; i++)
                    {
                        if (used[i]) continue;
                        var pl = polylines[i];
                        if (pl == null || pl.Count < 2) { used[i] = true; continue; }
                        var p0 = pl[0];
                        var p1 = pl[pl.Count - 1];
                        if (Near(p1, tail, weldFeet))
                        {
                            for (var k = pl.Count - 2; k >= 0; k--) AppendWelded(loop, pl[k], weldFeet);
                            used[i] = true;
                            extended = true;
                            break;
                        }
                        if (Near(p0, tail, weldFeet))
                        {
                            for (var k = 1; k < pl.Count; k++) AppendWelded(loop, pl[k], weldFeet);
                            used[i] = true;
                            extended = true;
                            break;
                        }
                        if (Near(p0, head, weldFeet))
                        {
                            var prep = new List<double[]>();
                            for (var k = 1; k < pl.Count; k++) prep.Add(pl[k]);
                            prep.Reverse();
                            loop.InsertRange(0, prep);
                            used[i] = true;
                            extended = true;
                            break;
                        }
                        if (Near(p1, head, weldFeet))
                        {
                            var prep = new List<double[]>();
                            for (var k = 0; k < pl.Count - 1; k++) prep.Add(pl[k]);
                            prep.Reverse();
                            loop.InsertRange(0, prep);
                            used[i] = true;
                            extended = true;
                            break;
                        }
                    }
                }

                CloseLoopDuplicate(loop, weldFeet);
                if (loop.Count >= 3) loops.Add(loop);
            }

            return loops;
        }

        public static RegionXY LoopToRegion(IList<double[]> loop)
        {
            var region = new RegionXY();
            if (loop == null || loop.Count < 3) return region;
            region.AddPolyline(loop, close: true);
            return region;
        }

        public static bool IsDegenerateLoop(IList<double[]> loop, out double areaFeet2)
        {
            areaFeet2 = AbsoluteArea(loop);
            if (loop == null || loop.Count < 3) return true;
            var minArea = ChordToleranceFeet * ChordToleranceFeet;
            return areaFeet2 < minArea;
        }

        public static double AbsoluteArea(IList<double[]> loop)
        {
            if (loop == null || loop.Count < 3) return 0;
            double sum = 0;
            var n = loop.Count;
            for (var i = 0; i < n; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % n];
                if (a == null || b == null || a.Length < 2 || b.Length < 2) continue;
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(sum) * 0.5;
        }

        private static void CloseLoopDuplicate(List<double[]> loop, double weldFeet)
        {
            if (loop == null || loop.Count < 3) return;
            if (Near(loop[0], loop[loop.Count - 1], weldFeet))
                loop.RemoveAt(loop.Count - 1);
        }

        private static void AppendWelded(List<double[]> loop, double[] pt, double _)
        {
            if (pt == null || pt.Length < 2) return;
            // Use a tiny dedupe tolerance so chord-sized weld does not drop arc endpoints needed for chaining.
            const double dedupeTol = 1.0e-12;
            if (loop.Count == 0 || !Near(loop[loop.Count - 1], pt, dedupeTol))
                loop.Add(new[] { pt[0], pt[1] });
        }

        public static bool PointsNear(double[] a, double[] b, double tol) => Near(a, b, tol);

        private static bool Near(double[] a, double[] b, double tol)
        {
            if (a == null || b == null || a.Length < 2 || b.Length < 2) return false;
            var dx = a[0] - b[0];
            var dy = a[1] - b[1];
            return dx * dx + dy * dy <= tol * tol;
        }
    }
}