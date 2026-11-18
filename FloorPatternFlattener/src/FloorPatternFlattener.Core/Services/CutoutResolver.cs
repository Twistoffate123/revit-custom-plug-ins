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
        public List<RegionXY> Regions { get; } = new List<RegionXY>();
        public List<string> DependencyIds { get; } = new List<string>();
    }

    internal static class CutoutResolver
    {
        public static void ApplyToInput(Document doc, Floor floor, FloorCutoutData data, FloorHatchInput input)
        {
            if (doc == null || floor == null || input == null || data == null || data.IsEmpty) return;
            var resolved = Resolve(doc, data);
            input.CutoutRegions.Clear();
            foreach (var r in resolved.Regions)
            {
                if (r != null && !r.IsEmpty) input.CutoutRegions.Add(r);
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
                if (!region.IsEmpty) result.Regions.Add(region);
            }
            foreach (var sketch in data.Sketches ?? new List<SketchCutout>())
            {
                var region = CutoutStorageCodec.SketchToRegion(sketch);
                if (!region.IsEmpty) result.Regions.Add(region);
            }
            return result;
        }

        public static void LogCutoutPhase(Stopwatch sw, int regions, SegmentPolygonClipper.ClipStats stats)
        {
            FpfRefreshLog.LogIfActive("cutout", sw.ElapsedMilliseconds,
                "regions=" + regions.ToString(CultureInfo.InvariantCulture) +
                " removed=" + stats.Removed.ToString(CultureInfo.InvariantCulture) +
                " clipped=" + stats.Clipped.ToString(CultureInfo.InvariantCulture) +
                " kept=" + stats.Kept.ToString(CultureInfo.InvariantCulture));
        }

        private static void AddUnique(List<string> list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return;
            for (var i = 0; i < list.Count; i++)
                if (list[i] == id) return;
            list.Add(id);
        }
    }
}