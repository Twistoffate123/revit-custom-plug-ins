using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Services
{
    internal sealed class CutoutResolveResult
    {
        public List<ResolvedCutout> Entries { get; } = new List<ResolvedCutout>();
        public List<string> DependencyIds { get; } = new List<string>();
    }

    internal sealed class ResolvedCutout
    {
        public string Kind;
        public RegionXY Region;
        public int PointCount;
        public double AreaFt2;
    }

    internal static class CutoutResolver
    {
        public static void ApplyToInput(Document doc, Floor floor, FloorCutoutData data, FloorHatchInput input)
        {
            if (doc == null || floor == null || input == null || data == null || data.IsEmpty) return;
            var resolved = Resolve(doc, data);
            var hasFloor = TryFloorPlanBounds(input, out var fMinX, out var fMinY, out var fMaxX, out var fMaxY);
            input.CutoutRegions.Clear();
            input.CutoutRegionKinds.Clear();
            foreach (var entry in resolved.Entries)
            {
                if (entry.Region == null || entry.Region.IsEmpty) continue;
                if (hasFloor && entry.Kind == "pick" && IsOutsideFloorBounds(entry.Region, fMinX, fMinY, fMaxX, fMaxY))
                {
                    FpfRefreshLog.LogIfActive("cutout", 0, "skip-pick outside-floor-bbox");
                    continue;
                }
                input.CutoutRegions.Add(entry.Region);
                input.CutoutRegionKinds.Add(entry.Kind ?? "region");
            }
            foreach (var id in resolved.DependencyIds)
                AddUnique(input.DependencyUniqueIds, id);
        }

        public static CutoutResolveResult Resolve(Document doc, FloorCutoutData data)
        {
            var result = new CutoutResolveResult();
            if (doc == null || data == null || data.IsEmpty) return result;
            foreach (var pick in data.Picked ?? new List<PickedCutout>())
            {
                if (pick == null || string.IsNullOrEmpty(pick.ElementUniqueId)) continue;
                result.DependencyIds.Add(pick.ElementUniqueId);
                var offsetFeet = FpfProjectParameters.MmToFeet(Math.Max(0, pick.OffsetMm));
                RegionXY region;
                if (pick.ElementUniqueId.IndexOf('|') >= 0)
                {
                    var link = doc.GetElement(pick.ElementUniqueId.Split('|')[0]) as RevitLinkInstance;
                    var linked = CutoutFootprint.ResolveElement(doc, pick.ElementUniqueId);
                    region = linked != null
                        ? CutoutFootprint.FromElement(link?.GetLinkDocument(), linked, offsetFeet)
                        : new RegionXY();
                }
                else
                {
                    var el = doc.GetElement(pick.ElementUniqueId);
                    region = CutoutFootprint.FromElement(doc, el, offsetFeet);
                }
                if (!region.IsEmpty)
                    result.Entries.Add(new ResolvedCutout { Kind = "pick", Region = region });
            }
            foreach (var sketch in data.Sketches ?? new List<SketchCutout>())
            {
                if (sketch?.Points == null || sketch.Points.Count < 3)
                {
                    LogSkippedSketch("points=" + (sketch?.Points?.Count ?? 0));
                    continue;
                }
                if (CutoutCurveMath.IsDegenerateLoop(sketch.Points, out var area))
                {
                    LogSkippedSketch("degenerate areaFt2=" + area.ToString("R", CultureInfo.InvariantCulture) +
                                     " points=" + sketch.Points.Count);
                    continue;
                }
                var region = CutoutStorageCodec.SketchToRegion(sketch);
                if (!region.IsEmpty)
                    result.Entries.Add(new ResolvedCutout
                    {
                        Kind = "sketch",
                        Region = region,
                        PointCount = Math.Max(sketch.Points.Count, region.EdgeCount),
                        AreaFt2 = area
                    });
                else
                    LogSkippedSketch("empty-region points=" + sketch.Points.Count);
            }
            return result;
        }

        /// <summary>Logs each cut-out region bbox and how many hatch segments may intersect it (plan feet, hatch XY).</summary>
        public static void LogCutoutRegionsAgainstSegments(FloorHatchInput input, IList<Segment3> segments)
        {
            if (input == null || input.CutoutRegions.Count == 0) return;
            double floorMinX = double.MaxValue, floorMinY = double.MaxValue;
            double floorMaxX = double.MinValue, floorMaxY = double.MinValue;
            var hasFloor = false;
            foreach (var p in input.Pieces)
            {
                if (p?.Region == null || p.Region.IsEmpty) continue;
                hasFloor = true;
                if (p.Region.MinX < floorMinX) floorMinX = p.Region.MinX;
                if (p.Region.MinY < floorMinY) floorMinY = p.Region.MinY;
                if (p.Region.MaxX > floorMaxX) floorMaxX = p.Region.MaxX;
                if (p.Region.MaxY > floorMaxY) floorMaxY = p.Region.MaxY;
            }

            for (var r = 0; r < input.CutoutRegions.Count; r++)
            {
                var region = input.CutoutRegions[r];
                if (region == null || region.IsEmpty) continue;
                var kind = r < input.CutoutRegionKinds.Count ? input.CutoutRegionKinds[r] : "region";
                var area = CutoutCurveMath.AbsoluteArea(RegionToPointLoop(region));
                var overlap = 0;
                if (segments != null)
                {
                    foreach (var seg in segments)
                    {
                        if (seg?.A == null || seg?.B == null) continue;
                        if (SegmentPolygonClipper.SegmentIntersectsAxisAlignedBox(
                                seg.A.X, seg.A.Y, seg.B.X, seg.B.Y,
                                region.MinX, region.MinY, region.MaxX, region.MaxY))
                            overlap++;
                    }
                }

                var floorNote = string.Empty;
                if (hasFloor)
                {
                    const double tol = 0.5;
                    var cx = (region.MinX + region.MaxX) * 0.5;
                    var cy = (region.MinY + region.MaxY) * 0.5;
                    if (cx < floorMinX - tol || cx > floorMaxX + tol || cy < floorMinY - tol || cy > floorMaxY + tol)
                        floorNote = " frame=outside-floor-bbox";
                }

                FpfRefreshLog.LogIfActive("cutout", 0,
                    "region kind=" + kind +
                    " edges=" + region.EdgeCount.ToString(CultureInfo.InvariantCulture) +
                    " areaFt2=" + area.ToString("R", CultureInfo.InvariantCulture) +
                    " bbox=" + region.MinX.ToString("R", CultureInfo.InvariantCulture) + "," +
                    region.MinY.ToString("R", CultureInfo.InvariantCulture) + "," +
                    region.MaxX.ToString("R", CultureInfo.InvariantCulture) + "," +
                    region.MaxY.ToString("R", CultureInfo.InvariantCulture) +
                    " segOverlap=" + overlap.ToString(CultureInfo.InvariantCulture) + floorNote);
            }
        }

        private static List<double[]> RegionToPointLoop(RegionXY region)
        {
            var pts = new List<double[]>();
            if (region == null) return pts;
            var e = region.RawEdges;
            if (e.Count >= 4)
            {
                pts.Add(new[] { e[0], e[1] });
                for (var i = 4; i < e.Count; i += 4)
                    pts.Add(new[] { e[i], e[i + 1] });
            }
            return pts;
        }

        public static void LogCutoutPhase(Stopwatch sw, int regions, SegmentPolygonClipper.ClipStats stats)
        {
            FpfRefreshLog.LogIfActive("cutout", sw.ElapsedMilliseconds,
                "regions=" + regions.ToString(CultureInfo.InvariantCulture) +
                " removed=" + stats.Removed.ToString(CultureInfo.InvariantCulture) +
                " clipped=" + stats.Clipped.ToString(CultureInfo.InvariantCulture) +
                " kept=" + stats.Kept.ToString(CultureInfo.InvariantCulture));
        }

        private static void LogSkippedSketch(string detail)
        {
            FpfRefreshLog.LogIfActive("cutout", 0, "skip-sketch " + (detail ?? ""));
        }

        private static void AddUnique(List<string> list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return;
            for (var i = 0; i < list.Count; i++)
                if (list[i] == id) return;
            list.Add(id);
        }

        private static bool TryFloorPlanBounds(FloorHatchInput input, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = double.MaxValue;
            minY = double.MaxValue;
            maxX = double.MinValue;
            maxY = double.MinValue;
            var any = false;
            if (input?.Pieces == null) return false;
            foreach (var p in input.Pieces)
            {
                if (p?.Region == null || p.Region.IsEmpty) continue;
                any = true;
                if (p.Region.MinX < minX) minX = p.Region.MinX;
                if (p.Region.MinY < minY) minY = p.Region.MinY;
                if (p.Region.MaxX > maxX) maxX = p.Region.MaxX;
                if (p.Region.MaxY > maxY) maxY = p.Region.MaxY;
            }
            return any;
        }

        private static bool IsOutsideFloorBounds(RegionXY region, double fMinX, double fMinY, double fMaxX, double fMaxY)
        {
            if (region == null || region.IsEmpty) return true;
            const double pad = 2.0;
            var cx = (region.MinX + region.MaxX) * 0.5;
            var cy = (region.MinY + region.MaxY) * 0.5;
            return cx < fMinX - pad || cx > fMaxX + pad || cy < fMinY - pad || cy > fMaxY + pad;
        }
    }
}