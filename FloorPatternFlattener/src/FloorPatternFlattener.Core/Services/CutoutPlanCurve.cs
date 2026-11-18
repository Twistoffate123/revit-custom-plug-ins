using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Services
{
    /// <summary>Tessellates Revit sketch curves into closed cut-out loops (XY, feet).</summary>
    internal static class CutoutPlanCurve
    {
        private static double WeldFeet => CutoutCurveMath.ChordToleranceFeet;

        public static IList<SketchCutout> SketchesFromModelCurves(IEnumerable<Curve> curves)
        {
            var result = new List<SketchCutout>();
            if (curves == null) return result;
            var polylines = new List<IList<double[]>>();
            foreach (var curve in curves)
            {
                if (curve == null) continue;
                var xy = TessellateCurveXY(curve, Transform.Identity);
                if (xy != null && xy.Count >= 2) polylines.Add(xy);
            }
            foreach (var loop in CutoutCurveMath.ChainPolylinesToLoops(polylines, WeldFeet))
            {
                if (CutoutCurveMath.IsDegenerateLoop(loop, out _)) continue;
                var sketch = new SketchCutout();
                foreach (var pt in loop)
                    sketch.Points.Add(new[] { pt[0], pt[1] });
                if (sketch.Points.Count >= 3) result.Add(sketch);
            }
            return result;
        }

        public static IList<double[]> TessellateCurveXY(Curve curve, Transform tr)
        {
            if (curve == null) return new List<double[]>();
            IList<XYZ> raw;
            try { raw = curve.Tessellate(); }
            catch { raw = null; }
            var chord = CutoutCurveMath.ChordToleranceFeet;

            if (NeedsDenseSample(curve, raw))
                raw = SampleCurveDense(curve, chord);

            if (raw == null || raw.Count < 2)
                return new List<double[]>();

            var list = new List<double[]>(raw.Count);
            foreach (var p in raw)
            {
                var q = tr == null || tr.IsIdentity ? p : tr.OfPoint(p);
                list.Add(new[] { q.X, q.Y });
            }
            return list;
        }

        public static void AddCurveEdgesToRegion(RegionXY region, Curve curve, Transform tr)
        {
            var pts = TessellateCurveXY(curve, tr);
            for (var i = 0; i + 1 < pts.Count; i++)
                region.AddEdge(pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1]);
        }

        private static bool NeedsDenseSample(Curve curve, IList<XYZ> tessellated)
        {
            if (curve == null) return true;
            try
            {
                if (!curve.IsBound) return true;
            }
            catch { return true; }
            if (tessellated == null || tessellated.Count < 3) return true;
            try
            {
                var a = tessellated[0];
                var b = tessellated[tessellated.Count - 1];
                if (a.DistanceTo(b) < WeldFeet) return true;
            }
            catch { /* ignore */ }
            if (curve is Arc || curve is Ellipse || curve is NurbSpline || curve is HermiteSpline)
            {
                var len3d = 0.0;
                for (var i = 0; i + 1 < tessellated.Count; i++)
                    len3d += tessellated[i].DistanceTo(tessellated[i + 1]);
                if (len3d > chordFeet(curve) * 1.5 && tessellated.Count < CutoutCurveMath.MinFullCircleSegments / 2)
                    return true;
            }
            return false;
        }

        private static double chordFeet(Curve curve) => CutoutCurveMath.ChordToleranceFeet;

        private static IList<XYZ> SampleCurveDense(Curve curve, double chordFeet)
        {
            if (curve == null) return new List<XYZ>();

            if (curve is Arc arc)
                return SampleArc(arc, chordFeet);

            IList<XYZ> list = new List<XYZ>();
            try
            {
                if (!curve.IsBound)
                {
                    if (curve is Arc unboundArc) return SampleArc(unboundArc, chordFeet);
                }

                var p0 = curve.GetEndParameter(0);
                var p1 = curve.GetEndParameter(1);
                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);
                if (start.DistanceTo(end) < WeldFeet && curve is Arc closedArc)
                    return SampleArc(closedArc, chordFeet);

                var length = curve.Length;
                var steps = Math.Max(2, (int)Math.Ceiling(length / Math.Max(chordFeet, 1.0e-9)));
                if (curve is Arc) steps = Math.Max(steps, CutoutCurveMath.MinFullCircleSegments / 4);
                list.Add(start);
                for (var i = 1; i < steps; i++)
                {
                    var t = p0 + (p1 - p0) * i / steps;
                    list.Add(curve.Evaluate(t, true));
                }
                list.Add(end);
                return list;
            }
            catch
            {
                try { return curve.Tessellate(); }
                catch { return list; }
            }
        }

        private static IList<XYZ> SampleArc(Arc arc, double chordFeet)
        {
            try
            {
                var center = arc.Center;
                var radius = arc.Radius;
                var xDir = arc.XDirection;
                var yDir = arc.YDirection;
                double a0, a1;
                try
                {
                    a0 = arc.GetEndParameter(0);
                    a1 = arc.GetEndParameter(1);
                }
                catch
                {
                    a0 = 0;
                    a1 = 2.0 * Math.PI;
                }

                var span = a1 - a0;
                var closed = !arc.IsBound || Math.Abs(span - 2.0 * Math.PI) < 1.0e-6;
                try
                {
                    if (!closed && arc.GetEndPoint(0).DistanceTo(arc.GetEndPoint(1)) < WeldFeet)
                        closed = true;
                }
                catch { /* ignore */ }

                if (closed)
                {
                    var n = CutoutCurveMath.MinFullCircleSegments;
                    var ring = new List<XYZ>(n);
                    for (var i = 0; i < n; i++)
                    {
                        var ang = 2.0 * Math.PI * i / n;
                        ring.Add(center + radius * (Math.Cos(ang) * xDir + Math.Sin(ang) * yDir));
                    }
                    ring.Add(ring[0]);
                    return ring;
                }

                var arcLen = Math.Abs(span) * radius;
                var steps = Math.Max(2, (int)Math.Ceiling(arcLen / Math.Max(chordFeet, 1.0e-9)));
                steps = Math.Max(steps, (int)Math.Ceiling(Math.Abs(span) / (2.0 * Math.PI) * CutoutCurveMath.MinFullCircleSegments));
                var pts = new List<XYZ>(steps + 1);
                for (var i = 0; i <= steps; i++)
                {
                    var ang = a0 + span * i / steps;
                    pts.Add(center + radius * (Math.Cos(ang) * xDir + Math.Sin(ang) * yDir));
                }
                return pts;
            }
            catch
            {
                try { return arc.Tessellate(); }
                catch { return new List<XYZ>(); }
            }
        }
    }
}