using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;

namespace FloorPatternFlattener.Services
{
    /// <summary>Reads back DirectShape curve Z in world space and compares to the draped floor top.</summary>
    internal static class GeneratedElevationVerifier
    {
        public sealed class Sample
        {
            public int Shapes;
            public int Endpoints;
            public double FloorTopMinFt = double.PositiveInfinity;
            public double FloorTopMaxFt = double.NegativeInfinity;
            public double CurveMinFt = double.PositiveInfinity;
            public double CurveMaxFt = double.NegativeInfinity;
            public int BelowTop;
            public double WorstDeficitFt;
            public string Phase = string.Empty;
            public bool Valid =>
                Endpoints > 0 && double.IsFinite(CurveMinFt) && double.IsFinite(CurveMaxFt);
        }

        public sealed class Report
        {
            public Sample PreGroup = new Sample();
            public Sample PostGroup = new Sample();
            public int ShapesMoved;
            public string FloorTopZText = string.Empty;
            public string GroupOriginZText = "n/a";
            public string SketchPlaneText = "n/a (DirectShape model curves, no sketch plane)";
            public string ViewCutZText = "n/a";
        }

        public static double SampleTopZ(FloorHatchInput input, double x, double y)
        {
            if (input?.Pieces == null) return double.NaN;
            double best = double.NegativeInfinity;
            var tol = FpfOptions.MeshCrackBridgeFeet;
            foreach (var p in input.Pieces)
            {
                var r = p?.Region;
                if (r == null || r.IsEmpty) continue;
                if (x < r.MinX - tol || x > r.MaxX + tol || y < r.MinY - tol || y > r.MaxY + tol) continue;
                if (!PolygonClipper.ContainsPoint(r, x, y)) continue;
                var z = p.ZAt(x, y);
                if (z > best) best = z;
            }
            if (double.IsNegativeInfinity(best))
            {
                foreach (var p in input.Pieces)
                {
                    if (p?.Region == null || p.Region.IsEmpty) continue;
                    var cx = (p.Region.MinX + p.Region.MaxX) * 0.5;
                    var cy = (p.Region.MinY + p.Region.MaxY) * 0.5;
                    var z = p.ZAt(cx, cy);
                    if (z > best) best = z;
                }
            }
            return double.IsNegativeInfinity(best) ? double.NaN : best;
        }

        public static Sample Measure(Document doc, IEnumerable<DirectShape> shapes, FloorHatchInput input, string phase,
            double requiredLiftMm = 0)
        {
            var s = new Sample { Phase = phase };
            if (doc == null || shapes == null || input == null) return s;
            foreach (var ds in shapes)
            {
                if (ds == null || !ds.IsValidObject) continue;
                s.Shapes++;
                foreach (var pt in ReadEndpoints(doc, ds))
                {
                    s.Endpoints++;
                    if (pt.Z < s.CurveMinFt) s.CurveMinFt = pt.Z;
                    if (pt.Z > s.CurveMaxFt) s.CurveMaxFt = pt.Z;
                    var top = SampleTopZ(input, pt.X, pt.Y);
                    if (!double.IsFinite(top)) continue;
                    if (top < s.FloorTopMinFt) s.FloorTopMinFt = top;
                    if (top > s.FloorTopMaxFt) s.FloorTopMaxFt = top;
                    var need = top + FpfOptions.VerifiedClearanceAboveTopFeet +
                               FpfProjectParameters.MmToFeet(Math.Max(0, requiredLiftMm));
                    if (pt.Z + 1.0e-9 < need)
                    {
                        s.BelowTop++;
                        var deficit = need - pt.Z;
                        if (deficit > s.WorstDeficitFt) s.WorstDeficitFt = deficit;
                    }
                }
            }
            if (!double.IsFinite(s.CurveMinFt)) s.CurveMinFt = double.NaN;
            if (!double.IsFinite(s.CurveMaxFt)) s.CurveMaxFt = double.NaN;
            if (!double.IsFinite(s.FloorTopMinFt)) s.FloorTopMinFt = double.NaN;
            if (!double.IsFinite(s.FloorTopMaxFt)) s.FloorTopMaxFt = double.NaN;
            return s;
        }

