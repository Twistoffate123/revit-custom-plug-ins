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

        /// <summary>Plan angle (rad) of the face U-axis; used with material grid angle for live pattern orientation.</summary>
        public double FacePlanRotationRad { get; set; }

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

        /// <summary>Top-surface boundary from Revit face edge loops only (never triangle interior edges).</summary>
        public List<BoundaryEdge> BoundaryEdges { get; } = new List<BoundaryEdge>();

        /// <summary>Closed loops chained from each top face <see cref="Face.EdgeLoops"/> in edge order (edge authority).</summary>
        internal List<AuthorityBoundaryLoop> AuthorityBoundaryLoops { get; } = new List<AuthorityBoundaryLoop>();

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

        /// <summary>Edge runs with the same id are written into one DirectShape (straight side or one curved span).</summary>
        public int EdgeRun;
    }

    /// <summary>One boundary segment from a face's outer or inner edge loop, draped on that face.</summary>
    public sealed class BoundaryEdge
    {
        public double X1, Y1, X2, Y2;
        public TopPiece Piece;
    }

    internal sealed class AuthorityBoundaryLoop
    {
        public List<Pt2> Points { get; } = new List<Pt2>();
        public TopPiece Piece;
    }

    internal struct Pt2
    {
        public double X, Y;
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
                        AddSurfaceBoundaryEdges(face, piece, input);
                        piece.Pattern = ResolvePattern(doc, floor, face, fallbackMaterial, patterns, input);
                        piece.FacePlanRotationRad = PatternAngleMatcher.TryFacePlanRotationRad(face);
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
                        TopPiece drapePiece = null;
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
                            var faceRot = PatternAngleMatcher.TryFacePlanRotationRad(face);
                            var piece = new TopPiece
                            {
                                Ox = a.X, Oy = a.Y, Oz = a.Z,
                                Nx = nrm.X, Ny = nrm.Y, Nz = nrm.Z,
                                Pattern = pattern,
                                FromTriangulation = true,
                                FacePlanRotationRad = faceRot
                            };
                            piece.Region.AddEdge(a.X, a.Y, b.X, b.Y);
                            piece.Region.AddEdge(b.X, b.Y, c.X, c.Y);
                            piece.Region.AddEdge(c.X, c.Y, a.X, a.Y);
                            if (piece.Region.IsEmpty) continue;
                            input.Pieces.Add(piece);
                            if (drapePiece == null) drapePiece = piece;
                            added++;
                        }
                        if (added > 0)
                        {
                            input.NonPlanarFaces++;
                            if (drapePiece != null) AddSurfaceBoundaryEdges(face, drapePiece, input);
                        }
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
                var raw = piece.Region.RawEdges;
                for (var i = 0; i < raw.Count; i += 4)
                    input.BoundaryEdges.Add(new BoundaryEdge
                    {
                        X1 = raw[i], Y1 = raw[i + 1], X2 = raw[i + 2], Y2 = raw[i + 3], Piece = piece
                    });
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

        /// <summary>
        /// Floor-edge source geometry: only Revit <see cref="Face.EdgeLoops"/> (outer + opening loops).
        /// Triangulation folds, sub-element split lines, and edges shared by two top faces are not drawn here;
        /// shared edges are added once per adjacent face and dropped later when their welded count is not 1.
        /// </summary>
        private static void AddSurfaceBoundaryEdges(Face face, TopPiece piece, FloorHatchInput input)
        {
            if (face?.EdgeLoops == null || piece == null) return;
            var weld = FpfOptions.MeshCrackBridgeFeet;
            foreach (EdgeArray edgeLoop in face.EdgeLoops)
            {
                var poly = new List<Pt2>();
                foreach (Edge edge in edgeLoop)
                {
                    var pts = edge.Tessellate();
                    if (pts == null) continue;
                    AppendBoundaryLoopEdges(pts, piece, input);
                    for (var i = 0; i < pts.Count; i++)
                        AddAuthorityLoopPoint(poly, pts[i].X, pts[i].Y, weld);
                }
                if (poly.Count >= 3)
                    TryRegisterAuthorityLoop(input, poly, piece);
            }
        }

        private static void AddAuthorityLoopPoint(List<Pt2> poly, double x, double y, double weld)
        {
            if (poly.Count > 0)
            {
                var last = poly[poly.Count - 1];
                if (Math.Abs(last.X - x) <= weld && Math.Abs(last.Y - y) <= weld) return;
            }
            poly.Add(new Pt2 { X = x, Y = y });
        }

        private static void TryRegisterAuthorityLoop(FloorHatchInput input, List<Pt2> poly, TopPiece piece)
        {
            if (poly.Count < 3) return;
            var weld = FpfOptions.MeshCrackBridgeFeet;
            if (poly.Count == 3)
            {
                var area = Math.Abs((poly[1].X - poly[0].X) * (poly[2].Y - poly[0].Y) -
                                    (poly[2].X - poly[0].X) * (poly[1].Y - poly[0].Y)) * 0.5;
                if (area < 1.0) return;
            }
            if (poly.Count >= 3 &&
                Math.Abs(poly[0].X - poly[poly.Count - 1].X) <= weld &&
                Math.Abs(poly[0].Y - poly[poly.Count - 1].Y) <= weld)
                poly.RemoveAt(poly.Count - 1);
            var sig = AuthorityLoopSignature(poly, weld);
            foreach (var existing in input.AuthorityBoundaryLoops)
            {
                if (existing == null) continue;
                if (AuthorityLoopSignature(existing.Points, weld) == sig) return;
            }
            var loop = new AuthorityBoundaryLoop { Piece = piece };
            loop.Points.AddRange(poly);
            input.AuthorityBoundaryLoops.Add(loop);
        }

        private static string AuthorityLoopSignature(List<Pt2> poly, double weld)
        {
            var n = poly.Count;
            if (n == 0) return string.Empty;
            var best = string.Empty;
            for (var start = 0; start < n; start++)
            {
                var sb = new StringBuilder(n * 12);
                for (var i = 0; i < n; i++)
                {
                    var p = poly[(start + i) % n];
                    sb.Append((long)Math.Round(p.X / weld)).Append(',')
                        .Append((long)Math.Round(p.Y / weld)).Append(';');
                }
                var s = sb.ToString();
                var rev = new StringBuilder(n * 12);
                for (var i = n - 1; i >= 0; i--)
                {
                    var p = poly[(start + i) % n];
                    rev.Append((long)Math.Round(p.X / weld)).Append(',')
                        .Append((long)Math.Round(p.Y / weld)).Append(';');
                }
                var sr = rev.ToString();
                if (best.Length == 0 || string.CompareOrdinal(s, best) < 0) best = s;
                if (string.CompareOrdinal(sr, best) < 0) best = sr;
            }
            return best;
        }

        /// <summary>Merge collinear tessellation chords on one Revit edge only; never skip or dedupe across faces.</summary>
        private static void AppendBoundaryLoopEdges(IList<XYZ> pts, TopPiece piece, FloorHatchInput input)
        {
            if (pts == null || pts.Count < 2) return;
            var col = 0.5 / 304.8;
            var runStart = 0;
            for (var i = 1; i < pts.Count; i++)
            {
                var isEnd = i + 1 >= pts.Count;
                if (!isEnd && CollinearXY(pts[runStart], pts[i], pts[i + 1], col)) continue;
                var x1 = pts[runStart].X;
                var y1 = pts[runStart].Y;
                var x2 = pts[i].X;
                var y2 = pts[i].Y;
                if (Math.Abs(x1 - x2) < 1.0e-12 && Math.Abs(y1 - y2) < 1.0e-12)
                {
                    runStart = i;
                    continue;
                }
                input.BoundaryEdges.Add(new BoundaryEdge { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Piece = piece });
                runStart = i;
            }
        }

        private static bool CollinearXY(XYZ a, XYZ b, XYZ c, double tol)
        {
            var vx = c.X - a.X;
            var vy = c.Y - a.Y;
            var len2 = vx * vx + vy * vy;
            if (len2 < 1.0e-18) return true;
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var cross = Math.Abs(ex * vy - ey * vx);
            return cross / Math.Sqrt(len2) <= tol;
        }

        private static bool CollinearPt2(Pt2 a, Pt2 b, Pt2 c, double tol)
        {
            return CollinearXY(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0), new XYZ(c.X, c.Y, 0), tol);
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

            AppendBoundary(input, minLen, result);
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
                        EmitDash(result, layer, dashed[d][0], dashed[d][1], covers, px, py, dx, dy, minLen, pieces);
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
            List<Cover> covers, double px, double py, double dx, double dy, double minLen, List<TopPiece> pieces)
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
                if (poly.Count == 0) poly.Add(LiftHatch(a, best, layer, px, py, dx, dy, pieces));
                poly.Add(LiftHatch(b, best, layer, px, py, dx, dy, pieces));
                prev = best;
            }
            Flush(result, layer, poly, minLen, null);
        }

        /// <summary>Lifts hatch lines above the live top so foreground and background both clear the surface pattern (+Z min clearance).</summary>
        private static XYZ LiftHatch(double t, TopPiece piece, PatternLayer layer, double px, double py, double dx, double dy,
            List<TopPiece> pieces)
        {
            var x = px + dx * t;
            var y = py + dy * t;
            piece = HighestPieceAt(pieces, x, y) ?? piece;
            var z = piece.ZAt(x, y);
            var o = FpfOptions.SurfaceOffsetFeet;
            var ox = piece.Nx * o;
            var oy = piece.Ny * o;
            var oz = piece.Nz * o;
            var minLift = z + o;
            var zOut = z + oz;
            if (zOut + 1.0e-9 < minLift)
                oz += minLift - zOut;
            return new XYZ(x + ox, y + oy, z + oz);
        }

        private static TopPiece HighestPieceAt(List<TopPiece> pieces, double x, double y)
        {
            if (pieces == null) return null;
            TopPiece best = null;
            var bestZ = double.NegativeInfinity;
            var tol = FpfOptions.MeshCrackBridgeFeet;
            for (var i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                var r = p?.Region;
                if (r == null || r.IsEmpty) continue;
                if (x < r.MinX - tol || x > r.MaxX + tol || y < r.MinY - tol || y > r.MaxY + tol) continue;
                var z = p.ZAt(x, y);
                if (z > bestZ) { bestZ = z; best = p; }
            }
            return best;
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
        /// Outer silhouette plus opening loops, from Revit face edge loops only.
        /// Shared top-face edges appear twice and are dropped; triangulation fold lines are never used.
        /// </summary>
        private static void AppendBoundary(FloorHatchInput input, double minLen, FloorHatchResult result)
        {
            var weld = FpfOptions.MeshCrackBridgeFeet;
            var edges = new List<BEdge>();
            foreach (var be in input.BoundaryEdges)
            {
                if (be?.Piece == null) continue;
                edges.Add(new BEdge
                {
                    X1 = be.X1, Y1 = be.Y1, X2 = be.X2, Y2 = be.Y2, Piece = be.Piece
                });
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
            boundary = DedupEdges(boundary);

            var loops = ChainLoops(boundary);
            loops.AddRange(ChainLoops(UncoveredHoleEdges(input, boundary)));
            var cleaned = new List<List<Pt2>>();
            var closed = new List<bool>();
            foreach (var loop in loops)
            {
                var c = CleanLoop(loop, minLen, out var isClosed);
                if (c.Count >= 2)
                {
                    cleaned.Add(c);
                    closed.Add(isClosed);
                }
            }

            ClipHatchToInset(result, cleaned, closed, minLen);
            EmitAuthorityEdgeLoops(input, minLen, result.Segments);
        }

        /// <summary>Drops a shorter edge that lies on top of a longer one (the usual cause of a thick/thin outline).</summary>
        private static List<BEdge> DedupEdges(List<BEdge> edges)
        {
            var keep = new bool[edges.Count];
            for (var i = 0; i < keep.Length; i++) keep[i] = true;
            var tol = FpfOptions.HatchInsetFeet;
            for (var i = 0; i < edges.Count; i++)
            {
                if (!keep[i]) continue;
                var li = Math.Sqrt(Math.Pow(edges[i].X2 - edges[i].X1, 2) + Math.Pow(edges[i].Y2 - edges[i].Y1, 2));
                for (var j = i + 1; j < edges.Count; j++)
                {
                    if (!keep[j]) continue;
                    if (!PolygonClipper.CollinearOverlap(edges[i].X1, edges[i].Y1, edges[i].X2, edges[i].Y2,
                            edges[j].X1, edges[j].Y1, edges[j].X2, edges[j].Y2, tol, out var lo, out var hi))
                        continue;
                    var lj = Math.Sqrt(Math.Pow(edges[j].X2 - edges[j].X1, 2) + Math.Pow(edges[j].Y2 - edges[j].Y1, 2));
                    var shorter = Math.Min(li, lj);
                    if (shorter < 1.0e-9 || hi - lo < shorter * 0.6) continue;
                    if (li >= lj) keep[j] = false;
                    else { keep[i] = false; break; }
                }
            }
            var result = new List<BEdge>();
            for (var i = 0; i < edges.Count; i++)
                if (keep[i]) result.Add(edges[i]);
            return result;
        }

        /// <summary>Opening loops that the solid did not already leave as a silhouette. Welded so each hole chains into one loop.</summary>
        private static List<BEdge> UncoveredHoleEdges(FloorHatchInput input, List<BEdge> boundary)
        {
            var result = new List<BEdge>();
            if (input.Holes == null) return result;
            var tol = FpfOptions.BoundaryOverlapFeet;
            var weld = FpfOptions.MeshCrackBridgeFeet;
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
                    var a = Vid(x1, y1);
                    var bId = Vid(x2, y2);
                    if (a == bId) continue;
                    var piece = PieceNearest(input.Pieces, (x1 + x2) * 0.5, (y1 + y2) * 0.5);
                    result.Add(new BEdge { A = a, B = bId, X1 = vx[a], Y1 = vy[a], X2 = vx[bId], Y2 = vy[bId], Piece = piece });
                }
            }
            return result;
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

        private static List<List<Pt2>> ChainLoops(List<BEdge> edges)
        {
            var loops = new List<List<Pt2>>();
            if (edges == null || edges.Count == 0) return loops;
            var adj = new Dictionary<int, List<int>>();
            for (var i = 0; i < edges.Count; i++)
            {
                if (edges[i].A < 0 || edges[i].B < 0 || edges[i].A == edges[i].B) continue;
                AddAdj(adj, edges[i].A, i);
                AddAdj(adj, edges[i].B, i);
            }
            var used = new bool[edges.Count];
            for (var i = 0; i < edges.Count; i++)
            {
                if (used[i] || edges[i].A < 0 || edges[i].A == edges[i].B) continue;
                var pts = new List<Pt2>();
                WalkXY(edges, adj, used, i, edges[i].A, pts);
                if (pts.Count >= 2) loops.Add(pts);
            }
            return loops;
        }

        private static void AddAdj(Dictionary<int, List<int>> adj, int vertex, int edge)
        {
            if (!adj.TryGetValue(vertex, out var list)) adj[vertex] = list = new List<int>();
            list.Add(edge);
        }

        /// <summary>Walks one direction, then the other, so an open chain is not cut in half.</summary>
        private static void WalkXY(List<BEdge> edges, Dictionary<int, List<int>> adj, bool[] used, int start, int fromVertex, List<Pt2> pts)
        {
            var guard = 0;
            var eidx = start;
            var prev = fromVertex;
            var backward = new List<Pt2>();
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
                if (pts.Count == 0) pts.Add(new Pt2 { X = x0, Y = y0 });
                pts.Add(new Pt2 { X = x1, Y = y1 });
                prev = next;
                eidx = NextUnused(adj, used, next, eidx);
            }

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
                backward.Add(new Pt2 { X = x1, Y = y1 });
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
            // Hatch sits at SurfaceOffsetFeet. The edge is a further 2 mm along the normal so it draws on top.
            var o = FpfOptions.SurfaceOffsetFeet + FpfOptions.EdgeLiftAboveHatchFeet;
            return new XYZ(x + piece.Nx * o, y + piece.Ny * o, z + piece.Nz * o);
        }

        /// <summary>
        /// Drops duplicate points, points that sit on the line between their neighbours, and edges shorter
        /// than Revit can store. What remains is one vertex per corner, shared by the two edges that meet there.
        /// </summary>
        private static List<Pt2> CleanLoop(List<Pt2> src, double minLen, out bool closed)
        {
            var pts = new List<Pt2>();
            var weld = FpfOptions.MeshCrackBridgeFeet;
            foreach (var p in src)
            {
                if (pts.Count == 0 || Dist2(pts[pts.Count - 1], p) > weld)
                    pts.Add(p);
            }
            closed = pts.Count >= 2 && Dist2(pts[0], pts[pts.Count - 1]) <= weld * 4;
            if (closed)
                pts.RemoveAt(pts.Count - 1);

            var col = 0.5 / 304.8;
            var guard = 0;
            var changed = true;
            while (changed && pts.Count >= 4 && guard++ < 8)
            {
                changed = false;
                for (var i = 0; i < pts.Count; i++)
                {
                    var a = pts[(i + pts.Count - 1) % pts.Count];
                    var b = pts[i];
                    var c = pts[(i + 1) % pts.Count];
                    if (DistPointSeg(b.X, b.Y, a.X, a.Y, c.X, c.Y) <= col)
                    {
                        pts.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            guard = 0;
            while (pts.Count >= 4 && guard++ < pts.Count + 2)
            {
                var kill = -1;
                for (var i = 0; i < pts.Count; i++)
                {
                    if (Dist2(pts[i], pts[(i + 1) % pts.Count]) < minLen)
                    {
                        kill = (i + 1) % pts.Count;
                        break;
                    }
                }
                if (kill < 0) break;
                pts.RemoveAt(kill);
            }
            if (pts.Count < 3) closed = false;
            return pts;
        }

        private static double Dist2(Pt2 a, Pt2 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Closed loops from top-face EdgeLoops only (consecutive vertices; collinear merge on same loop).</summary>
        private static void EmitAuthorityEdgeLoops(FloorHatchInput input, double minLen, List<Segment3> target)
        {
            var pieces = input.Pieces;
            var cornerRad = 2.0 * Math.PI / 180.0;
            var runId = 0;
            foreach (var auth in input.AuthorityBoundaryLoops)
            {
                if (auth?.Points == null || auth.Points.Count < 2) continue;
                var loop = MergeConsecutiveCollinearInLoop(auth.Points, true);
                if (loop.Count < 2) continue;
                var n = loop.Count;
                var sideRun = ++runId;
                var defPiece = auth.Piece;
                for (var i = 0; i < n; i++)
                {
                    var prev = loop[(i + n - 1) % n];
                    var cur = loop[i];
                    var next = loop[(i + 1) % n];
                    if (i > 0 && TurnAngle(prev, cur, next) >= cornerRad)
                        sideRun = ++runId;
                    var aPt = loop[i];
                    var bPt = loop[(i + 1) % n];
                    var pieceA = PieceNearest(pieces, aPt.X, aPt.Y) ?? defPiece;
                    var pieceB = PieceNearest(pieces, bPt.X, bPt.Y) ?? defPiece;
                    var a = pieceA == null ? new XYZ(aPt.X, aPt.Y, 0) : LiftXY(aPt.X, aPt.Y, pieceA);
                    var b = pieceB == null ? new XYZ(bPt.X, bPt.Y, 0) : LiftXY(bPt.X, bPt.Y, pieceB);
                    if (a.DistanceTo(b) < minLen) continue;
                    target.Add(new Segment3 { A = a, B = b, Layer = EdgeLayer, EdgeRun = sideRun });
                }
            }
        }

        private static List<Pt2> MergeConsecutiveCollinearInLoop(List<Pt2> loop, bool closed)
        {
            if (loop == null || loop.Count < 3) return loop;
            var col = 0.5 / 304.8;
            var n = loop.Count;
            var keep = new bool[n];
            for (var i = 0; i < n; i++) keep[i] = true;
            for (var i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) continue;
                var prev = loop[(i + n - 1) % n];
                var cur = loop[i];
                var next = loop[(i + 1) % n];
                if (CollinearPt2(prev, cur, next, col)) keep[i] = false;
            }
            var merged = new List<Pt2>(n);
            for (var i = 0; i < n; i++)
                if (keep[i]) merged.Add(loop[i]);
            return merged.Count >= 2 ? merged : loop;
        }

        private static double TurnAngle(Pt2 prev, Pt2 cur, Pt2 next)
        {
            var v1x = cur.X - prev.X;
            var v1y = cur.Y - prev.Y;
            var v2x = next.X - cur.X;
            var v2y = next.Y - cur.Y;
            var l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            var l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (l1 < 1.0e-12 || l2 < 1.0e-12) return 0;
            var dot = (v1x * v2x + v1y * v2y) / (l1 * l2);
            if (dot > 1) dot = 1;
            else if (dot < -1) dot = -1;
            return Math.Acos(dot);
        }

        /// <summary>
        /// Keeps hatch only inside the floor, and 1.5 mm clear of every outline and opening edge.
        /// Strokes that end on, cross, or fall inside an opening are removed.
        /// </summary>
        private static void ClipHatchToInset(FloorHatchResult result, List<List<Pt2>> loops, List<bool> closedFlags, double minLen)
        {
            var closed = new List<List<Pt2>>();
            for (var i = 0; i < loops.Count; i++)
                if (i < closedFlags.Count && closedFlags[i] && loops[i].Count >= 3) closed.Add(loops[i]);
            if (closed.Count == 0 || result.Segments.Count == 0) return;

            var region = new RegionXY();
            AddLoops(region, closed);
            if (region.IsEmpty) return;
            var inset = FpfOptions.HatchInsetFeet;

            var edges = new List<Pt2>();
            for (var li = 0; li < loops.Count; li++)
            {
                var loop = loops[li];
                var isClosed = li < closedFlags.Count && closedFlags[li] && loop.Count >= 3;
                var n = isClosed ? loop.Count : loop.Count - 1;
                for (var i = 0; i < n; i++)
                {
                    edges.Add(loop[i]);
                    edges.Add(loop[(i + 1) % loop.Count]);
                }
            }

            var kept = new List<Segment3>();
            foreach (var seg in result.Segments)
            {
                if (seg?.A == null || seg.B == null) continue;
                if (seg.Layer != null && seg.Layer.Kind == PatternLayerKind.Edge)
                {
                    kept.Add(seg);
                    continue;
                }
                var dx = seg.B.X - seg.A.X;
                var dy = seg.B.Y - seg.A.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < minLen) continue;
                var inside = PolygonClipper.ClipLine(region, seg.A.X, seg.A.Y, dx / len, dy / len);
                foreach (var iv in inside)
                {
                    var t0 = Math.Max(0, iv[0]);
                    var t1 = Math.Min(len, iv[1]);
                    if (t1 - t0 < minLen) continue;
                    var cuts = new List<double[]>();
                    var sx0 = seg.A.X + dx * (t0 / len);
                    var sy0 = seg.A.Y + dy * (t0 / len);
                    var sx1 = seg.A.X + dx * (t1 / len);
                    var sy1 = seg.A.Y + dy * (t1 / len);
                    for (var e = 0; e + 1 < edges.Count; e += 2)
                    {
                        var ex0 = Math.Min(edges[e].X, edges[e + 1].X) - inset;
                        var ex1 = Math.Max(edges[e].X, edges[e + 1].X) + inset;
                        var ey0 = Math.Min(edges[e].Y, edges[e + 1].Y) - inset;
                        var ey1 = Math.Max(edges[e].Y, edges[e + 1].Y) + inset;
                        if (Math.Max(sx0, sx1) < ex0 || Math.Min(sx0, sx1) > ex1 ||
                            Math.Max(sy0, sy1) < ey0 || Math.Min(sy0, sy1) > ey1) continue;
                        AddCapsuleCuts(seg.A.X, seg.A.Y, seg.B.X, seg.B.Y,
                            edges[e].X, edges[e].Y, edges[e + 1].X, edges[e + 1].Y, inset, cuts);
                    }
                    var remain = cuts.Count == 0
                        ? new List<double[]> { new[] { t0, t1 } }
                        : PolygonClipper.SubtractIntervals(new List<double[]> { new[] { t0, t1 } },
                            PolygonClipper.UnionIntervals(cuts, inset));
                    foreach (var bit in remain)
                    {
                        if (bit[1] - bit[0] < minLen) continue;
                        var u0 = bit[0] / len;
                        var u1 = bit[1] / len;
                        kept.Add(new Segment3
                        {
                            A = new XYZ(seg.A.X + dx * u0, seg.A.Y + dy * u0, seg.A.Z + (seg.B.Z - seg.A.Z) * u0),
                            B = new XYZ(seg.A.X + dx * u1, seg.A.Y + dy * u1, seg.A.Z + (seg.B.Z - seg.A.Z) * u1),
                            Layer = seg.Layer
                        });
                    }
                }
            }
            result.Segments.Clear();
            result.Segments.AddRange(kept);
        }

        /// <summary>Parameter ranges of AB (feet from A) that come within <paramref name="radius"/> of segment CD.</summary>
        private static void AddCapsuleCuts(double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy, double radius, List<double[]> cuts)
        {
            var abx = bx - ax;
            var aby = by - ay;
            var len = Math.Sqrt(abx * abx + aby * aby);
            if (len < 1.0e-9) return;
            var ux = abx / len;
            var uy = aby / len;
            AddDiskCut(ax, ay, ux, uy, len, cx, cy, radius, cuts);
            AddDiskCut(ax, ay, ux, uy, len, dx, dy, radius, cuts);

            var cdx = dx - cx;
            var cdy = dy - cy;
            var clen = Math.Sqrt(cdx * cdx + cdy * cdy);
            if (clen < 1.0e-9) return;
            var vx = cdx / clen;
            var vy = cdy / clen;
            var c0 = (ax - cx) * vy - (ay - cy) * vx;
            var c1 = ux * vy - uy * vx;
            var s0 = (ax - cx) * vx + (ay - cy) * vy;
            var s1 = ux * vx + uy * vy;
            if (!TIntervalAbs(c0, c1, radius, out var tA, out var tB)) return;
            if (!TIntervalRange(s0, s1, 0, clen, out var sA, out var sB)) return;
            var lo = Math.Max(0, Math.Max(tA, sA));
            var hi = Math.Min(len, Math.Min(tB, sB));
            if (hi - lo > 1.0e-9) cuts.Add(new[] { lo, hi });
        }

        private static void AddDiskCut(double ax, double ay, double ux, double uy, double len,
            double cx, double cy, double radius, List<double[]> cuts)
        {
            var px = ax - cx;
            var py = ay - cy;
            var b = px * ux + py * uy;
            var c = px * px + py * py - radius * radius;
            var disc = b * b - c;
            if (disc < 0) return;
            var sd = Math.Sqrt(disc);
            var t0 = Math.Max(0, -b - sd);
            var t1 = Math.Min(len, -b + sd);
            if (t1 - t0 > 1.0e-9) cuts.Add(new[] { t0, t1 });
        }

        private static bool TIntervalAbs(double c0, double c1, double r, out double lo, out double hi)
        {
            if (Math.Abs(c1) < 1.0e-12)
            {
                lo = double.NegativeInfinity;
                hi = double.PositiveInfinity;
                return Math.Abs(c0) <= r;
            }
            lo = (-r - c0) / c1;
            hi = (r - c0) / c1;
            if (lo > hi) { var t = lo; lo = hi; hi = t; }
            return true;
        }

        private static bool TIntervalRange(double s0, double s1, double a, double b, out double lo, out double hi)
        {
            if (Math.Abs(s1) < 1.0e-12)
            {
                lo = double.NegativeInfinity;
                hi = double.PositiveInfinity;
                return s0 >= a - 1.0e-9 && s0 <= b + 1.0e-9;
            }
            lo = (a - s0) / s1;
            hi = (b - s0) / s1;
            if (lo > hi) { var t = lo; lo = hi; hi = t; }
            return true;
        }

        private static void AddLoops(RegionXY region, List<List<Pt2>> loops)
        {
            foreach (var loop in loops)
            {
                if (loop.Count < 2) continue;
                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    if (i == loop.Count - 1 && loop.Count < 3) break;
                    region.AddEdge(a.X, a.Y, b.X, b.Y);
                }
            }
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
