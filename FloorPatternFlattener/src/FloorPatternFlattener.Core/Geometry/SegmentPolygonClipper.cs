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
            var result = new List<double[]> { new[] { x1, y1, x2, y2 } };
            var dx = x2 - x1;
            var dy = y2 - y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < minLen) return new List<double[]>();

            var ux = dx / len;
            var uy = dy / len;
            var segMinX = Math.Min(x1, x2);
            var segMaxX = Math.Max(x1, x2);
            var segMinY = Math.Min(y1, y2);
            var segMaxY = Math.Max(y1, y2);

            foreach (var region in cutouts)
            {
                if (region == null || region.IsEmpty) continue;
                if (segMaxX < region.MinX - 1.0e-9 || segMinX > region.MaxX + 1.0e-9 ||
                    segMaxY < region.MinY - 1.0e-9 || segMinY > region.MaxY + 1.0e-9)
                    continue;

                var intervals = new List<double[]> { new[] { 0.0, len } };
                var inside = PolygonClipper.ClipLine(region, x1, y1, ux, uy);
                if (inside.Count == 0) continue;
                intervals = PolygonClipper.SubtractIntervals(intervals, inside);
                result = EmitPieces(x1, y1, ux, uy, intervals, minLen);
                if (result.Count == 0) break;
            }

            return result;
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