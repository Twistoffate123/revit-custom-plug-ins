using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// Extracts a floor's plan outline and top elevation, then builds hatch line segments
    /// lying on a horizontal plane (plan-flat overlay).
    /// </summary>
    public static class FloorHatchBuilder
    {
        public sealed class HatchResult
        {
            public List<(XYZ A, XYZ B)> Segments { get; } = new List<(XYZ A, XYZ B)>();
            public double Elevation { get; set; }
            public Color Color { get; set; } = new Color(80, 80, 80);
            public double SpacingFeet { get; set; }
        }

        public static HatchResult Build(Floor floor, Document doc)
        {
            var result = new HatchResult();
            if (floor == null || doc == null) return result;

            if (!TryGetTopOutline(floor, out var outlineXy, out var elevation))
                return result;

            result.Elevation = elevation;

            var patternInfo = TryReadSurfacePattern(floor, doc);
            result.SpacingFeet = patternInfo.SpacingFeet;
            result.Color = patternInfo.Color;
            var angle0 = patternInfo.AngleRadians;
            var angle1 = angle0 + Math.PI / 2.0;

            PolygonClipper.GetBounds(outlineXy, out var minX, out var minY, out _, out _);
            var originX = minX;
            var originY = minY;

            var lines0 = PolygonClipper.ClipParallelLines(outlineXy, result.SpacingFeet, angle0, originX, originY);
            var lines1 = PolygonClipper.ClipParallelLines(outlineXy, result.SpacingFeet, angle1, originX, originY);

            foreach (var (a, b) in lines0.Concat(lines1))
            {
                var aa = new XYZ(a.X, a.Y, elevation);
                var bb = new XYZ(b.X, b.Y, elevation);
                if (aa.DistanceTo(bb) > 1.0e-6)
                    result.Segments.Add((aa, bb));
            }

            return result;
        }

        private struct PatternInfo
        {
            public double SpacingFeet;
            public double AngleRadians;
            public Color Color;
        }

        private static PatternInfo TryReadSurfacePattern(Floor floor, Document doc)
        {
            // Default: ~300 mm grid. Revit internal units are feet.
            const double defaultMm = 300.0;
            var defaultFeet = UnitUtils.ConvertToInternalUnits(defaultMm, UnitTypeId.Millimeters);
            // Fallback if UnitTypeId unavailable in very old APIs — still fine for 2023+.
            if (defaultFeet <= 0)
                defaultFeet = 300.0 / 304.8;

            var info = new PatternInfo
            {
                SpacingFeet = defaultFeet,
                AngleRadians = 0.0,
                Color = new Color(80, 80, 80)
            };

            try
            {
                var matId = GetTopFaceMaterialId(floor);
                if (matId == null || matId == ElementId.InvalidElementId)
                    return info;

                var material = doc.GetElement(matId) as Material;
                if (material == null) return info;

                // Prefer foreground surface pattern (model pattern for floors).
                var fpId = material.SurfaceForegroundPatternId;
                if (fpId == null || fpId == ElementId.InvalidElementId)
                    fpId = material.SurfaceBackgroundPatternId;

                var fp = doc.GetElement(fpId) as FillPatternElement;
                if (fp != null)
                {
                    var pattern = fp.GetFillPattern();
                    if (pattern != null && !pattern.IsSolidFill)
                    {
                        // Use first fill grid length/angle when available.
                        try
                        {
                            var grids = pattern.GetFillGrids();
                            if (grids != null && grids.Count > 0)
                            {
                                var grid = grids[0];
                                // Offset is the distance between parallel hatch lines.
                                var spacing = Math.Abs(grid.Offset);
                                if (spacing > 1.0e-9)
                                    info.SpacingFeet = spacing;
                                info.AngleRadians = grid.Angle;
                            }
                        }
                        catch
                        {
                            // Some patterns expose grids differently; keep defaults.
                        }
                    }
                }

                try
                {
                    var c = material.SurfaceForegroundPatternColor;
                    if (c != null)
                        info.Color = c;
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // Material/pattern APIs vary; defaults are fine.
            }

            return info;
        }

        private static ElementId GetTopFaceMaterialId(Floor floor)
        {
            try
            {
                // Compound structure outer layer material is a good proxy for finish.
                var type = floor.Document.GetElement(floor.GetTypeId()) as HostObjAttributes;
                var cs = type?.GetCompoundStructure();
                if (cs != null)
                {
                    var layers = cs.GetLayers();
                    if (layers != null && layers.Count > 0)
                    {
                        // Layer 0 is typically the top/exterior finish for floors.
                        return layers[0].MaterialId;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return ElementId.InvalidElementId;
        }

        public static bool TryGetTopOutline(Floor floor, out List<XYZ> outlineXy, out double elevation)
        {
            outlineXy = null;
            elevation = 0;

            var options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };

            var geom = floor.get_Geometry(options);
            if (geom == null) return false;

            PlanarFace bestFace = null;
            double bestZ = double.MinValue;
            double bestArea = -1;

            foreach (var obj in geom)
            {
                Solid solid = obj as Solid;
                if (solid == null && obj is GeometryInstance gi)
                {
                    var instGeom = gi.GetInstanceGeometry();
                    if (instGeom == null) continue;
                    foreach (var o2 in instGeom)
                    {
                        solid = o2 as Solid;
                        if (solid != null && solid.Faces != null && solid.Volume > 0)
                            ConsiderSolid(solid, ref bestFace, ref bestZ, ref bestArea);
                    }
                    continue;
                }

                if (solid != null && solid.Faces != null && solid.Volume > 0)
                    ConsiderSolid(solid, ref bestFace, ref bestZ, ref bestArea);
            }

            if (bestFace == null)
            {
                // Fallback: bounding box top
                var bb = floor.get_BoundingBox(null);
                if (bb == null) return false;
                elevation = bb.Max.Z;
                outlineXy = new List<XYZ>
                {
                    new XYZ(bb.Min.X, bb.Min.Y, 0),
                    new XYZ(bb.Max.X, bb.Min.Y, 0),
                    new XYZ(bb.Max.X, bb.Max.Y, 0),
                    new XYZ(bb.Min.X, bb.Max.Y, 0),
                    new XYZ(bb.Min.X, bb.Min.Y, 0)
                };
                return true;
            }

            elevation = bestFace.Origin.Z;
            // Prefer a horizontal face; for multi-slope use highest planar top face Z as plane.
            if (!IsHorizontal(bestFace))
            {
                // Average Z of face edges as a compromise for gently sloped tops.
                elevation = AverageEdgeZ(bestFace);
            }

            outlineXy = ExtractOuterLoopXy(bestFace);
            if (outlineXy == null || outlineXy.Count < 3)
            {
                var bb = floor.get_BoundingBox(null);
                if (bb == null) return false;
                elevation = Math.Max(elevation, bb.Max.Z);
                outlineXy = new List<XYZ>
                {
                    new XYZ(bb.Min.X, bb.Min.Y, 0),
                    new XYZ(bb.Max.X, bb.Min.Y, 0),
                    new XYZ(bb.Max.X, bb.Max.Y, 0),
                    new XYZ(bb.Min.X, bb.Max.Y, 0),
                    new XYZ(bb.Min.X, bb.Min.Y, 0)
                };
            }

            return true;
        }

        private static void ConsiderSolid(Solid solid, ref PlanarFace bestFace, ref double bestZ, ref double bestArea)
        {
            foreach (Face face in solid.Faces)
            {
                var pf = face as PlanarFace;
                if (pf == null) continue;
                var n = pf.FaceNormal;
                // Upward-facing
                if (n.Z <= 0.1) continue;
                var z = pf.Origin.Z;
                var area = pf.Area;
                if (z > bestZ + 1.0e-6 || (Math.Abs(z - bestZ) < 1.0e-6 && area > bestArea))
                {
                    bestZ = z;
                    bestArea = area;
                    bestFace = pf;
                }
            }
        }

        private static bool IsHorizontal(PlanarFace face)
        {
            return Math.Abs(face.FaceNormal.Z) > 0.999;
        }

        private static double AverageEdgeZ(PlanarFace face)
        {
            double sum = 0;
            int n = 0;
            foreach (EdgeArray loop in face.EdgeLoops)
            {
                foreach (Edge e in loop)
                {
                    var curve = e.AsCurve();
                    if (curve == null) continue;
                    sum += curve.GetEndPoint(0).Z;
                    sum += curve.GetEndPoint(1).Z;
                    n += 2;
                }
            }
            return n > 0 ? sum / n : face.Origin.Z;
        }

        private static List<XYZ> ExtractOuterLoopXy(PlanarFace face)
        {
            EdgeArray outer = null;
            double bestLen = -1;
            foreach (EdgeArray loop in face.EdgeLoops)
            {
                double len = 0;
                foreach (Edge e in loop)
                {
                    try { len += e.ApproximateLength; }
                    catch { /* ignore */ }
                }
                if (len > bestLen)
                {
                    bestLen = len;
                    outer = loop;
                }
            }

            if (outer == null) return null;

            var pts = new List<XYZ>();
            foreach (Edge e in outer)
            {
                var curve = e.AsCurve();
                if (curve == null) continue;
                // Tessellate for non-lines
                IList<XYZ> tess;
                try { tess = curve.Tessellate(); }
                catch
                {
                    tess = new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };
                }

                for (var i = 0; i < tess.Count; i++)
                {
                    var p = new XYZ(tess[i].X, tess[i].Y, 0);
                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 1.0e-7)
                        pts.Add(p);
                }
            }

            if (pts.Count >= 3)
            {
                if (pts[0].DistanceTo(pts[pts.Count - 1]) > 1.0e-6)
                    pts.Add(pts[0]);
                return pts;
            }

            return null;
        }
    }
}
