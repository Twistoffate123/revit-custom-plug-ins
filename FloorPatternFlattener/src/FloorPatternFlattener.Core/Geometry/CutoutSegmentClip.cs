using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Geometry
{
    internal static class CutoutSegmentClip
    {
        /// <summary>
        /// Replaces <paramref name="segments"/> with the parts outside every cut-out region.
        /// </summary>
        public static SegmentPolygonClipper.ClipStats ClipSegmentListInPlace(
            IList<Segment3> segments, IList<RegionXY> cutouts, double minLen)
        {
            var stats = new SegmentPolygonClipper.ClipStats { Input = segments?.Count ?? 0 };
            if (segments == null || segments.Count == 0 || cutouts == null || cutouts.Count == 0)
            {
                stats.Kept = stats.Input;
                return stats;
            }

            var next = new List<Segment3>(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg?.A == null || seg?.B == null || seg.Layer == null)
                {
                    next.Add(seg);
                    continue;
                }
                var x1 = seg.A.X;
                var y1 = seg.A.Y;
                var x2 = seg.B.X;
                var y2 = seg.B.Y;
                var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(x1, y1, x2, y2, cutouts, minLen, ref stats);
                if (parts.Count == 0)
                {
                    stats.Removed++;
                    continue;
                }
                if (parts.Count == 1 && SegmentUnchanged(parts[0], x1, y1, x2, y2, minLen))
                {
                    next.Add(seg);
                    stats.Kept++;
                    continue;
                }
                stats.Clipped++;
                foreach (var p in parts)
                {
                    var layer = seg.Layer;
                    var run = seg.EdgeRun;
                    var za = seg.A.Z + (seg.B.Z - seg.A.Z) * ParamAlong(x1, y1, x2, y2, p[0], p[1]);
                    var zb = seg.A.Z + (seg.B.Z - seg.A.Z) * ParamAlong(x1, y1, x2, y2, p[2], p[3]);
                    next.Add(new Segment3
                    {
                        A = new XYZ(p[0], p[1], za),
                        B = new XYZ(p[2], p[3], zb),
                        Layer = layer,
                        EdgeRun = run
                    });
                }
            }

            segments.Clear();
            foreach (var s in next) segments.Add(s);
            stats.Kept = next.Count;
            return stats;
        }

        private static bool SegmentUnchanged(double[] part, double x1, double y1, double x2, double y2, double minLen)
        {
            if (part == null || part.Length < 4) return false;
            var tol = Math.Max(minLen, 1.0e-6);
            var sameOrder =
                Math.Abs(part[0] - x1) <= tol && Math.Abs(part[1] - y1) <= tol &&
                Math.Abs(part[2] - x2) <= tol && Math.Abs(part[3] - y2) <= tol;
            var revOrder =
                Math.Abs(part[0] - x2) <= tol && Math.Abs(part[1] - y2) <= tol &&
                Math.Abs(part[2] - x1) <= tol && Math.Abs(part[3] - y1) <= tol;
            return sameOrder || revOrder;
        }

        private static double ParamAlong(double x1, double y1, double x2, double y2, double px, double py)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var len2 = dx * dx + dy * dy;
            if (len2 < 1.0e-18) return 0;
            var t = ((px - x1) * dx + (py - y1) * dy) / len2;
            if (t < 0) return 0;
            if (t > 1) return 1;
            return t;
        }
    }
}