        public static int CorrectBelowTop(Document doc, IList<DirectShape> shapes, FloorHatchInput input, out int endpoints,
            double requiredLiftMm = 0)
        {
            endpoints = 0;
            if (doc == null || shapes == null || shapes.Count == 0) return 0;
            var moves = new Dictionary<ElementId, double>();
            foreach (var ds in shapes)
            {
                if (ds == null || !ds.IsValidObject) continue;
                var pts = ReadEndpoints(doc, ds);
                endpoints += pts.Count;
                var maxDeficit = 0.0;
                foreach (var pt in pts)
                {
                    var top = SampleTopZ(input, pt.X, pt.Y);
                    if (!double.IsFinite(top)) continue;
                    var need = top + FpfOptions.VerifiedClearanceAboveTopFeet +
                               FpfProjectParameters.MmToFeet(Math.Max(0, requiredLiftMm));
                    var deficit = need - pt.Z;
                    if (deficit > maxDeficit) maxDeficit = deficit;
                }
                if (maxDeficit > 1.0e-9) moves[ds.Id] = maxDeficit;
            }
            foreach (var kv in moves)
            {
                try
                {
                    ElementTransformUtils.MoveElement(doc, kv.Key, new XYZ(0, 0, kv.Value));
                }
                catch
                {
                    // ignore one shape
                }
            }
            return moves.Count;
        }

        public static void TryAlignGroupToFloor(Document doc, Group group, Floor floor)
        {
            if (doc == null || group == null || floor == null) return;
            TrySetGroupLevel(group, floor.LevelId);
            try
            {
                var fh = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                var gh = group.get_Parameter(BuiltInParameter.GROUP_OFFSET_FROM_LEVEL);
                if (fh != null && gh != null && !gh.IsReadOnly && gh.StorageType == StorageType.Double)
                    gh.Set(fh.AsDouble());
            }
            catch
            {
                // ignore
            }
        }

        public static string FormatFtMm(double feet)
        {
            if (!double.IsFinite(feet)) return "?";
            return feet.ToString("0.####", CultureInfo.InvariantCulture) + " ft (" +
                   (feet * 304.8).ToString("0.#", CultureInfo.InvariantCulture) + " mm)";
        }

