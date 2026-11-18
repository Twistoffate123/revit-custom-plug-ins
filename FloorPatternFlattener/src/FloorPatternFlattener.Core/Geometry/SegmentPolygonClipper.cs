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

            var ux = dx / len;
            var uy = dy / len;
            var outside = new List<double[]> { new[] { 0.0, len } };

            foreach (var region in cutouts)
            {
                if (region == null || region.IsEmpty) continue;
                if (!SegmentIntersectsAxisAlignedBox(x1, y1, x2, y2, region.MinX, region.MinY, region.MaxX, region.MaxY))
                    continue;

                var inside = PolygonClipper.ClipLine(region, x1, y1, ux, uy);
                if (inside.Count == 0) continue;
                var onSegment = ClipIntervalsToSegment(inside, len);
                if (onSegment.Count == 0) continue;
                outside = PolygonClipper.SubtractIntervals(outside, onSegment);
                if (outside.Count == 0) break;
            }

            return EmitPieces(x1, y1, ux, uy, outside, minLen);
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

        private static List<double[]> ClipIntervalsToSegment(IList<double[]> inside, double segLen)
        {
            var clipped = new List<double[]>();
            if (inside == null || segLen < 1.0e-12) return clipped;
            foreach (var iv in inside)
            {
                if (iv == null || iv.Length < 2) continue;
                var a0 = Math.Max(0, iv[0]);
                var a1 = Math.Min(segLen, iv[1]);
                if (a1 - a0 > FpfOptions.GeometryTolerance)
                    clipped.Add(new[] { a0, a1 });
            }
            return clipped;
        }

        private static List<double[]> EmitPieces(double x1, double y1, double ux, double uy,
            List<double[]> intervals, double minLen)
        {
            var pieces = new List<double[]>();
            foreach (var iv in intervals)
            {
                var a0 = iv[0];
                var a1 = iv[1];
                if (a1 - a0 < minLen) continue;
                var px1 = x1 + ux * a0;
                var py1 = y1 + uy * a0;
                var px2 = x1 + ux * a1;
                var py2 = y1 + uy * a1;
                pieces.Add(new[] { px1, py1, px2, py2 });
            }
            return pieces;
        }
    }
}