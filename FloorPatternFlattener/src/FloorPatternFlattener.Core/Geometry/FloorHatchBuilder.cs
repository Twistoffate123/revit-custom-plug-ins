using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// One upward-facing planar piece of a floor's top surface: its XY footprint (outer + holes) and plane.
    /// Planar faces map 1:1 to a piece; non-planar faces are triangulated into one piece per triangle.
    /// </summary>
    public sealed class TopPiece
    {
        public RegionXY Region { get; } = new RegionXY();
        public double Ox, Oy, Oz;   // point on plane
        public double Nx, Ny, Nz;   // unit normal, Nz > 0
        public PatternSource Pattern { get; set; }
        public bool FromTriangulation { get; set; }

        /// <summary>Z of the plane at (x, y): vertical projection onto the face plane.</summary>
        public double ZAt(double x, double y) => Oz - (Nx * (x - Ox) + Ny * (y - Oy)) / Nz;
    }

    /// <summary>Everything the hatch depends on, plus a signature (hash) of it.</summary>
    public sealed class FloorHatchInput
    {
        public List<TopPiece> Pieces { get; } = new List<TopPiece>();
        public string Signature { get; set; } = string.Empty;

        /// <summary>UniqueIds of materials / fill patterns used (for cheap updater filtering).</summary>
        public List<string> DependencyUniqueIds { get; } = new List<string>();

        public int NonPlanarFaces { get; set; }
        public bool UsedDefaultGrid { get; set; }
        public bool UsedDraftingPattern { get; set; }
        public List<string> PatternDescriptions { get; } = new List<string>();
    }

    public sealed class Segment3
    {
        public XYZ A;
        public XYZ B;
    }

    public sealed class FloorHatchResult
    {
        public List<Segment3> Segments { get; } = new List<Segment3>();
        public bool Truncated { get; set; }
        public int SkippedGrids { get; set; }
        public int SkippedShort { get; set; }
    }

    /// <summary>
    /// Builds the flat-pattern hatch for a floor:
    ///  1. find all upward-facing top faces (normal.Z &gt; tolerance), incl. every slope of a multi-slope floor;
    ///  2. project each face's edge loops (outer + openings) to XY;
    ///  3. generate the true fill-pattern lines in PLAN, aligned to the project internal origin;
    ///  4. clip them per face (even-odd), apply dashes;
    ///  5. lift the endpoints vertically onto the face plane and offset along the face normal.
    /// A straight line projected vertically onto a plane stays straight, so the result reads as a plan pattern
    /// while lying exactly on the slope.
    /// </summary>
    public static class FloorHatchBuilder
    {
        // ---------------------------------------------------------------- input

        public static FloorHatchInput ReadInput(Floor floor, PatternReader patterns)
        {
            if (floor == null) throw new ArgumentNullException(nameof(floor));
            var doc = floor.Document;
            var input = new FloorHatchInput();
            var fallbackMaterial = GetTypeTopMaterial(floor);

            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geom = null;
            try { geom = floor.get_Geometry(options); } catch { geom = null; }

            if (geom != null)
            {
                foreach (var obj in geom)
                {
                    if (obj is Solid s)
                        ReadSolid(doc, floor, s, fallbackMaterial, patterns, input);
                    else if (obj is GeometryInstance gi)
                    {
                        var ig = gi.GetInstanceGeometry();
                        if (ig == null) continue;
                        foreach (var o2 in ig)
                            if (o2 is Solid s2)
                                ReadSolid(doc, floor, s2, fallbackMaterial, patterns, input);
                    }
                }
            }

            input.Signature = ComputeSignature(floor, input);
            return input;
        }

        private static void ReadSolid(Document doc, Floor floor, Solid solid, ElementId fallbackMaterial,
            PatternReader patterns, FloorHatchInput input)
        {
            if (solid == null || solid.Faces == null || solid.Faces.Size == 0) return;
            if (solid.Volume <= 0) return;

            foreach (Face face in solid.Faces)
            {
                try
                {
                    if (face is PlanarFace pf)
                    {
                        var n = pf.FaceNormal;
                        if (n.Z <= FpfOptions.MinUpwardNormalZ) continue;

                        var piece = new TopPiece
                        {
                            Ox = pf.Origin.X, Oy = pf.Origin.Y, Oz = pf.Origin.Z,
                            Nx = n.X, Ny = n.Y, Nz = n.Z
                        };
                        AddFaceEdges(face, piece.Region);
                        if (piece.Region.IsEmpty) continue;
                        piece.Pattern = ResolvePattern(doc, floor, face, fallbackMaterial, patterns, input);
                        input.Pieces.Add(piece);
                    }
                    else
                    {
                        // Non-planar: best effort. If the face is upward-facing at its centre, triangulate it and
                        // treat every upward triangle as a small planar piece (lines are split at triangle edges
                        // but stay continuous because neighbouring triangles share edges).
                        var bb = face.GetBoundingBox();
                        var mid = new UV((bb.Min.U + bb.Max.U) * 0.5, (bb.Min.V + bb.Max.V) * 0.5);
                        var cn = face.ComputeNormal(mid);
                        if (cn == null || cn.Z <= FpfOptions.MinUpwardNormalZ) continue;

                        var mesh = face.Triangulate();
                        if (mesh == null || mesh.NumTriangles == 0) continue;

                        input.NonPlanarFaces++;
                        var pattern = ResolvePattern(doc, floor, face, fallbackMaterial, patterns, input);
                        for (var i = 0; i < mesh.NumTriangles; i++)
                        {
                            var tri = mesh.get_Triangle(i);
                            var a = tri.get_Vertex(0);
                            var b = tri.get_Vertex(1);
                            var c = tri.get_Vertex(2);
                            var nrm = (b - a).CrossProduct(c - a);
                            if (nrm.GetLength() < 1.0e-12) continue;
                            nrm = nrm.Normalize();
                            if (nrm.Z < 0) nrm = nrm.Negate();
                            if (nrm.Z <= FpfOptions.MinUpwardNormalZ) continue;

                            var piece = new TopPiece
                            {
                                Ox = a.X, Oy = a.Y, Oz = a.Z,
                                Nx = nrm.X, Ny = nrm.Y, Nz = nrm.Z,
                                Pattern = pattern,
                                FromTriangulation = true
                            };
                            piece.Region.AddEdge(a.X, a.Y, b.X, b.Y);
                            piece.Region.AddEdge(b.X, b.Y, c.X, c.Y);
                            piece.Region.AddEdge(c.X, c.Y, a.X, a.Y);
                            if (!piece.Region.IsEmpty) input.Pieces.Add(piece);
                        }
                    }
                }
                catch
                {
                    // A single bad face must not abort the whole floor.
                }
            }
        }

        /// <summary>All edge loops (outer + inner) of a face, tessellated, projected to XY.</summary>
        private static void AddFaceEdges(Face face, RegionXY region)
        {
            foreach (EdgeArray loop in face.EdgeLoops)
            {
                foreach (Edge edge in loop)
                {
                    var pts = edge.Tessellate();
                    if (pts == null) continue;
                    for (var i = 0; i + 1 < pts.Count; i++)
                        region.AddEdge(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y);
                }
            }
        }

        private static PatternSource ResolvePattern(Document doc, Floor floor, Face face, ElementId fallbackMaterial,
            PatternReader patterns, FloorHatchInput input)
        {
            var matId = GetFaceMaterial(doc, floor, face, fallbackMaterial);
            var src = patterns.ForMaterial(matId);
            if (src.IsDefaultGrid) input.UsedDefaultGrid = true;
            if (src.IsDraftingScaled) input.UsedDraftingPattern = true;
            AddUnique(input.DependencyUniqueIds, src.MaterialUniqueId);
            AddUnique(input.DependencyUniqueIds, src.PatternUniqueId);
            AddUnique(input.PatternDescriptions, src.Description);
            return src;
        }

        private static void AddUnique(List<string> list, string value)
        {
            if (!string.IsNullOrEmpty(value) && !list.Contains(value)) list.Add(value);
        }

        /// <summary>
        /// Material shown on the face: painted material &gt; face material &gt; type's top layer &gt; Floors category.
        /// </summary>
        public static ElementId GetFaceMaterial(Document doc, Element floor, Face face, ElementId fallback)
        {
            try
            {
                if (doc.IsPainted(floor.Id, face))
                {
                    var painted = doc.GetPaintedMaterial(floor.Id, face);
                    if (IsValid(painted)) return painted;
                }
            }
            catch
            {
                // not paintable / not supported
            }

            try
            {
                var m = face.MaterialElementId;
                if (IsValid(m) && doc.GetElement(m) is Material) return m;
            }
            catch
            {
                // ignore
            }

            if (IsValid(fallback)) return fallback;

            try
            {
                var catMat = floor.Category?.Material;
                if (catMat != null) return catMat.Id;
            }
            catch
            {
                // ignore
            }

            return ElementId.InvalidElementId;
        }

        /// <summary>
        /// First layer from the top (layer 0 is the top/exterior side of a floor) down to the first core layer
        /// that has a valid material. Variable-thickness layers do not change which layer is on top.
        /// </summary>
        public static ElementId GetTypeTopMaterial(Floor floor)
        {
            try
            {
                var type = floor.Document.GetElement(floor.GetTypeId()) as HostObjAttributes;
                var cs = type?.GetCompoundStructure();
                if (cs == null) return ElementId.InvalidElementId;
                var layers = cs.GetLayers();
                if (layers == null || layers.Count == 0) return ElementId.InvalidElementId;

                var lastIndex = cs.GetFirstCoreLayerIndex();
                if (lastIndex < 0 || lastIndex >= layers.Count) lastIndex = layers.Count - 1;

                for (var i = 0; i <= lastIndex; i++)
                {
                    var id = layers[i].MaterialId;
                    if (IsValid(id)) return id;
                }
            }
            catch
            {
                // ignore
            }

            return ElementId.InvalidElementId;
        }

        private static bool IsValid(ElementId id) => id != null && id != ElementId.InvalidElementId;

        // ---------------------------------------------------------------- signature

        private static string ComputeSignature(Floor floor, FloorHatchInput input)
        {
            var sb = new StringBuilder(4096);
            sb.Append("A").Append(FpfOptions.AlgorithmVersion)
              .Append("|O").Append(F(FpfOptions.SurfaceOffsetFeet))
              .Append("|S").Append(F(FpfOptions.DraftingPatternScale))
              .Append("|G").Append(F(FpfOptions.DefaultGridSpacingFeet))
              .Append("|X").Append(FpfOptions.MaxSegmentsPerFloor).Append(',').Append(FpfOptions.SegmentsPerDirectShape);

            try
            {
                sb.Append("|PC").Append(floor.CreatedPhaseId).Append("|PD").Append(floor.DemolishedPhaseId);
            }
            catch
            {
                // ignore
            }

            try { sb.Append("|W").Append(floor.WorksetId.IntegerValue); } catch { /* ignore */ }

            foreach (var p in input.Pieces)
            {
                sb.Append("\n").Append(p.FromTriangulation ? 'T' : 'P')
                  .Append(F(p.Nx)).Append(',').Append(F(p.Ny)).Append(',').Append(F(p.Nz)).Append(',')
                  .Append(F(p.Nx * p.Ox + p.Ny * p.Oy + p.Nz * p.Oz));
                var e = p.Region.RawEdges;
                for (var i = 0; i < e.Count; i++)
                    sb.Append(i % 4 == 0 ? ';' : ',').Append(F(e[i]));
                sb.Append("|").Append(p.Pattern?.SignatureText ?? "-");
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static string F(double v) => PatternReader.F(v);

        // ---------------------------------------------------------------- build

        public static FloorHatchResult Build(FloorHatchInput input, double shortCurveTolerance)
        {
            var result = new FloorHatchResult();
            if (input == null) return result;

            var minLen = Math.Max(shortCurveTolerance, 1.0e-6);
            var dotLen = Math.Max(FpfOptions.DotLengthFeet, minLen * 1.5);
            var intervals = new List<double[]>();
            var dashed = new List<double[]>();

            foreach (var piece in input.Pieces)
            {
                if (piece.Pattern == null) continue;
                var r = piece.Region;
                // lift offset along the face normal
                var offX = piece.Nx * FpfOptions.SurfaceOffsetFeet;
                var offY = piece.Ny * FpfOptions.SurfaceOffsetFeet;
                var offZ = piece.Nz * FpfOptions.SurfaceOffsetFeet;

                foreach (var g in piece.Pattern.Grids)
                {
                    var dx = Math.Cos(g.Angle);
                    var dy = Math.Sin(g.Angle);
                    var nx = -dy;
                    var ny = dx;

                    // Range of perpendicular distance covered by the region's bounding box.
                    double smin = double.MaxValue, smax = double.MinValue;
                    Span(r.MinX, r.MinY, g, nx, ny, ref smin, ref smax);
                    Span(r.MaxX, r.MinY, g, nx, ny, ref smin, ref smax);
                    Span(r.MaxX, r.MaxY, g, nx, ny, ref smin, ref smax);
                    Span(r.MinX, r.MaxY, g, nx, ny, ref smin, ref smax);

                    long kmin, kmax;
                    if (g.Offset < 1.0e-9)
                    {
                        if (smin > 0 || smax < 0) continue;
                        kmin = kmax = 0;
                    }
                    else
                    {
                        kmin = (long)Math.Ceiling(smin / g.Offset);
                        kmax = (long)Math.Floor(smax / g.Offset);
                    }

                    if (kmax - kmin + 1 > FpfOptions.MaxLinesPerGrid)
                    {
                        result.SkippedGrids++;
                        continue;
                    }

                    for (var k = kmin; k <= kmax; k++)
                    {
                        var px = g.OriginX + k * (g.Shift * dx + g.Offset * nx);
                        var py = g.OriginY + k * (g.Shift * dy + g.Offset * ny);

                        intervals.Clear();
                        intervals.AddRange(PolygonClipper.ClipLine(r, px, py, dx, dy));
                        if (intervals.Count == 0) continue;

                        dashed.Clear();
                        PolygonClipper.ApplyDashes(intervals, g.Dashes, dotLen, dashed);

                        foreach (var iv in dashed)
                        {
                            var ax = px + dx * iv[0];
                            var ay = py + dy * iv[0];
                            var bx = px + dx * iv[1];
                            var by = py + dy * iv[1];
                            var a = new XYZ(ax + offX, ay + offY, piece.ZAt(ax, ay) + offZ);
                            var b = new XYZ(bx + offX, by + offY, piece.ZAt(bx, by) + offZ);
                            if (a.DistanceTo(b) < minLen)
                            {
                                result.SkippedShort++;
                                continue;
                            }

                            if (result.Segments.Count >= FpfOptions.MaxSegmentsPerFloor)
                            {
                                result.Truncated = true;
                                return result;
                            }

                            result.Segments.Add(new Segment3 { A = a, B = b });
                        }
                    }
                }
            }

            return result;
        }

        private static void Span(double x, double y, PatternGridDef g, double nx, double ny,
            ref double smin, ref double smax)
        {
            var s = (x - g.OriginX) * nx + (y - g.OriginY) * ny;
            if (s < smin) smin = s;
            if (s > smax) smax = s;
        }
    }
}
