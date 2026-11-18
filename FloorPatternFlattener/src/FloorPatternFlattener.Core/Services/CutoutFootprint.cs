using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;

namespace FloorPatternFlattener.Services
{
    /// <summary>Plan footprint of model elements for hatch cut-outs.</summary>
    internal static class CutoutFootprint
    {
        public static RegionXY FromElement(Document doc, Element element, double offsetFeet)
        {
            if (doc == null || element == null || !element.IsValidObject) return new RegionXY();
            var region = new RegionXY();
            try
            {
                CollectGeometry(doc, element, Transform.Identity, region);
            }
            catch
            {
                // ignore
            }
            if (region.IsEmpty)
            {
                try { AddBoundingBoxFootprint(element, Transform.Identity, region); }
                catch { /* ignore */ }
            }
            if (offsetFeet > 1.0e-9) OffsetRegion(region, offsetFeet);
            return region;
        }

        public static RegionXY FromReference(Document doc, Reference reference, double offsetFeet)
        {
            if (doc == null || reference == null) return new RegionXY();
            try
            {
                if (reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    var link = doc.GetElement(reference.ElementId) as RevitLinkInstance;
                    if (link == null) return new RegionXY();
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc == null) return new RegionXY();
                    var linked = linkDoc.GetElement(reference.LinkedElementId);
                    if (linked == null) return new RegionXY();
                    var region = new RegionXY();
                    var tr = link.GetTotalTransform();
                    CollectGeometry(linkDoc, linked, tr, region);
                    if (region.IsEmpty) AddBoundingBoxFootprint(linked, tr, region);
                    if (offsetFeet > 1.0e-9) OffsetRegion(region, offsetFeet);
                    return region;
                }
            }
            catch { /* fall through to host */ }

            var host = doc.GetElement(reference.ElementId);
            return FromElement(doc, host, offsetFeet);
        }

        public static string StableRefKey(Document doc, Reference reference)
        {
            if (reference == null) return string.Empty;
            try
            {
                if (reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    var link = doc.GetElement(reference.ElementId) as RevitLinkInstance;
                    var linkUid = link?.UniqueId ?? reference.ElementId.ToString();
                    var linkDoc = link?.GetLinkDocument();
                    var linked = linkDoc?.GetElement(reference.LinkedElementId);
                    return linkUid + "|" + (linked?.UniqueId ?? reference.LinkedElementId.ToString());
                }
            }
            catch { /* ignore */ }
            try
            {
                return doc.GetElement(reference.ElementId)?.UniqueId ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static Element ResolveElement(Document doc, string storedKey)
        {
            if (string.IsNullOrEmpty(storedKey)) return null;
            if (storedKey.IndexOf('|') >= 0)
            {
                var parts = storedKey.Split(new[] { '|' }, 2);
                var link = doc.GetElement(parts[0]) as RevitLinkInstance;
                var linkDoc = link?.GetLinkDocument();
                if (linkDoc == null || parts.Length < 2) return null;
                return linkDoc.GetElement(parts[1]);
            }
            return doc.GetElement(storedKey);
        }

        private static void CollectGeometry(Document doc, Element element, Transform tr, RegionXY region)
        {
            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            try { opt.DetailLevel = ViewDetailLevel.Fine; } catch { /* ignore */ }
            GeometryElement ge;
            try { ge = element.get_Geometry(opt); }
            catch { return; }
            if (ge == null) return;
            WalkGeometry(ge, tr, region);
        }

        private static void WalkGeometry(GeometryElement ge, Transform tr, RegionXY region)
        {
            foreach (var obj in ge)
            {
                if (obj is Solid solid && solid.Volume > 1.0e-12)
                    ProjectSolid(solid, tr, region);
                else if (obj is GeometryInstance inst)
                {
                    var sub = inst.GetInstanceGeometry();
                    if (sub != null) WalkGeometry(sub, tr.Multiply(inst.Transform), region);
                }
                else if (obj is Mesh mesh)
                    ProjectMesh(mesh, tr, region);
            }
        }

        private static void ProjectSolid(Solid solid, Transform tr, RegionXY region)
        {
            foreach (Face face in solid.Faces)
            {
                var mesh = face.Triangulate();
                if (mesh == null) continue;
                ProjectMesh(mesh, tr, region);
            }
        }

        private static void ProjectMesh(Mesh mesh, Transform tr, RegionXY region)
        {
            if (mesh == null) return;
            var verts = mesh.Vertices;
            var tris = mesh.NumTriangles;
            for (var t = 0; t < tris; t++)
            {
                var tri = mesh.get_Triangle(t);
                var a = tr.OfPoint(tri.get_Vertex(0));
                var b = tr.OfPoint(tri.get_Vertex(1));
                var c = tr.OfPoint(tri.get_Vertex(2));
                region.AddEdge(a.X, a.Y, b.X, b.Y);
                region.AddEdge(b.X, b.Y, c.X, c.Y);
                region.AddEdge(c.X, c.Y, a.X, a.Y);
            }
        }

        private static void AddBoundingBoxFootprint(Element element, Transform tr, RegionXY region)
        {
            var bb = element.get_BoundingBox(null);
            if (bb == null) return;
            var p0 = tr.OfPoint(bb.Min);
            var p1 = tr.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z));
            var p2 = tr.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z));
            var p3 = tr.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z));
            region.AddEdge(p0.X, p0.Y, p1.X, p1.Y);
            region.AddEdge(p1.X, p1.Y, p2.X, p2.Y);
            region.AddEdge(p2.X, p2.Y, p3.X, p3.Y);
            region.AddEdge(p3.X, p3.Y, p0.X, p0.Y);
        }

        /// <summary>Expands the footprint by shifting each edge along its left normal (small offsets).</summary>
        public static void OffsetRegion(RegionXY region, double offsetFeet)
        {
            if (region == null || region.IsEmpty || offsetFeet < 1.0e-9) return;
            var raw = region.RawEdges;
            var extra = new List<double>();
            for (var i = 0; i < raw.Count; i += 4)
            {
                var x1 = raw[i];
                var y1 = raw[i + 1];
                var x2 = raw[i + 2];
                var y2 = raw[i + 3];
                var dx = x2 - x1;
                var dy = y2 - y1;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1.0e-9) continue;
                var nx = -dy / len * offsetFeet;
                var ny = dx / len * offsetFeet;
                extra.Add(x1 + nx);
                extra.Add(y1 + ny);
                extra.Add(x2 + nx);
                extra.Add(y2 + ny);
            }
            for (var i = 0; i < extra.Count; i += 4)
                region.AddEdge(extra[i], extra[i + 1], extra[i + 2], extra[i + 3]);
        }
    }
}