using System;
using System.Collections.Generic;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>Clips finite XY segments by removing intervals that lie inside cut-out regions.</summary>
    public static class SegmentPolygonClipper
    {
        public struct ClipStats
        {
            public int Input;
            public int Removed;
            public int Clipped;
            public int Kept;
        }

        /// <summary>Returns sub-segments as x1,y1,x2,y2 outside all regions.</summary>
        public static List<double[]> ClipSegmentRemoveInteriors(
            double x1, double y1, double x2, double y2,
            IList<RegionXY> cutouts, double minLen, ref ClipStats stats)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < minLen) return new List<double[]>();

            var anyBbox = false;
            for (var r = 0; r < cutouts.Count; r++)
            {
                var region = cutouts[r];
                if (region == null || region.IsEmpty) continue;
                if (SegmentIntersectsAxisAlignedBox(x1, y1, x2, y2, region.MinX, region.MinY, region.MaxX, region.MaxY))
                {
                    anyBbox = true;
                    break;
                }
            }
            if (!anyBbox)
                return new List<double[]> { new[] { x1, y1, x2, y2 } };

            var breaks = new List<double> { 0.0, 1.0 };
            const double tol = 1.0e-9;
            foreach (var region in cutouts)
            {
                if (region == null || region.IsEmpty) continue;
                if (!SegmentIntersectsAxisAlignedBox(x1, y1, x2, y2, region.MinX, region.MinY, region.MaxX, region.MaxY))
                    continue;
                var e = region.RawEdges;
                for (var i = 0; i < e.Count; i += 4)
                {
                    if (TrySegmentEdgeIntersection(x1, y1, x2, y2, e[i], e[i + 1], e[i + 2], e[i + 3], tol, out var t))
                        breaks.Add(t);
                }
            }

            breaks.Sort();
            var merged = new List<double> { breaks[0] };
            for (var i = 1; i < breaks.Count; i++)
            {
                if (breaks[i] - merged[merged.Count - 1] > tol)
                    merged.Add(breaks[i]);
                else
                    merged[merged.Count - 1] = breaks[i];
            }

            var pieces = new List<double[]>();
            for (var i = 0; i + 1 < merged.Count; i++)
            {
                var t0 = merged[i];
                var t1 = merged[i + 1];
                if ((t1 - t0) * len < minLen) continue;
                var mx = x1 + dx * (t0 + t1) * 0.5;
                var my = y1 + dy * (t0 + t1) * 0.5;
                if (PolygonClipper.PointInsideAnyRegion(mx, my, cutouts)) continue;
                pieces.Add(new[]
                {
                    x1 + dx * t0,
                    y1 + dy * t0,
                    x1 + dx * t1,
                    y1 + dy * t1
                });
            }

            return pieces;
        }

        private static bool TrySegmentEdgeIntersection(double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy, double tol, out double tOnAb)
        {
            tOnAb = 0;
            var rpx = bx - ax;
            var rpy = by - ay;
            var spx = dx - cx;
            var spy = dy - cy;
            var denom = rpx * spy - rpy * spx;
            if (Math.Abs(denom) < 1.0e-15) return false;
            var qmx = cx - ax;
            var qmy = cy - ay;
            tOnAb = (qmx * spy - qmy * spx) / denom;
            var uOnCd = (qmx * rpy - qmy * rpx) / denom;
            if (tOnAb < -tol || tOnAb > 1.0 + tol || uOnCd < -tol || uOnCd > 1.0 + tol)
                return false;
            if (tOnAb < tol || tOnAb > 1.0 - tol)
                return false;
            return true;
        }

        /// <summary>True when the finite segment can meet the axis-aligned box (not endpoint-in-box).</summary>
        internal static bool SegmentIntersectsAxisAlignedBox(double x1, double y1, double x2, double y2,
            double minX, double minY, double maxX, double maxY)
        {
            if (maxX < minX || maxY < minY) return false;
            var segMinX = Math.Min(x1, x2);
            var segMaxX = Math.Max(x1, x2);
            var segMinY = Math.Min(y1, y2);
            var segMaxY = Math.Max(y1, y2);
            if (segMaxX < minX || segMinX > maxX || segMaxY < minY || segMinY > maxY)
                return false;

            if (PointInClosedBox(x1, y1, minX, minY, maxX, maxY) || PointInClosedBox(x2, y2, minX, minY, maxX, maxY))
                return true;

            const double tol = 1.0e-9;
            if (Math.Abs(x2 - x1) < tol)
            {
                return segMinY <= maxY && segMaxY >= minY && x1 >= minX - tol && x1 <= maxX + tol;
            }
            if (Math.Abs(y2 - y1) < tol)
            {
                return segMinX <= maxX && segMaxX >= minX && y1 >= minY - tol && y1 <= maxY + tol;
            }

            double Cross(double px, double py) => (x2 - x1) * (py - y1) - (y2 - y1) * (px - x1);
            var c00 = Cross(minX, minY);
            var c10 = Cross(maxX, minY);
            var c11 = Cross(maxX, maxY);
            var c01 = Cross(minX, maxY);
            if ((c00 > tol && c10 > tol && c11 > tol && c01 > tol) ||
                (c00 < -tol && c10 < -tol && c11 < -tol && c01 < -tol))
                return false;

            return LinesIntersect(x1, y1, x2, y2, minX, minY, maxX, minY) ||
                   LinesIntersect(x1, y1, x2, y2, maxX, minY, maxX, maxY) ||
                   LinesIntersect(x1, y1, x2, y2, maxX, maxY, minX, maxY) ||
                   LinesIntersect(x1, y1, x2, y2, minX, maxY, minX, minY);
        }

        private static bool PointInClosedBox(double x, double y, double minX, double minY, double maxX, double maxY)
        {
            const double tol = 1.0e-9;
            return x >= minX - tol && x <= maxX + tol && y >= minY - tol && y <= maxY + tol;
        }

        private static bool LinesIntersect(double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy)
        {
            var denom = (bx - ax) * (dy - cy) - (by - ay) * (dx - cx);
            if (Math.Abs(denom) < 1.0e-12) return false;
            var t = ((cx - ax) * (dy - cy) - (cy - ay) * (dx - cx)) / denom;
            var u = ((cx - ax) * (by - ay) - (cy - ay) * (bx - ax)) / denom;
            const double tol = 1.0e-9;
            return t >= -tol && t <= 1.0 + tol && u >= -tol && u <= 1.0 + tol;
        }
    }
}