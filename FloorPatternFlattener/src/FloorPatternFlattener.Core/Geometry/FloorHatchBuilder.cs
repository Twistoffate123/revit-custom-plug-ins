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

        /// <summary>The face's own 3D edge loops (outer first), used by Surface mode; null if unavailable.</summary>
        public IList<CurveLoop> Loops { get; set; }

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

        /// <summary>Plan holes (shafts, face openings, sketch inner loops) subtracted after faces are unioned.</summary>
        public List<RegionXY> Holes { get; } = new List<RegionXY>();

        /// <summary>Set when no upward face could be used. The floor is skipped and this text is shown.</summary>
        public string FailureReason { get; set; }

        /// <summary>Non-fatal notes (sketch fallback, unreadable volume, faces that threw).</summary>
        public List<string> Notes { get; } = new List<string>();

        public int SolidsSeen { get; set; }
        public int FacesSeen { get; set; }
        public int FacesFailed { get; set; }
        public bool UsedSketchFallback { get; set; }
    }

    public sealed class Segment3
    {
        public XYZ A;
        public XYZ B;

        /// <summary>Foreground / background surface pattern (and its colour) this segment belongs to.</summary>
        public PatternLayer Layer;
    }

    public sealed class FloorHatchResult
    {
        public List<Segment3> Segments { get; } = new List<Segment3>();
        public int SkippedGrids { get; set; }
        public int SkippedShort { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Builds the flat-pattern hatch for a floor:
    ///  1. find all upward-facing top faces (normal.Z &gt; tolerance), incl. every slope of a multi-slope floor;
    ///  2. project each face's edge loops (outer + openings) to XY;
    ///  3. generate the true fill-pattern lines in PLAN, aligned to the project internal origin, for the
    ///     material's background AND foreground surface pattern (each keeps its own colour);
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
            string firstFaceError = null;

            try
            {
                var solids = new List<Solid>();
                CollectSolids(TryGeometry(floor, false), solids, 0);
                if (solids.Count == 0)
                {
                    CollectSolids(TryGeometry(floor, true), solids, 0);
                    if (solids.Count > 0)
                        input.Notes.Add("model geometry was only available with non-visible objects included");
                }

                foreach (var solid in solids)
                    ReadSolid(doc, floor, solid, fallbackMaterial, patterns, input, ref firstFaceError);

                ReadOpenings(floor, input);
                if (input.Pieces.Count == 0)
                    TrySketchFallback(floor, fallbackMaterial, patterns, input);
                else
                    DropBuried(input.Pieces);

                DiscardHolesThatCoverTheFloor(input);
            }
            catch (Exception ex)
            {
                input.FailureReason = "could not read floor geometry: " + ex.Message;
            }

            if (input.FacesFailed > 0)
                input.Notes.Add(input.FacesFailed + " face(s) could not be read (" + (firstFaceError ?? "unknown") + ")");

            if (input.Pieces.Count == 0 && string.IsNullOrEmpty(input.FailureReason))
            {
                var why = new StringBuilder();
                why.Append("no upward-facing top face (solids ").Append(input.SolidsSeen)
                    .Append(", faces ").Append(input.FacesSeen).Append(")");
                foreach (var n in input.Notes) why.Append("; ").Append(n);
                input.FailureReason = why.ToString();
            }

            input.Signature = ComputeSignature(floor, input);
            return input;
        }

        private static GeometryElement TryGeometry(Floor floor, bool includeNonVisible)
        {
            try
            {
                return floor.get_Geometry(new Options
                {
                    ComputeReferences = includeNonVisible,
                    IncludeNonVisibleObjects = includeNonVisible,
                    DetailLevel = ViewDetailLevel.Fine
                });
            }
            catch
            {
                return null;
            }
        }

        private static void CollectSolids(GeometryElement geom, List<Solid> solids, int depth)
        {
            if (geom == null || depth > 5) return;
            foreach (GeometryObject obj in geom)
            {
                try
                {
                    if (obj is Solid s)
                    {
                        if (s.Faces != null && s.Faces.Size > 0) solids.Add(s);
                    }
                    else if (obj is GeometryInstance gi)
                    {
                        GeometryElement inst = null;
                        try { inst = gi.GetInstanceGeometry(); } catch { inst = null; }
                        if (inst != null)
                        {
                            CollectSolids(inst, solids, depth + 1);
                            continue;
                        }
                        var sym = gi.GetSymbolGeometry();
                        var t = gi.Transform;
                        if (sym != null && t != null && !t.IsIdentity)
                        {
                            var local = new List<Solid>();
                            CollectSolids(sym, local, depth + 1);
                            foreach (var sol in local)
                            {
                                try { solids.Add(SolidUtils.CreateTransformed(sol, t)); }
                                catch { solids.Add(sol); }
                            }
                        }
                        else CollectSolids(sym, solids, depth + 1);
                    }
                }
                catch
                {
                    // one bad object must not drop the rest of the floor
                }
            }
        }

        private static void ReadSolid(Document doc, Floor floor, Solid solid, ElementId fallbackMaterial,
            PatternReader patterns, FloorHatchInput input, ref string firstFaceError)
        {
            // Do not reject Volume <= 0. Shape-edited floors with openings often come back inverted
            // (negative volume) or with a volume that throws; the faces are still the top surface.
            if (solid == null || solid.Faces == null || solid.Faces.Size == 0) return;
            input.SolidsSeen++;

            var flip = false;
            try { flip = solid.Volume < 0; }
            catch (Exception ex) { input.Notes.Add("a solid's volume could not be read (" + ex.Message + "); its faces were still used"); }

            var before = input.Pieces.Count;
            ReadFaces(doc, floor, solid, flip, fallbackMaterial, patterns, input, ref firstFaceError);
            if (input.Pieces.Count == before)
                ReadFaces(doc, floor, solid, !flip, fallbackMaterial, patterns, input, ref firstFaceError);
        }

        private static void ReadFaces(Document doc, Floor floor, Solid solid, bool flip, ElementId fallbackMaterial,
            PatternReader patterns, FloorHatchInput input, ref string firstFaceError)
        {
            foreach (Face face in solid.Faces)
            {
                input.FacesSeen++;
                try
                {
                    if (face is PlanarFace pf)
                    {
                        var n = pf.FaceNormal;
                        if (flip) n = n.Negate();
                        if (n.Z <= FpfOptions.MinUpwardNormalZ) continue;

                        var piece = new TopPiece
                        {
                            Ox = pf.Origin.X, Oy = pf.Origin.Y, Oz = pf.Origin.Z,
                            Nx = n.X, Ny = n.Y, Nz = n.Z
                        };
                        // Every loop: outer boundary and inner loops from shafts / face openings / sketch holes.
                        AddFaceEdges(face, piece.Region);
                        if (piece.Region.IsEmpty) continue;
                        piece.Pattern = ResolvePattern(doc, floor, face, fallbackMaterial, patterns, input);
                        input.Pieces.Add(piece);
                    }
                    else
                    {
                        // Warped / shape-edited top. Do not sample the UV-box centre: with an opening that point
                        // is often off the face, ComputeNormal throws, and the whole floor used to be skipped.
                        Mesh mesh = null;
                        try { mesh = face.Triangulate(0.5); } catch { mesh = null; }
                        if (mesh == null || mesh.NumTriangles == 0)
                            try { mesh = face.Triangulate(); } catch { mesh = null; }
                        if (mesh == null || mesh.NumTriangles == 0) continue;

                        PatternSource pattern = null;
                        var added = 0;
                        for (var i = 0; i < mesh.NumTriangles; i++)
                        {
                            var tri = mesh.get_Triangle(i);
                            var a = tri.get_Vertex(0);
                            var b = tri.get_Vertex(1);
                            var c = tri.get_Vertex(2);
                            var nrm = (b - a).CrossProduct(c - a);
                            if (nrm.GetLength() < 1.0e-12) continue;
                            nrm = nrm.Normalize();
                            if (flip) nrm = nrm.Negate();
                            if (nrm.Z <= FpfOptions.MinUpwardNormalZ) continue;

                            if (pattern == null)
                                pattern = ResolvePattern(doc, floor, face, fallbackMaterial, patterns, input);
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
                            if (piece.Region.IsEmpty) continue;
                            input.Pieces.Add(piece);
                            added++;
                        }
                        if (added > 0) input.NonPlanarFaces++;
                    }
                }
                catch (Exception ex)
                {
                    input.FacesFailed++;
                    if (firstFaceError == null) firstFaceError = ex.Message;
                }
            }
        }

        /// <summary>Shafts and face openings whose cut might not already be a hole in the solid.</summary>
        private static void ReadOpenings(Floor floor, FloorHatchInput input)
        {
            try
            {
                var ids = floor.FindInserts(true, false, false, false);
                if (ids == null) return;
                foreach (var id in ids)
                {
                    if (!(floor.Document.GetElement(id) is Opening opening)) continue;
                    var region = new RegionXY();
                    try
                    {
                        var curves = opening.BoundaryCurves;
                        if (curves != null)
                            foreach (Curve c in curves) AddCurve(region, c);
                    }
                    catch { /* rectangular openings expose BoundaryRect instead */ }

                    if (region.IsEmpty)
                    {
                        try
                        {
                            var rect = opening.BoundaryRect;
                            if (rect != null && rect.Count >= 2)
                            {
                                var x0 = Math.Min(rect[0].X, rect[1].X);
                                var x1 = Math.Max(rect[0].X, rect[1].X);
                                var y0 = Math.Min(rect[0].Y, rect[1].Y);
                                var y1 = Math.Max(rect[0].Y, rect[1].Y);
                                region.AddEdge(x0, y0, x1, y0);
                                region.AddEdge(x1, y0, x1, y1);
                                region.AddEdge(x1, y1, x0, y1);
                                region.AddEdge(x0, y1, x0, y0);
                            }
                        }
                        catch { /* ignore this opening */ }
                    }
                    if (!region.IsEmpty) input.Holes.Add(region);
                }
            }
            catch (Exception ex)
            {
                input.Notes.Add("hosted openings could not be read (" + ex.Message + ")");
            }
        }

        /// <summary>
        /// Last resort when the solid has no usable top: the sketch profile, flat at the top of the bounding box.
        /// Inner sketch loops stay holes via even-odd on the same region.
        /// </summary>
        private static void TrySketchFallback(Floor floor, ElementId fallbackMaterial, PatternReader patterns, FloorHatchInput input)
        {
            try
            {
                var sketch = floor.Document.GetElement(floor.SketchId) as Sketch;
                if (sketch?.Profile == null) return;
                var piece = new TopPiece { Nx = 0, Ny = 0, Nz = 1 };
                foreach (CurveArray loop in sketch.Profile)
                    foreach (Curve c in loop)
                        AddCurve(piece.Region, c);
                if (piece.Region.IsEmpty) return;

                BoundingBoxXYZ bb = null;
                try { bb = floor.get_BoundingBox(null); } catch { bb = null; }
                if (bb != null)
                {
                    const double pad = 5.0;
                    if (piece.Region.MaxX < bb.Min.X - pad || piece.Region.MinX > bb.Max.X + pad ||
                        piece.Region.MaxY < bb.Min.Y - pad || piece.Region.MinY > bb.Max.Y + pad)
                    {
                        input.Notes.Add("the floor sketch was not in model coordinates, so it was not used");
                        return;
                    }
                    piece.Oz = bb.Max.Z;
                }
                piece.Pattern = ResolveFromMaterial(floor.Document, fallbackMaterial, patterns, input);
                input.Pieces.Add(piece);
                input.UsedSketchFallback = true;
                input.Notes.Add("no solid top faces were found; the sketch outline was flattened on a horizontal plane at the top of the floor. The slope is not followed.");
            }
            catch (Exception ex)
            {
                input.Notes.Add("floor sketch could not be used (" + ex.Message + ")");
            }
        }

        /// <summary>Drops faces buried under another top face (compound-layer tops), not neighbouring slopes.</summary>
        private static void DropBuried(List<TopPiece> pieces)
        {
            if (pieces.Count < 2) return;
            const double gap = 5.0 / 304.8;
            var drop = new bool[pieces.Count];
            var cx = new double[pieces.Count];
            var cy = new double[pieces.Count];
            var ok = new bool[pieces.Count];
            for (var i = 0; i < pieces.Count; i++)
                ok[i] = TryCentroid(pieces[i].Region, out cx[i], out cy[i]);

            for (var i = 0; i < pieces.Count; i++)
            {
                if (!ok[i]) continue;
                var zi = pieces[i].ZAt(cx[i], cy[i]);
                for (var j = 0; j < pieces.Count; j++)
                {
                    if (i == j) continue;
                    var rj = pieces[j].Region;
                    if (cx[i] < rj.MinX || cx[i] > rj.MaxX || cy[i] < rj.MinY || cy[i] > rj.MaxY) continue;
                    if (!PolygonClipper.ContainsPoint(rj, cx[i], cy[i])) continue;
                    if (pieces[j].ZAt(cx[i], cy[i]) > zi + gap)
                    {
                        drop[i] = true;
                        break;
                    }
                }
            }

            for (var i = pieces.Count - 1; i >= 0; i--)
                if (drop[i]) pieces.RemoveAt(i);
        }

        private static bool TryCentroid(RegionXY region, out double x, out double y)
        {
            x = (region.MinX + region.MaxX) * 0.5;
            y = (region.MinY + region.MaxY) * 0.5;
            if (PolygonClipper.ContainsPoint(region, x, y)) return true;
            var e = region.RawEdges;
            if (e.Count < 4) return false;
            double sx = 0, sy = 0;
            var n = 0;
            for (var i = 0; i < e.Count; i += 4)
            {
                sx += e[i];
                sy += e[i + 1];
                n++;
            }
            x = sx / n;
            y = sy / n;
            return PolygonClipper.ContainsPoint(region, x, y);
        }

        /// <summary>An opening curve set that is really the floor outline must not punch the whole hatch out.</summary>
        private static void DiscardHolesThatCoverTheFloor(FloorHatchInput input)
        {
            if (input.Holes.Count == 0 || input.Pieces.Count == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in input.Pieces)
            {
                if (p.Region.MinX < minX) minX = p.Region.MinX;
                if (p.Region.MinY < minY) minY = p.Region.MinY;
                if (p.Region.MaxX > maxX) maxX = p.Region.MaxX;
                if (p.Region.MaxY > maxY) maxY = p.Region.MaxY;
            }
            var area = Math.Max(0, maxX - minX) * Math.Max(0, maxY - minY);
            input.Holes.RemoveAll(h =>
            {
                var ha = Math.Max(0, h.MaxX - h.MinX) * Math.Max(0, h.MaxY - h.MinY);
                var covers = h.MinX <= minX + 0.05 && h.MinY <= minY + 0.05 &&
                             h.MaxX >= maxX - 0.05 && h.MaxY >= maxY - 0.05;
                return covers && ha >= area * 0.95;
            });
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

        private static void AddCurve(RegionXY region, Curve curve)
        {
            if (region == null || curve == null) return;
            IList<XYZ> pts = null;
            try { pts = curve.Tessellate(); } catch { pts = null; }
            if (pts == null) return;
            for (var i = 0; i + 1 < pts.Count; i++)
                region.AddEdge(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y);
        }

        private static PatternSource ResolveFromMaterial(Document doc, ElementId materialId, PatternReader patterns, FloorHatchInput input)
        {
            var src = patterns.ForMaterial(materialId);
            if (src.IsDefaultGrid) input.UsedDefaultGrid = true;
            if (src.IsDraftingScaled) input.UsedDraftingPattern = true;
            AddUnique(input.DependencyUniqueIds, src.MaterialUniqueId);
            foreach (var pid in src.PatternUniqueIds) AddUnique(input.DependencyUniqueIds, pid);
            AddUnique(input.PatternDescriptions, src.Description);
            return src;
        }

        private static PatternSource ResolvePattern(Document doc, Floor floor, Face face, ElementId fallbackMaterial,
            PatternReader patterns, FloorHatchInput input)
        {
            var matId = GetFaceMaterial(doc, floor, face, fallbackMaterial);
            return ResolveFromMaterial(doc, matId, patterns, input);
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
              .Append("|X").Append(FpfOptions.SegmentsPerDirectShape)
              .Append("|H").Append(input.Holes.Count)
              .Append(input.UsedSketchFallback ? "|SK" : "");

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

        public static FloorHatchResult Build(FloorHatchInput input, double shortCurveTolerance,
            PatternPlacement placement = null)
        {
            placement = placement ?? PatternPlacement.Identity;
            var result = new FloorHatchResult();
            if (input == null || input.Pieces.Count == 0) return result;

            var minLen = Math.Max(shortCurveTolerance, 1.0e-6);
            var dotLen = Math.Max(FpfOptions.DotLengthFeet, minLen * 1.5);

            // Faces that share a pattern are hatched together, so a running-bond dash continues across
            // triangles instead of being clipped (and dropped) on each one.
            var groups = new Dictionary<string, List<TopPiece>>();
            foreach (var p in input.Pieces)
            {
                if (p?.Pattern == null || p.Pattern.Layers.Count == 0) continue;
                var key = p.Pattern.SignatureText ?? string.Empty;
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<TopPiece>();
                list.Add(p);
            }

            foreach (var group in groups.Values)
                BuildGroup(group, input.Holes, placement, minLen, dotLen, result);

            var boundary = new List<Segment3>();
            AppendBoundary(input, minLen, boundary);
            TrimPatternOffBoundary(result, boundary, minLen);
            return result;
        }

        private static readonly PatternLayer EdgeLayer = new PatternLayer
        {
            Kind = PatternLayerKind.Edge,
            Description = "floor edge"
        };

        private static void BuildGroup(List<TopPiece> pieces, List<RegionXY> holes, PatternPlacement placement,
            double minLen, double dotLen, FloorHatchResult result)
        {
            var pattern = pieces[0].Pattern;
            var intervals = new List<double[]>();
            var covers = new List<Cover>();
            var dashed = new List<double[]>();

            foreach (var layer in pattern.Layers)
            foreach (var g0 in layer.Grids)
            {
                var g = placement.Apply(g0);
                var dx = Math.Cos(g.Angle);
                var dy = Math.Sin(g.Angle);
                var nx = -dy;
                var ny = dx;
                var buckets = new PieceBuckets(pieces, nx, ny, g.OriginX, g.OriginY, g.Offset);

                double smin = double.MaxValue, smax = double.MinValue;
                foreach (var p in pieces)
                    SpanRegion(p.Region, g, nx, ny, ref smin, ref smax);
                if (smin > smax) continue;

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
                if (kmax < kmin) continue;
                try { checked { var span = kmax - kmin; } }
                catch (OverflowException)
                {
                    result.SkippedGrids++;
                    result.Notes.Add("a pattern grid has degenerate spacing and was skipped");
                    continue;
                }

                for (var k = kmin; k <= kmax; k++)
                {
                    var lineS = g.Offset < 1.0e-9 ? 0 : k * g.Offset;
                    var px = g.OriginX + k * (g.Shift * dx + g.Offset * nx);
                    var py = g.OriginY + k * (g.Shift * dy + g.Offset * ny);

                    intervals.Clear();
                    covers.Clear();
                    buckets.Collect(lineS, (piece) =>
                    {
                        var clipped = PolygonClipper.ClipLine(piece.Region, px, py, dx, dy);
                        for (var i = 0; i < clipped.Count; i++)
                        {
                            intervals.Add(clipped[i]);
                            covers.Add(new Cover { T0 = clipped[i][0], T1 = clipped[i][1], Piece = piece });
                        }
                    });
                    if (intervals.Count == 0) continue;

                    var merged = PolygonClipper.UnionIntervals(intervals, FpfOptions.MeshCrackBridgeFeet);
                    if (holes != null)
                    {
                        for (var h = 0; h < holes.Count; h++)
                        {
                            var cut = PolygonClipper.ClipLine(holes[h], px, py, dx, dy);
                            if (cut.Count > 0) merged = PolygonClipper.SubtractIntervals(merged, cut);
                        }
                    }
                    if (merged.Count == 0) continue;

                    dashed.Clear();
                    PolygonClipper.ApplyDashes(merged, g.Dashes, dotLen, dashed);
                    covers.Sort((a, b) => a.T0.CompareTo(b.T0));
                    for (var d = 0; d < dashed.Count; d++)
                        EmitDash(result, layer, dashed[d][0], dashed[d][1], covers, px, py, dx, dy, minLen);
                }
            }
        }

        private sealed class Cover
        {
            public double T0, T1;
            public TopPiece Piece;
        }

        /// <summary>Pieces indexed by signed distance so each hatch line only tests faces it can cross.</summary>
        private sealed class PieceBuckets
        {
            private readonly double _cell;
            private readonly Dictionary<long, List<TopPiece>> _cells = new Dictionary<long, List<TopPiece>>();
            private readonly List<TopPiece> _wide = new List<TopPiece>();

            public PieceBuckets(List<TopPiece> pieces, double nx, double ny, double ox, double oy, double offset)
            {
                _cell = Math.Max(Math.Abs(offset) * 4.0, 1.0);
                foreach (var p in pieces)
                {
                    if (p?.Region == null || p.Region.IsEmpty) continue;
                    double smin = double.MaxValue, smax = double.MinValue;
                    var g = new PatternGridDef { OriginX = ox, OriginY = oy };
                    SpanRegion(p.Region, g, nx, ny, ref smin, ref smax);
                    if (smin > smax || double.IsInfinity(smin) || double.IsInfinity(smax)) continue;
                    var c0 = (long)Math.Floor(smin / _cell);
                    var c1 = (long)Math.Floor(smax / _cell);
                    if (c1 < c0 || c1 - c0 > 1000000) { _wide.Add(p); continue; }
                    for (var c = c0; c <= c1; c++)
                    {
                        if (!_cells.TryGetValue(c, out var list))
                            _cells[c] = list = new List<TopPiece>();
                        list.Add(p);
                    }
                }
            }

            public void Collect(double s, Action<TopPiece> visit)
            {
                for (var i = 0; i < _wide.Count; i++) visit(_wide[i]);
                var key = (long)Math.Floor(s / _cell);
                if (!_cells.TryGetValue(key, out var list)) return;
                for (var i = 0; i < list.Count; i++) visit(list[i]);
            }
        }

        private static void EmitDash(FloorHatchResult result, PatternLayer layer, double t0, double t1,
            List<Cover> covers, double px, double py, double dx, double dy, double minLen)
        {
            if (t1 - t0 <= FpfOptions.GeometryTolerance) return;
            var events = new List<double> { t0, t1 };
            for (var i = 0; i < covers.Count; i++)
            {
                var c = covers[i];
                if (c.T1 <= t0 || c.T0 >= t1) continue;
                events.Add(c.T0 < t0 ? t0 : c.T0);
                events.Add(c.T1 > t1 ? t1 : c.T1);
            }
            events.Sort();

            var poly = new List<XYZ>();
            TopPiece prev = null;
            var bridge = FpfOptions.MeshCrackBridgeFeet;
            for (var i = 0; i + 1 < events.Count; i++)
            {
                var a = events[i];
                var b = events[i + 1];
                if (b - a < 1.0e-10) continue;
                var mid = (a + b) * 0.5;
                TopPiece best = null;
                var bestZ = double.NegativeInfinity;
                for (var c = 0; c < covers.Count; c++)
                {
                    var cv = covers[c];
                    if (cv.T0 > mid) break;
                    if (mid > cv.T1) continue;
                    var z = cv.Piece.ZAt(px + dx * mid, py + dy * mid);
                    if (z > bestZ) { bestZ = z; best = cv.Piece; }
                }
                if (best == null && prev != null && (b - a) <= bridge) best = prev;
                if (best == null)
                {
                    Flush(result, layer, poly, minLen, null);
                    poly.Clear();
                    prev = null;
                    continue;
                }
                if (poly.Count == 0) poly.Add(Lift(a, best, px, py, dx, dy));
                poly.Add(Lift(b, best, px, py, dx, dy));
                prev = best;
            }
            Flush(result, layer, poly, minLen, null);
        }

        private static XYZ Lift(double t, TopPiece piece, double px, double py, double dx, double dy)
        {
            var x = px + dx * t;
            var y = py + dy * t;
            var z = piece.ZAt(x, y);
            var o = FpfOptions.SurfaceOffsetFeet;
            return new XYZ(x + piece.Nx * o, y + piece.Ny * o, z + piece.Nz * o);
        }

        /// <summary>
        /// Emits the longest chords that stay within the drape tolerance. Fragments shorter than Revit's
        /// minimum curve length are merged into the neighbour or lengthened, not dropped.
        /// </summary>
        private static void Flush(FloorHatchResult result, PatternLayer layer, List<XYZ> pts, double minLen, List<Segment3> target)
        {
            if (pts == null || pts.Count < 2) return;
            var dest = target ?? result.Segments;
            var tol = FpfOptions.DrapeToleranceFeet;
            var s = 0;
            while (s < pts.Count - 1)
            {
                var e = s + 1;
                while (e < pts.Count - 1)
                {
                    var n = e + 1;
                    var needLen = pts[s].DistanceTo(pts[e]) < minLen;
                    if (needLen || MaxDev(pts, s, n) <= tol) e = n;
                    else break;
                }
                var a = pts[s];
                var b = pts[e];
                var len = a.DistanceTo(b);
                if (len < minLen)
                {
                    if (len < 1.0e-9)
                    {
                        if (target == null) result.SkippedShort++;
                    }
                    else
                    {
                        b = a + (b - a).Normalize() * minLen;
                        dest.Add(new Segment3 { A = a, B = b, Layer = layer });
                    }
                }
                else dest.Add(new Segment3 { A = a, B = b, Layer = layer });
                s = e;
            }
        }

        private static double MaxDev(List<XYZ> pts, int s, int e)
        {
            var ab = pts[e] - pts[s];
            var len2 = ab.DotProduct(ab);
            if (len2 < 1.0e-18) return 0;
            double max = 0;
            for (var i = s + 1; i < e; i++)
            {
                var ap = pts[i] - pts[s];
                var t = ap.DotProduct(ab) / len2;
                if (t < 0) t = 0;
                else if (t > 1) t = 1;
                var dist = (ap - ab * t).GetLength();
                if (dist > max) max = dist;
            }
            return max;
        }

        /// <summary>
        /// Outer silhouette plus opening loops, draped on the same planes as the hatch.
        /// Shared triangle edges cancel, so fold lines inside the slab are not drawn as a boundary.
        /// </summary>
        private static void AppendBoundary(FloorHatchInput input, double minLen, List<Segment3> target)
        {
            var weld = FpfOptions.MeshCrackBridgeFeet;
            var edges = new List<BEdge>();
            foreach (var p in input.Pieces)
            {
                var raw = p?.Region?.RawEdges;
                if (raw == null) continue;
                for (var i = 0; i < raw.Count; i += 4)
                    edges.Add(new BEdge { X1 = raw[i], Y1 = raw[i + 1], X2 = raw[i + 2], Y2 = raw[i + 3], Piece = p });
            }
            if (edges.Count == 0) return;

            var idOf = new Dictionary<WeldKey, int>();
            var vx = new List<double>();
            var vy = new List<double>();
            int Vid(double x, double y)
            {
                var key = new WeldKey { X = (long)Math.Round(x / weld), Y = (long)Math.Round(y / weld) };
                if (idOf.TryGetValue(key, out var id)) return id;
                id = vx.Count;
                idOf[key] = id;
                vx.Add(x);
                vy.Add(y);
                return id;
            }

            foreach (var e in edges)
            {
                e.A = Vid(e.X1, e.Y1);
                e.B = Vid(e.X2, e.Y2);
            }

            var grid = new Dictionary<WeldKey, List<int>>();
            var cell = Math.Max(weld * 8.0, 1.0);
            for (var i = 0; i < vx.Count; i++)
            {
                var ck = new WeldKey { X = (long)Math.Floor(vx[i] / cell), Y = (long)Math.Floor(vy[i] / cell) };
                if (!grid.TryGetValue(ck, out var bucket)) grid[ck] = bucket = new List<int>();
                bucket.Add(i);
            }

            var micro = new List<BEdge>();
            foreach (var e in edges)
            {
                if (e.A == e.B) continue;
                var dx = e.X2 - e.X1;
                var dy = e.Y2 - e.Y1;
                var len2 = dx * dx + dy * dy;
                if (len2 < 1.0e-18) continue;

                var ts = new List<double> { 0, 1 };
                var ids = new List<int> { e.A, e.B };
                var c0x = (long)Math.Floor(Math.Min(e.X1, e.X2) / cell) - 1;
                var c1x = (long)Math.Floor(Math.Max(e.X1, e.X2) / cell) + 1;
                var c0y = (long)Math.Floor(Math.Min(e.Y1, e.Y2) / cell) - 1;
                var c1y = (long)Math.Floor(Math.Max(e.Y1, e.Y2) / cell) + 1;
                if (c1x - c0x <= 10000 && c1y - c0y <= 10000)
                {
                    for (var cx = c0x; cx <= c1x; cx++)
                    for (var cy = c0y; cy <= c1y; cy++)
                    {
                        if (!grid.TryGetValue(new WeldKey { X = cx, Y = cy }, out var vs)) continue;
                        for (var vi = 0; vi < vs.Count; vi++)
                        {
                            var v = vs[vi];
                            if (v == e.A || v == e.B) continue;
                            var t = ((vx[v] - e.X1) * dx + (vy[v] - e.Y1) * dy) / len2;
                            if (t <= 1.0e-4 || t >= 1.0 - 1.0e-4) continue;
                            var px = e.X1 + t * dx;
                            var py = e.Y1 + t * dy;
                            var ex = px - vx[v];
                            var ey = py - vy[v];
                            if (ex * ex + ey * ey <= weld * weld)
                            {
                                ts.Add(t);
                                ids.Add(v);
                            }
                        }
                    }
                }

                var order = new int[ts.Count];
                for (var i = 0; i < order.Length; i++) order[i] = i;
                Array.Sort(order, (i, j) => ts[i].CompareTo(ts[j]));
                var prevT = -1.0;
                var prevId = -1;
                for (var oi = 0; oi < order.Length; oi++)
                {
                    var t = ts[order[oi]];
                    var id = ids[order[oi]];
                    if (prevId >= 0 && t - prevT > 1.0e-6 && id != prevId)
                    {
                        micro.Add(new BEdge
                        {
                            A = prevId, B = id, Piece = e.Piece,
                            X1 = vx[prevId], Y1 = vy[prevId], X2 = vx[id], Y2 = vy[id]
                        });
                    }
                    if (prevId < 0 || t - prevT > 1.0e-6)
                    {
                        prevT = t;
                        prevId = id;
                    }
                }
            }

            var count = new Dictionary<PairKey, int>();
            var sample = new Dictionary<PairKey, BEdge>();
            foreach (var e in micro)
            {
                if (e.A == e.B) continue;
                var key = new PairKey(e.A, e.B);
                count.TryGetValue(key, out var n);
                count[key] = n + 1;
                if (n == 0) sample[key] = e;
            }

            var boundary = new List<BEdge>();
            foreach (var kv in count)
                if (kv.Value == 1) boundary.Add(sample[kv.Key]);

            AddUncoveredHoles(input, boundary);
            ChainBoundary(boundary, minLen, target);
        }

        /// <summary>Opening loops that the solid did not already leave as a single silhouette edge.</summary>
        private static void AddUncoveredHoles(FloorHatchInput input, List<BEdge> boundary)
        {
            if (input.Holes == null) return;
            var tol = FpfOptions.BoundaryOverlapFeet;
            foreach (var hole in input.Holes)
            {
                var raw = hole?.RawEdges;
                if (raw == null) continue;
                for (var i = 0; i < raw.Count; i += 4)
                {
                    var x1 = raw[i];
                    var y1 = raw[i + 1];
                    var x2 = raw[i + 2];
                    var y2 = raw[i + 3];
                    var covered = false;
                    for (var b = 0; b < boundary.Count; b++)
                    {
                        var e = boundary[b];
                        if (PolygonClipper.CollinearOverlap(x1, y1, x2, y2, e.X1, e.Y1, e.X2, e.Y2, tol, out var lo, out var hi))
                        {
                            var len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                            if (len > 1.0e-9 && hi - lo > len * 0.5) { covered = true; break; }
                        }
                    }
                    if (covered) continue;
                    var piece = PieceNearest(input.Pieces, (x1 + x2) * 0.5, (y1 + y2) * 0.5);
                    if (piece == null) continue;
                    boundary.Add(new BEdge { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Piece = piece, A = -1, B = -2 - i });
                }
            }
        }

        private static TopPiece PieceNearest(List<TopPiece> pieces, double x, double y)
        {
            TopPiece best = null;
            var bestD = double.MaxValue;
            var bestZ = double.NegativeInfinity;
            for (var i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (p?.Region == null) continue;
                var d = DistToRegion(p.Region, x, y);
                var z = p.ZAt(x, y);
                if (d < bestD - 1.0e-6 || (Math.Abs(d - bestD) <= 1.0e-6 && z > bestZ))
                {
                    bestD = d;
                    bestZ = z;
                    best = p;
                }
            }
            return best;
        }

        private static double DistToRegion(RegionXY region, double x, double y)
        {
            if (PolygonClipper.ContainsPoint(region, x, y)) return 0;
            var e = region.RawEdges;
            var best = double.MaxValue;
            for (var i = 0; i < e.Count; i += 4)
            {
                var d = DistPointSeg(x, y, e[i], e[i + 1], e[i + 2], e[i + 3]);
                if (d < best) best = d;
            }
            return best;
        }

        private static double DistPointSeg(double px, double py, double ax, double ay, double bx, double by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var len2 = dx * dx + dy * dy;
            double t = 0;
            if (len2 > 1.0e-18)
            {
                t = ((px - ax) * dx + (py - ay) * dy) / len2;
                if (t < 0) t = 0;
                else if (t > 1) t = 1;
            }
            var ex = ax + t * dx - px;
            var ey = ay + t * dy - py;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static void ChainBoundary(List<BEdge> edges, double minLen, List<Segment3> target)
        {
            if (edges.Count == 0) return;
            // Hole edges added with negative ids are not chained through the weld graph; emit them directly.
            var linked = new List<BEdge>();
            foreach (var e in edges)
            {
                if (e.A < 0 || e.B < 0)
                {
                    var piece = e.Piece;
                    var pts = new List<XYZ>
                    {
                        LiftXY(e.X1, e.Y1, piece),
                        LiftXY(e.X2, e.Y2, piece)
                    };
                    Flush(null, EdgeLayer, pts, minLen, target);
                    continue;
                }
                linked.Add(e);
            }
            if (linked.Count == 0) return;

            var adj = new Dictionary<int, List<int>>();
            for (var i = 0; i < linked.Count; i++)
            {
                AddAdj(adj, linked[i].A, i);
                AddAdj(adj, linked[i].B, i);
            }
            var used = new bool[linked.Count];
            for (var i = 0; i < linked.Count; i++)
            {
                if (used[i]) continue;
                var forward = new List<XYZ>();
                Walk(linked, adj, used, i, linked[i].A, forward);
                if (forward.Count >= 2)
                    Flush(null, EdgeLayer, forward, minLen, target);
            }
        }

        private static void AddAdj(Dictionary<int, List<int>> adj, int vertex, int edge)
        {
            if (!adj.TryGetValue(vertex, out var list)) adj[vertex] = list = new List<int>();
            list.Add(edge);
        }

        /// <summary>Walks one direction, then the other, so an open chain is not cut in half.</summary>
        private static void Walk(List<BEdge> edges, Dictionary<int, List<int>> adj, bool[] used, int start, int fromVertex, List<XYZ> pts)
        {
            var guard = 0;
            var eidx = start;
            var prev = fromVertex;
            var backward = new List<XYZ>();
            while (eidx >= 0 && !used[eidx] && guard++ < edges.Count + 2)
            {
                used[eidx] = true;
                var e = edges[eidx];
                var forwardDir = e.A == prev;
                var x0 = forwardDir ? e.X1 : e.X2;
                var y0 = forwardDir ? e.Y1 : e.Y2;
                var x1 = forwardDir ? e.X2 : e.X1;
                var y1 = forwardDir ? e.Y2 : e.Y1;
                var next = forwardDir ? e.B : e.A;
                if (pts.Count == 0) pts.Add(LiftXY(x0, y0, e.Piece));
                pts.Add(LiftXY(x1, y1, e.Piece));
                prev = next;
                eidx = NextUnused(adj, used, next, eidx);
            }

            // Edges on the other side of the start vertex.
            if (!adj.TryGetValue(fromVertex, out var startLinks)) return;
            eidx = -1;
            for (var i = 0; i < startLinks.Count; i++)
                if (!used[startLinks[i]]) { eidx = startLinks[i]; break; }
            prev = fromVertex;
            guard = 0;
            while (eidx >= 0 && !used[eidx] && guard++ < edges.Count + 2)
            {
                used[eidx] = true;
                var e = edges[eidx];
                var forwardDir = e.A == prev;
                var x1 = forwardDir ? e.X2 : e.X1;
                var y1 = forwardDir ? e.Y2 : e.Y1;
                var next = forwardDir ? e.B : e.A;
                backward.Add(LiftXY(x1, y1, e.Piece));
                prev = next;
                eidx = NextUnused(adj, used, next, eidx);
            }
            if (backward.Count == 0) return;
            backward.Reverse();
            backward.AddRange(pts);
            pts.Clear();
            pts.AddRange(backward);
        }

        private static int NextUnused(Dictionary<int, List<int>> adj, bool[] used, int vertex, int current)
        {
            if (!adj.TryGetValue(vertex, out var list)) return -1;
            for (var i = 0; i < list.Count; i++)
                if (list[i] != current && !used[list[i]]) return list[i];
            return -1;
        }

        private static XYZ LiftXY(double x, double y, TopPiece piece)
        {
            var z = piece.ZAt(x, y);
            var o = FpfOptions.SurfaceOffsetFeet;
            return new XYZ(x + piece.Nx * o, y + piece.Ny * o, z + piece.Nz * o);
        }

        /// <summary>Cuts hatch strokes that run along the boundary so the heavy edge is what you see.</summary>
        private static void TrimPatternOffBoundary(FloorHatchResult result, List<Segment3> boundary, double minLen)
        {
            if (boundary == null || boundary.Count == 0) return;
            if (result.Segments.Count == 0)
            {
                result.Segments.AddRange(boundary);
                return;
            }

            var tol = FpfOptions.BoundaryOverlapFeet;
            var cell = 2.0;
            var grid = new Dictionary<WeldKey, List<int>>();
            for (var i = 0; i < boundary.Count; i++)
            {
                var s = boundary[i];
                var minX = Math.Min(s.A.X, s.B.X) - tol;
                var maxX = Math.Max(s.A.X, s.B.X) + tol;
                var minY = Math.Min(s.A.Y, s.B.Y) - tol;
                var maxY = Math.Max(s.A.Y, s.B.Y) + tol;
                var c0x = (long)Math.Floor(minX / cell);
                var c1x = (long)Math.Floor(maxX / cell);
                var c0y = (long)Math.Floor(minY / cell);
                var c1y = (long)Math.Floor(maxY / cell);
                for (var cx = c0x; cx <= c1x; cx++)
                for (var cy = c0y; cy <= c1y; cy++)
                {
                    var key = new WeldKey { X = cx, Y = cy };
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(i);
                }
            }

            var kept = new List<Segment3>(result.Segments.Count + boundary.Count);
            foreach (var seg in result.Segments)
            {
                if (seg?.A == null || seg.B == null) continue;
                var cuts = new List<double[]>();
                var minX = Math.Min(seg.A.X, seg.B.X) - tol;
                var maxX = Math.Max(seg.A.X, seg.B.X) + tol;
                var minY = Math.Min(seg.A.Y, seg.B.Y) - tol;
                var maxY = Math.Max(seg.A.Y, seg.B.Y) + tol;
                var seen = new HashSet<int>();
                var c0x = (long)Math.Floor(minX / cell);
                var c1x = (long)Math.Floor(maxX / cell);
                var c0y = (long)Math.Floor(minY / cell);
                var c1y = (long)Math.Floor(maxY / cell);
                for (var cx = c0x; cx <= c1x && cuts.Count < 64; cx++)
                for (var cy = c0y; cy <= c1y; cy++)
                {
                    if (!grid.TryGetValue(new WeldKey { X = cx, Y = cy }, out var list)) continue;
                    for (var li = 0; li < list.Count; li++)
                    {
                        var bi = list[li];
                        if (!seen.Add(bi)) continue;
                        var b = boundary[bi];
                        if (PolygonClipper.CollinearOverlap(
                                seg.A.X, seg.A.Y, seg.B.X, seg.B.Y,
                                b.A.X, b.A.Y, b.B.X, b.B.Y, tol, out var lo, out var hi))
                            cuts.Add(new[] { lo, hi });
                    }
                }

                if (cuts.Count == 0)
                {
                    kept.Add(seg);
                    continue;
                }

                var len = seg.A.DistanceTo(seg.B);
                var remain = PolygonClipper.SubtractIntervals(
                    new List<double[]> { new[] { 0, len } },
                    PolygonClipper.UnionIntervals(cuts, FpfOptions.MeshCrackBridgeFeet));
                if (len < 1.0e-9) continue;
                foreach (var iv in remain)
                {
                    if (iv[1] - iv[0] < minLen) continue;
                    var t0 = iv[0] / len;
                    var t1 = iv[1] / len;
                    kept.Add(new Segment3
                    {
                        A = new XYZ(
                            seg.A.X + (seg.B.X - seg.A.X) * t0,
                            seg.A.Y + (seg.B.Y - seg.A.Y) * t0,
                            seg.A.Z + (seg.B.Z - seg.A.Z) * t0),
                        B = new XYZ(
                            seg.A.X + (seg.B.X - seg.A.X) * t1,
                            seg.A.Y + (seg.B.Y - seg.A.Y) * t1,
                            seg.A.Z + (seg.B.Z - seg.A.Z) * t1),
                        Layer = seg.Layer
                    });
                }
            }
            kept.AddRange(boundary);
            result.Segments.Clear();
            result.Segments.AddRange(kept);
        }

        private struct WeldKey : IEquatable<WeldKey>
        {
            public long X, Y;
            public bool Equals(WeldKey other) => X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is WeldKey other && Equals(other);
            public override int GetHashCode() => unchecked((int)(X * 73856093L ^ Y * 19349663L));
        }

        private struct PairKey : IEquatable<PairKey>
        {
            public int A, B;
            public PairKey(int a, int b)
            {
                if (a < b) { A = a; B = b; }
                else { A = b; B = a; }
            }
            public bool Equals(PairKey other) => A == other.A && B == other.B;
            public override bool Equals(object obj) => obj is PairKey other && Equals(other);
            public override int GetHashCode() => unchecked(A * 73856093 ^ B * 19349663);
        }

        private sealed class BEdge
        {
            public int A, B;
            public double X1, Y1, X2, Y2;
            public TopPiece Piece;
        }

        private static void SpanRegion(RegionXY r, PatternGridDef g, double nx, double ny, ref double smin, ref double smax)
        {
            if (r == null) return;
            Span(r.MinX, r.MinY, g, nx, ny, ref smin, ref smax);
            Span(r.MaxX, r.MinY, g, nx, ny, ref smin, ref smax);
            Span(r.MaxX, r.MaxY, g, nx, ny, ref smin, ref smax);
            Span(r.MinX, r.MaxY, g, nx, ny, ref smin, ref smax);
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
