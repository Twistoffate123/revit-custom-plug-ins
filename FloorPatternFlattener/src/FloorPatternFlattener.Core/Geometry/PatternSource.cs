using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>Which of the material's two surface patterns a layer comes from.</summary>
    public enum PatternLayerKind
    {
        Background = 0,
        Foreground = 1
    }

    /// <summary>
    /// One surface pattern of a material (foreground or background): its grids and its pattern colour.
    /// Every layer is generated in its own DirectShape(s), on its own Generic Models subcategory.
    /// </summary>
    public sealed class PatternLayer
    {
        public PatternLayerKind Kind { get; set; }
        public List<PatternGridDef> Grids { get; } = new List<PatternGridDef>();
        public byte Red { get; set; }
        public byte Green { get; set; }
        public byte Blue { get; set; }
        public bool IsDefaultGrid { get; set; }
        public bool IsDraftingScaled { get; set; }
        public string PatternUniqueId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Groups segments into shapes: same kind + same colour share DirectShapes and a subcategory.</summary>
        public string Key => ((int)Kind).ToString(CultureInfo.InvariantCulture) + "|" + Red + "," + Green + "," + Blue;

        public bool IsBlack => Red == 0 && Green == 0 && Blue == 0;
    }

    /// <summary>The resolved hatch definition for one material: background layer (if any) then foreground layer.</summary>
    public sealed class PatternSource
    {
        /// <summary>Draw order: background first, foreground on top.</summary>
        public List<PatternLayer> Layers { get; } = new List<PatternLayer>();

        public bool IsDefaultGrid => Layers.Any(l => l.IsDefaultGrid);
        public bool IsDraftingScaled => Layers.Any(l => l.IsDraftingScaled);
        public string MaterialUniqueId { get; set; } = string.Empty;
        public IEnumerable<string> PatternUniqueIds => Layers.Select(l => l.PatternUniqueId).Where(s => !string.IsNullOrEmpty(s));
        public string Description => string.Join(" + ", Layers.Select(l => l.Kind.ToString().ToLowerInvariant() + " " + l.Description));

        /// <summary>Deterministic text used in the floor signature.</summary>
        public string SignatureText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Reads the true fill-pattern geometry for BOTH of a material's surface patterns (foreground and background,
    /// each with its own colour). Results are cached per material id for the lifetime of the reader.
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

            var src = new PatternSource
            {
                MaterialUniqueId = material?.UniqueId ?? string.Empty
            };

            if (material != null)
            {
                // Background first so the foreground is drawn on top (same stacking as Revit).
                var bg = ReadLayer(SafePatternId(() => material.SurfaceBackgroundPatternId), PatternLayerKind.Background,
                    SafeColor(() => material.SurfaceBackgroundPatternColor));
                if (bg != null) src.Layers.Add(bg);

                var fg = ReadLayer(SafePatternId(() => material.SurfaceForegroundPatternId), PatternLayerKind.Foreground,
                    SafeColor(() => material.SurfaceForegroundPatternColor));
                if (fg != null) src.Layers.Add(fg);
            }

            if (src.Layers.Count == 0)
            {
                var color = material == null ? null : SafeColor(() => material.SurfaceForegroundPatternColor);
                var layer = new PatternLayer
                {
                    Kind = PatternLayerKind.Foreground,
                    IsDefaultGrid = true,
                    Description = "default 300 mm grid"
                };
                ApplyColor(layer, color);
                var s = FpfOptions.DefaultGridSpacingFeet;
                layer.Grids.Add(new PatternGridDef { Angle = 0, Offset = s, Dashes = new double[0] });
                layer.Grids.Add(new PatternGridDef { Angle = Math.PI / 2.0, Offset = s, Dashes = new double[0] });
                src.Layers.Add(layer);
            }

            src.SignatureText = BuildSignatureText(src);
            return src;
        }

        /// <summary>Reads one surface pattern; null when it is none, solid or has no grids.</summary>
        private PatternLayer ReadLayer(ElementId patternId, PatternLayerKind kind, Color color)
        {
            var fpe = TryGetPattern(patternId);
            if (fpe == null) return null;

            FillPattern pattern;
            try { pattern = fpe.GetFillPattern(); } catch { pattern = null; }
            if (pattern == null || pattern.IsSolidFill) return null;

            var layer = new PatternLayer { Kind = kind };
            ApplyColor(layer, color);

            var scale = 1.0;
            if (pattern.Target == FillPatternTarget.Drafting)
            {
                scale = FpfOptions.DraftingPatternScale;
                layer.IsDraftingScaled = true;
            }

            IList<FillGrid> grids;
            try { grids = pattern.GetFillGrids(); } catch { grids = null; }
            if (grids == null) return null;

            foreach (var g in grids)
            {
                if (g == null) continue;
                // Line k starts at Origin + k*(Shift along the line, Offset perpendicular to it).
                // Offset may be negative (e.g. the 135 deg family of every standard herringbone .pat).
                // To use a positive spacing BOTH values must be negated; negating only Offset (the old Math.Abs)
                // moves every other line along its length and gives overlapping / crossing bricks.
                var gOffset = g.Offset;
                var gShift = g.Shift;
                if (gOffset < 0)
                {
                    gOffset = -gOffset;
                    gShift = -gShift;
                }
                var def = new PatternGridDef
                {
                    Angle = g.Angle,
                    OriginX = g.Origin.U * scale,
                    OriginY = g.Origin.V * scale,
                    Offset = gOffset * scale,
                    Shift = gShift * scale
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
                layer.Grids.Add(def);
            }

            if (layer.Grids.Count == 0) return null;

            layer.PatternUniqueId = fpe.UniqueId;
            layer.Description = (pattern.Target == FillPatternTarget.Drafting ? "drafting pattern '" : "model pattern '")
                                + SafeName(fpe) + "'";
            return layer;
        }

        private static void ApplyColor(PatternLayer layer, Color c)
        {
            try
            {
                if (c != null && c.IsValid)
                {
                    layer.Red = c.Red;
                    layer.Green = c.Green;
                    layer.Blue = c.Blue;
                }
            }
            catch
            {
                // keep black
            }
        }

        private static Color SafeColor(Func<Color> get)
        {
            try { return get(); } catch { return null; }
        }

        private static ElementId SafePatternId(Func<ElementId> get)
        {
            try { return get(); } catch { return ElementId.InvalidElementId; }
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

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return "?"; }
        }

        private static string BuildSignatureText(PatternSource src)
        {
            var sb = new StringBuilder();
            sb.Append("M:").Append(src.MaterialUniqueId);
            foreach (var l in src.Layers)
            {
                sb.Append("|L").Append((int)l.Kind).Append(':').Append(l.PatternUniqueId)
                  .Append("|C").Append(l.Red).Append(',').Append(l.Green).Append(',').Append(l.Blue)
                  .Append("|D:").Append(l.IsDefaultGrid ? 1 : 0).Append(l.IsDraftingScaled ? 1 : 0);
                foreach (var g in l.Grids)
                {
                    sb.Append("|G").Append(F(g.Angle)).Append(',').Append(F(g.OriginX)).Append(',').Append(F(g.OriginY))
                      .Append(',').Append(F(g.Offset)).Append(',').Append(F(g.Shift));
                    foreach (var d in g.Dashes) sb.Append(';').Append(F(d));
                }
            }
            return sb.ToString();
        }

        internal static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
    }
}