        public static bool TryGetGroupOriginZ(Group group, out double z, out string text)
        {
            z = double.NaN;
            text = "n/a";
            if (group == null) return false;
            try
            {
                if (group.Location is LocationPoint lp)
                {
                    z = lp.Point.Z;
                    text = FormatFtMm(z);
                    return true;
                }
                var bb = group.get_BoundingBox(null);
                if (bb != null)
                {
                    z = (bb.Min.Z + bb.Max.Z) * 0.5;
                    text = "bbox mid " + FormatFtMm(z);
                    return true;
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        public static bool TryGetPlanCutZ(View view, Document doc, out string text)
        {
            text = "n/a";
            if (view == null || doc == null || !(view is ViewPlan vp)) return false;
            try
            {
                var level = doc.GetElement(vp.GenLevel?.Id ?? ElementId.InvalidElementId) as Level;
                if (level == null) return false;
                var vr = vp.GetViewRange();
                var off = vr.GetOffset(PlanViewPlane.CutPlane);
                var cutZ = level.Elevation + off;
                text = level.Name + " cut " + FormatFtMm(cutZ) + " (offset " + (off * 304.8).ToString("0.#", CultureInfo.InvariantCulture) + " mm)";
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>One-line elevation note from generation targets (no geometry read-back).</summary>
        public static string BuildPlannedSummary(Floor floor, FloorHatchInput input, double floorLiftMm)
        {
            var name = FpfFloorNaming.FloorDisplayName(floor);
            var patternFeet = FpfProjectParameters.MmToFeet(Math.Max(FpfOptions.DefaultFloorLiftMm, floorLiftMm));
            if (patternFeet + 1.0e-12 < FpfOptions.PatternAboveTopFeet)
                patternFeet = FpfOptions.PatternAboveTopFeet;
            var edgeFeet = patternFeet + FpfOptions.EdgeLiftAboveHatchFeet;
            var top = SampleTopZ(input, 0, 0);
            if (!double.IsFinite(top) && input?.Pieces?.Count > 0)
            {
                var r = input.Pieces[0].Region;
                if (r != null && !r.IsEmpty)
                    top = SampleTopZ(input, (r.MinX + r.MaxX) * 0.5, (r.MinY + r.MaxY) * 0.5);
            }
            var topText = double.IsFinite(top) ? "; draped top ~" + FormatFtMm(top) : string.Empty;
            return name + ": pattern world Z = top + " + (patternFeet * 304.8).ToString("0.##", CultureInfo.InvariantCulture) +
                   " mm, edges + " + (edgeFeet * 304.8).ToString("0.##", CultureInfo.InvariantCulture) +
                   " mm (planned, no read-back)" + topText;
        }

        public static string BuildSummary(Floor floor, Report r)
        {
            var name = FpfFloorNaming.FloorDisplayName(floor);
            if (!r.PostGroup.Valid && !r.PreGroup.Valid)
                return name + ": elevation read-back failed (no curve geometry)";
            var post = r.PostGroup.Valid ? r.PostGroup : r.PreGroup;
            var sb = name + ": draped top Z " +
                     FormatFtMm(post.FloorTopMinFt) + ".." + FormatFtMm(post.FloorTopMaxFt) +
                     "; curves " + FormatFtMm(post.CurveMinFt) + ".." + FormatFtMm(post.CurveMaxFt) +
                     " (" + post.Phase + ", " + post.Endpoints + " endpoints)";
            if (post.BelowTop > 0)
                sb += "; was " + post.BelowTop + " below top (worst " + (post.WorstDeficitFt * 304.8).ToString("0.##", CultureInfo.InvariantCulture) + " mm)";
            if (r.ShapesMoved > 0)
                sb += "; moved " + r.ShapesMoved + " shape(s) up";
            sb += "; group origin Z " + r.GroupOriginZText + "; view cut " + r.ViewCutZText + "; " + r.SketchPlaneText;
            return sb;
        }

        private static List<XYZ> ReadEndpoints(Document doc, DirectShape ds)
        {
            var list = new List<XYZ>();
            if (doc == null || ds == null) return list;
            try
            {
                var geom = ds.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                CollectEndpoints(geom, Transform.Identity, list);
            }
            catch
            {
                // ignore
            }
            return list;
        }

        private static void CollectEndpoints(GeometryElement geom, Transform xform, List<XYZ> dest)
        {
            if (geom == null) return;
            foreach (var obj in geom)
            {
                if (obj is GeometryInstance gi)
                {
                    var t = xform.Multiply(gi.Transform);
                    CollectEndpoints(gi.GetInstanceGeometry(), t, dest);
                    continue;
                }
                if (obj is Line line)
                {
                    dest.Add(xform.OfPoint(line.GetEndPoint(0)));
                    dest.Add(xform.OfPoint(line.GetEndPoint(1)));
                    continue;
                }
                if (obj is Curve curve && curve.IsBound)
                {
                    try
                    {
                        dest.Add(xform.OfPoint(curve.GetEndPoint(0)));
                        dest.Add(xform.OfPoint(curve.GetEndPoint(1)));
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }

        private static void TrySetGroupLevel(Group group, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId) return;
            try
            {
                var p = group.get_Parameter(BuiltInParameter.GROUP_LEVEL);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId)
                    p.Set(levelId);
            }
            catch
            {
                // ignore
            }
        }
    }
}