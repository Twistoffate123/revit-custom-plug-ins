using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>Compares live surface pattern orientation to generated hatch lines and corrects small mismatches.</summary>
    internal static class PatternAngleMatcher
    {
        private const double VerifyToleranceRad = 0.1 * Math.PI / 180.0;

        public sealed class Result
        {
            public bool Verified;
            public double? LiveDeg;
            public double? FlatDeg;
            public bool Corrected;
            public double CorrectionDeg;
        }

        /// <summary>
        /// When <paramref name="placement"/> is identity, measures live vs flat (deg) and may set
        /// <paramref name="placement"/> angle so flattened lines match the live pattern.
        /// </summary>
        public static Result VerifyAndCorrect(Floor floor, FloorHatchInput input, FloorHatchResult build,
            PatternPlacement placement, Document doc)
        {
            var result = new Result();
            if (placement == null || input == null || build == null || doc == null)
                return result;

            var piece = LargestPiece(input);
            if (piece?.Pattern == null)
                return result;

            var live = TryLiveDominantAngleRad(piece, doc);
            var flat = MeasureFlatDominantAngleRad(build);
            if (!live.HasValue || !flat.HasValue)
                return result;

            result.LiveDeg = live.Value * 180.0 / Math.PI;
            result.FlatDeg = flat.Value * 180.0 / Math.PI;
            result.Verified = true;

            if (!placement.IsIdentity)
                return result;

            var delta = UndirectedDelta(live.Value, flat.Value);
            if (Math.Abs(delta) <= VerifyToleranceRad)
                return result;

            placement.AngleRad += delta;
            result.Corrected = true;
            result.CorrectionDeg = delta * 180.0 / Math.PI;
            return result;
        }

        /// <summary>Updates <see cref="Result.FlatDeg"/> after a rebuild.</summary>
        public static void RefreshFlatFromBuild(FloorHatchResult build, Result r)
        {
            if (r == null || build == null) return;
            var flat = MeasureFlatDominantAngleRad(build);
            if (flat.HasValue) r.FlatDeg = flat.Value * 180.0 / Math.PI;
        }

        public static string FormatForReport(Floor floor, Result r)
        {
            var name = Services.FpfFloorNaming.FloorDisplayName(floor);
            if (!r.Verified || !r.LiveDeg.HasValue || !r.FlatDeg.HasValue)
                return name + ": angle unverified";
            var live = r.LiveDeg.Value.ToString("0.###", CultureInfo.InvariantCulture);
            var flat = r.FlatDeg.Value.ToString("0.###", CultureInfo.InvariantCulture);
            var suffix = r.Corrected
                ? " (corrected " + r.CorrectionDeg.ToString("0.###", CultureInfo.InvariantCulture) + " deg)"
                : string.Empty;
            return name + ": live " + live + " deg, flat " + flat + " deg" + suffix;
        }

        private static TopPiece LargestPiece(FloorHatchInput input)
        {
            TopPiece best = null;
            var bestArea = 0.0;
            foreach (var p in input.Pieces)
            {
                if (p?.Region == null || p.Pattern == null) continue;
                if (p.Region.IsEmpty) continue;
                var a = (p.Region.MaxX - p.Region.MinX) * (p.Region.MaxY - p.Region.MinY);
                if (a > bestArea) { bestArea = a; best = p; }
            }
            return best;
        }

        internal static double TryFacePlanRotationRad(Face face)
        {
            if (face == null) return 0;
            try
            {
                var bb = face.GetBoundingBox();
                if (bb == null) return 0;
                var uv = new UV((bb.Min.U + bb.Max.U) * 0.5, (bb.Min.V + bb.Max.V) * 0.5);
                var d = face.ComputeDerivatives(uv);
                var bx = d.BasisX;
                var len = Math.Sqrt(bx.X * bx.X + bx.Y * bx.Y);
                if (len < 1.0e-9) return 0;
                return Math.Atan2(bx.Y, bx.X);
            }
            catch
            {
                return 0;
            }
        }

        private static double? TryLiveDominantAngleRad(TopPiece piece, Document doc)
        {
            var pattern = piece.Pattern;
            var layer = pattern.Layers.FirstOrDefault(l => l.Kind == PatternLayerKind.Foreground)
                        ?? pattern.Layers.FirstOrDefault();
            if (layer == null || layer.Grids.Count == 0) return null;

            var grid = layer.Grids.OrderByDescending(g => g.Offset).FirstOrDefault(g => g.Offset > 1.0e-9)
                     ?? layer.Grids[0];
            var gridAngle = grid.Angle;

            var target = FillPatternTarget.Model;
            var hostOri = FillPatternHostOrientation.ToHost;
            if (!string.IsNullOrEmpty(layer.PatternUniqueId))
            {
                try
                {
                    if (doc.GetElement(layer.PatternUniqueId) is FillPatternElement fpe)
                    {
                        var fp = fpe.GetFillPattern();
                        if (fp != null)
                        {
                            target = fp.Target;
                            hostOri = fp.HostOrientation;
                        }
                    }
                }
                catch
                {
                    // keep defaults
                }
            }

            if (layer.IsDraftingScaled || target == FillPatternTarget.Drafting ||
                hostOri == FillPatternHostOrientation.ToView)
                return NormalizeUndirectedRad(gridAngle);

            return NormalizeUndirectedRad(gridAngle + piece.FacePlanRotationRad);
        }

        private static double? MeasureFlatDominantAngleRad(FloorHatchResult build)
        {
            var sumX = 0.0;
            var sumY = 0.0;
            var total = 0.0;
            foreach (var seg in build.Segments)
            {
                if (seg.Layer == null || seg.Layer.Kind == PatternLayerKind.Edge) continue;
                if (seg.Layer.Kind == PatternLayerKind.Background) continue;
                var dx = seg.B.X - seg.A.X;
                var dy = seg.B.Y - seg.A.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.05) continue;
                sumX += dx / len * len;
                sumY += dy / len * len;
                total += len;
            }
            if (total < 1.0e-6) return null;
            return NormalizeUndirectedRad(Math.Atan2(sumY / total, sumX / total));
        }

        private static double NormalizeUndirectedRad(double a)
        {
            var t = a % Math.PI;
            if (t < 0) t += Math.PI;
            return t;
        }

        private static double UndirectedDelta(double live, double flat)
        {
            var d = live - flat;
            while (d > Math.PI * 0.5) d -= Math.PI;
            while (d < -Math.PI * 0.5) d += Math.PI;
            return d;
        }
    }
}