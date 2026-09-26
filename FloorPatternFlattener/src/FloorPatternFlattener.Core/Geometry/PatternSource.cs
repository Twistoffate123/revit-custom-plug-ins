using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>One family of parallel hatch lines (a FillGrid) in plan, Revit internal feet.</summary>
    public sealed class PatternGridDef
    {
        public double Angle;     // radians, measured in plan from +X
        public double OriginX;   // pattern-space origin == project internal origin based XY
        public double OriginY;
        public double Offset;    // perpendicular distance between successive lines
        public double Shift;     // along-line shift between successive lines
        public double[] Dashes;  // dash, gap, dash, gap ... (empty => continuous)
    }

    /// <summary>The resolved hatch definition for one material.</summary>
    public sealed class PatternSource
    {
        public List<PatternGridDef> Grids { get; } = new List<PatternGridDef>();
        public bool IsDefaultGrid { get; set; }
        public bool IsDraftingScaled { get; set; }
        public string MaterialUniqueId { get; set; } = string.Empty;
        public string PatternUniqueId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Deterministic text used in the floor signature.</summary>
        public string SignatureText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Reads the true fill-pattern geometry for a material's surface pattern (foreground, fallback background).
    /// Results are cached per material id for the lifetime of the reader (one command / updater run).
    /// </summary>
    public sealed class PatternReader
    {
        private readonly Document _doc;
        private readonly Dictionary<string, PatternSource> _cache = new Dictionary<string, PatternSource>();

        public PatternReader(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        public PatternSource ForMaterial(ElementId materialId)
        {
            var key = materialId == null ? "-1" : materialId.ToString();
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var src = Build(materialId);
            _cache[key] = src;
            return src;
        }

        private PatternSource Build(ElementId materialId)
        {
            Material material = null;
            if (materialId != null && materialId != ElementId.InvalidElementId)
                material = _doc.GetElement(materialId) as Material;

            FillPatternElement fpe = null;
            if (material != null)
            {
                fpe = TryGetPattern(material.SurfaceForegroundPatternId);
                if (fpe == null || IsSolidOrEmpty(fpe))
                {
                    var bg = TryGetPattern(material.SurfaceBackgroundPatternId);
                    if (bg != null && !IsSolidOrEmpty(bg)) fpe = bg;
                }
            }

            var src = new PatternSource
            {
                MaterialUniqueId = material?.UniqueId ?? string.Empty
            };

            FillPattern pattern = null;
            try { pattern = fpe?.GetFillPattern(); } catch { pattern = null; }

            if (pattern != null && !pattern.IsSolidFill)
            {
                var scale = 1.0;
                if (pattern.Target == FillPatternTarget.Drafting)
                {
                    scale = FpfOptions.DraftingPatternScale;
                    src.IsDraftingScaled = true;
                }

                IList<FillGrid> grids = null;
                try { grids = pattern.GetFillGrids(); } catch { grids = null; }

                if (grids != null)
                {
                    foreach (var g in grids)
                    {
                        if (g == null) continue;
                        var def = new PatternGridDef
                        {
                            Angle = g.Angle,
                            OriginX = g.Origin.U * scale,
                            OriginY = g.Origin.V * scale,
                            Offset = Math.Abs(g.Offset) * scale,
                            Shift = g.Shift * scale
                        };
                        var segs = new List<double>();
                        try
                        {
                            var raw = g.GetSegments();
                            if (raw != null)
                                foreach (var s in raw) segs.Add(Math.Abs(s) * scale);
                        }
                        catch
                        {
                            // continuous line
                        }
                        def.Dashes = segs.ToArray();
                        src.Grids.Add(def);
                    }
                }

                if (src.Grids.Count > 0)
                {
                    src.PatternUniqueId = fpe.UniqueId;
                    src.Description = (pattern.Target == FillPatternTarget.Drafting ? "drafting pattern '" : "model pattern '")
                                      + SafeName(fpe) + "'";
                }
            }

            if (src.Grids.Count == 0)
            {
                src.IsDefaultGrid = true;
                src.IsDraftingScaled = false;
                src.PatternUniqueId = fpe?.UniqueId ?? string.Empty;
                src.Description = "default 300 mm grid";
                var s = FpfOptions.DefaultGridSpacingFeet;
                src.Grids.Add(new PatternGridDef { Angle = 0, Offset = s, Dashes = new double[0] });
                src.Grids.Add(new PatternGridDef { Angle = Math.PI / 2.0, Offset = s, Dashes = new double[0] });
            }

            src.SignatureText = BuildSignatureText(src);
            return src;
        }

        private FillPatternElement TryGetPattern(ElementId id)
        {
            try
            {
                if (id == null || id == ElementId.InvalidElementId) return null;
                return _doc.GetElement(id) as FillPatternElement;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSolidOrEmpty(FillPatternElement fpe)
        {
            try
            {
                var p = fpe.GetFillPattern();
                return p == null || p.IsSolidFill || p.GridCount == 0;
            }
            catch
            {
                return true;
            }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return "?"; }
        }

        private static string BuildSignatureText(PatternSource src)
        {
            var sb = new StringBuilder();
            sb.Append("M:").Append(src.MaterialUniqueId).Append("|P:").Append(src.PatternUniqueId)
              .Append("|D:").Append(src.IsDefaultGrid ? 1 : 0).Append(src.IsDraftingScaled ? 1 : 0);
            foreach (var g in src.Grids)
            {
                sb.Append("|G").Append(F(g.Angle)).Append(',').Append(F(g.OriginX)).Append(',').Append(F(g.OriginY))
                  .Append(',').Append(F(g.Offset)).Append(',').Append(F(g.Shift));
                foreach (var d in g.Dashes) sb.Append(';').Append(F(d));
            }
            return sb.ToString();
        }

        internal static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
    }
}
