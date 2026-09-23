using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// 2D polygon helpers in the XY plane (Z ignored for clipping; restored by caller).
    /// Sutherland–Hodgman polygon clip + segment-vs-convex/non-convex polygon clip.
    /// </summary>
    public static class PolygonClipper
    {
        private const double Tol = 1.0e-9;

        public static List<XYZ> ProjectToXy(IList<XYZ> loop)
        {
            var pts = new List<XYZ>(loop.Count);
            foreach (var p in loop)
                pts.Add(new XYZ(p.X, p.Y, 0));
            return pts;
        }

        public static bool IsClosed(IList<XYZ> poly)
        {
            if (poly == null || poly.Count < 3) return false;
            var a = poly[0];
            var b = poly[poly.Count - 1];
            return a.DistanceTo(b) < 1.0e-6;
        }

        public static List<XYZ> EnsureClosed(IList<XYZ> poly)
        {
            var list = new List<XYZ>(poly);
            if (list.Count >= 3 && !IsClosed(list))
                list.Add(list[0]);
            return list;
        }

        public static void GetBounds(IList<XYZ> poly, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (var p in poly)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        /// <summary>
        /// Clip an infinite family of parallel hatch lines against a closed XY polygon.
        /// Returns pairs of XYZ endpoints at z=0; caller lifts Z.
        /// </summary>
        public static List<(XYZ A, XYZ B)> ClipParallelLines(
            IList<XYZ> closedXyPolygon,
            double spacing,
            double angleRadians,
            double originX,
            double originY)
        {
            var results = new List<(XYZ A, XYZ B)>();
            if (closedXyPolygon == null || closedXyPolygon.Count < 3 || spacing <= Tol)
                return results;

            var poly = EnsureClosed(closedXyPolygon);
            GetBounds(poly, out var minX, out var minY, out var maxX, out var maxY);

            var cos = Math.Cos(angleRadians);
            var sin = Math.Sin(angleRadians);
            // Direction of hatch lines
            var dir = new XYZ(cos, sin, 0);
            // Perpendicular offset direction
            var perp = new XYZ(-sin, cos, 0);

            // Project bbox corners onto perp to find offset range
            var corners = new[]
            {
                new XYZ(minX, minY, 0),
                new XYZ(maxX, minY, 0),
                new XYZ(maxX, maxY, 0),
                new XYZ(minX, maxY, 0)
            };

            double minT = double.MaxValue, maxT = double.MinValue;
            foreach (var c in corners)
            {
                var t = (c.X - originX) * perp.X + (c.Y - originY) * perp.Y;
                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
            }

            // Extend slightly so edges are covered
            minT -= spacing;
            maxT += spacing;

            var startT = Math.Floor(minT / spacing) * spacing;
            for (var t = startT; t <= maxT + Tol; t += spacing)
            {
                var onLine = new XYZ(originX + perp.X * t, originY + perp.Y * t, 0);
                // Long segment through bbox along dir
                var diag = Math.Max(maxX - minX, maxY - minY) * 2.0 + spacing * 4.0;
                if (diag < spacing) diag = spacing * 10.0;
                var a = onLine - dir * diag;
                var b = onLine + dir * diag;

                foreach (var seg in ClipSegmentToPolygon(a, b, poly))
                    results.Add(seg);
            }

            return results;
        }

        public static List<(XYZ A, XYZ B)> ClipSegmentToPolygon(XYZ a, XYZ b, IList<XYZ> closedXyPolygon)
        {
            var hits = new List<(double U, XYZ P)>();
            var poly = EnsureClosed(closedXyPolygon);

            if (PointInPolygon(a, poly))
                hits.Add((0.0, a));
            if (PointInPolygon(b, poly))
                hits.Add((1.0, b));

            for (var i = 0; i < poly.Count - 1; i++)
            {
                if (TrySegmentIntersection(a, b, poly[i], poly[i + 1], out var u, out var p))
                {
                    if (u >= -Tol && u <= 1.0 + Tol)
                        hits.Add((Clamp01(u), p));
                }
            }

            hits.Sort((x, y) => x.U.CompareTo(y.U));

            // Deduplicate close parameters
            var unique = new List<(double U, XYZ P)>();
            foreach (var h in hits)
            {
                if (unique.Count == 0 || Math.Abs(unique[unique.Count - 1].U - h.U) > 1.0e-8)
                    unique.Add(h);
            }

            var segs = new List<(XYZ A, XYZ B)>();
            for (var i = 0; i + 1 < unique.Count; i++)
            {
                var midU = 0.5 * (unique[i].U + unique[i + 1].U);
                var mid = a + (b - a) * midU;
                if (PointInPolygon(mid, poly))
                    segs.Add((unique[i].P, unique[i + 1].P));
            }

            return segs;
        }

        private static double Clamp01(double u)
        {
            if (u < 0) return 0;
            if (u > 1) return 1;
            return u;
        }

        public static bool PointInPolygon(XYZ p, IList<XYZ> closedXyPolygon)
        {
            // Ray casting
            var inside = false;
            var poly = EnsureClosed(closedXyPolygon);
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var pi = poly[i];
                var pj = poly[j];
                var intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / ((pj.Y - pi.Y) + Tol) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static bool TrySegmentIntersection(XYZ a, XYZ b, XYZ c, XYZ d, out double u, out XYZ p)
        {
            u = 0;
            p = null;
            var r = b - a;
            var s = d - c;
            var rxs = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(rxs) < Tol) return false; // parallel
            var q = c - a;
            u = (q.X * s.Y - q.Y * s.X) / rxs;
            var t = (q.X * r.Y - q.Y * r.X) / rxs;
            if (u < -Tol || u > 1 + Tol || t < -Tol || t > 1 + Tol) return false;
            p = a + r * u;
            return true;
        }
    }
}
