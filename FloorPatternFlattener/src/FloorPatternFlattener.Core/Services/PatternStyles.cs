using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;

namespace FloorPatternFlattener.Services
{
    /// <summary>
    /// Makes the flattened pattern controllable like any other model graphics:
    ///  - every pattern layer gets a Generic Models subcategory ("FPF Pattern Foreground" / "FPF Pattern Background",
    ///    plus " RGB r-g-b" for non-black pattern colours) whose line colour is the material's pattern colour,
    ///    and the hatch lines are drawn with that subcategory's projection style;
    ///  - every layer's shapes use a DirectShapeType ("FPF Flat Pattern - Foreground" / "- Background") so view
    ///    filters can target the layers separately (Type Name / Type Comments), next to Comments = "FPF Flat Pattern".
    /// Category / subcategory settings are the lowest priority, so view filters (and V/G) still override them.
    /// Surface mode: one "FPF Flat Pattern - Surface" type, an "FPF Pattern Surface" subcategory and, per source
    /// material, a copy "FPF Flat Pattern - &lt;material&gt;" with the same surface patterns and colours.
    /// Everything is best effort: on failure the lines just use the plain Generic Models style.
    /// </summary>
    public sealed class PatternStyles
    {
        private readonly Document _doc;
        private readonly Dictionary<string, ElementId> _styleByKey = new Dictionary<string, ElementId>();
        private readonly Dictionary<string, ElementId> _typeByName = new Dictionary<string, ElementId>();
        private readonly Dictionary<string, ElementId> _surfaceMaterialBySource = new Dictionary<string, ElementId>();

        public PatternStyles(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        public static string SubcategoryName(PatternLayer layer)
        {
            var name = FpfOptions.SubcategoryPrefix + (layer.Kind == PatternLayerKind.Background ? "Background" : "Foreground");
            if (!layer.IsBlack)
                name += string.Format(CultureInfo.InvariantCulture, " RGB {0}-{1}-{2}", layer.Red, layer.Green, layer.Blue);
            return name;
        }

        public static string TypeName(PatternLayerKind kind) =>
            kind == PatternLayerKind.Background ? FpfOptions.BackgroundTypeName : FpfOptions.ForegroundTypeName;

        /// <summary>Projection GraphicsStyle id of the layer's subcategory (created on first use), or InvalidElementId.</summary>
        public ElementId GetLineStyleId(PatternLayer layer)
        {
            if (layer == null) return ElementId.InvalidElementId;
            var name = SubcategoryName(layer);
            if (_styleByKey.TryGetValue(name, out var cached)) return cached;

            var id = ElementId.InvalidElementId;
            try
            {
                var parent = Category.GetCategory(_doc, BuiltInCategory.OST_GenericModel);
                if (parent != null)
                {
                    Category sub = null;
                    foreach (Category c in parent.SubCategories)
                    {
                        if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) { sub = c; break; }
                    }

                    if (sub == null && parent.CanAddSubcategory)
                    {
                        sub = _doc.Settings.Categories.NewSubcategory(parent, name);
                        // Colour is only set when the subcategory is created; later edits in Object Styles are kept.
                        try { sub.LineColor = new Color(layer.Red, layer.Green, layer.Blue); } catch { /* ignore */ }
                    }

                    var gs = sub?.GetGraphicsStyle(GraphicsStyleType.Projection);
                    if (gs != null) id = gs.Id;
                }
            }
            catch
            {
                id = ElementId.InvalidElementId;
            }

            _styleByKey[name] = id;
            return id;
        }

        /// <summary>DirectShapeType for the layer kind (created on first use), or InvalidElementId.</summary>
        public ElementId GetTypeId(PatternLayerKind kind) => GetTypeIdByName(TypeName(kind));

        /// <summary>Surface mode DirectShapeType "FPF Flat Pattern - Surface" (created on first use), or InvalidElementId.</summary>
        public ElementId GetSurfaceTypeId() => GetTypeIdByName(FpfOptions.SurfaceTypeName);

        private ElementId GetTypeIdByName(string name)
        {
            if (_typeByName.TryGetValue(name, out var cached)) return cached;
            var catId = new ElementId(BuiltInCategory.OST_GenericModel);
            var id = ElementId.InvalidElementId;
            try
            {
                foreach (var el in new FilteredElementCollector(_doc).OfClass(typeof(DirectShapeType)))
                {
                    if (el is DirectShapeType t && t.Category != null && t.Category.Id == catId &&
                        string.Equals(t.Name, name, StringComparison.Ordinal))
                    {
                        id = t.Id;
                        break;
                    }
                }

                if (id == ElementId.InvalidElementId)
                {
                    var t = DirectShapeType.Create(_doc, name, catId);
                    if (t != null)
                    {
                        id = t.Id;
                        try
                        {
                            var p = t.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                            if (p != null && !p.IsReadOnly) p.Set(name);
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch
            {
                id = ElementId.InvalidElementId;
            }

            _typeByName[name] = id;
            return id;
        }

        /// <summary>Surface mode: graphics style of the "FPF Pattern Surface" subcategory (created on first use).</summary>
        public ElementId GetSurfaceStyleId()
        {
            var name = FpfOptions.SurfaceSubcategoryName;
            if (_styleByKey.TryGetValue(name, out var cached)) return cached;
            var id = ElementId.InvalidElementId;
            try
            {
                var parent = Category.GetCategory(_doc, BuiltInCategory.OST_GenericModel);
                if (parent != null)
                {
                    Category sub = null;
                    foreach (Category c in parent.SubCategories)
                        if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) { sub = c; break; }
                    if (sub == null && parent.CanAddSubcategory)
                        sub = _doc.Settings.Categories.NewSubcategory(parent, name);
                    var gs = sub?.GetGraphicsStyle(GraphicsStyleType.Projection);
                    if (gs != null) id = gs.Id;
                }
            }
            catch
            {
                id = ElementId.InvalidElementId;
            }
            _styleByKey[name] = id;
            return id;
        }

        // ------------------------------------------------------------------ surface materials

        public static string SurfaceMaterialName(Material source) =>
            FpfOptions.SurfaceMaterialPrefix + (source == null ? "No Material" : SafeName(source));

        /// <summary>
        /// Surface mode: the add-in's copy of <paramref name="sourceMaterialId"/> ("FPF Flat Pattern - &lt;name&gt;"),
        /// created on first use and kept in sync: surface foreground / background pattern + colour, shading colour
        /// and transparency. Returns InvalidElementId when it cannot be created (the surface then uses the category
        /// material). <paramref name="changed"/> is set when the material had to be created or updated.
        /// </summary>
        public ElementId GetSurfaceMaterialId(ElementId sourceMaterialId, ref bool changed) =>
            GetSurfaceMaterialId(sourceMaterialId, null, ref changed);

        /// <summary>
        /// As above; a non-identity <paramref name="placement"/> gives a separate material
        /// ("FPF Flat Pattern - &lt;name&gt; [R30 #ab12]") whose patterns are rotated / moved copies
        /// ("FPF &lt;pattern&gt; [R30 #ab12]") of the source material's patterns.
        /// </summary>
        public ElementId GetSurfaceMaterialId(ElementId sourceMaterialId, Geometry.PatternPlacement placement,
            ref bool changed)
        {
            if (placement != null && placement.IsIdentity) placement = null;
            var suffix = placement == null ? string.Empty : " " + PlacementTag(placement);
            var key = (sourceMaterialId == null ? "-1" : sourceMaterialId.ToString()) + suffix;
            if (_surfaceMaterialBySource.TryGetValue(key, out var cached)) return cached;

            var source = sourceMaterialId == null || sourceMaterialId == ElementId.InvalidElementId
                ? null
                : _doc.GetElement(sourceMaterialId) as Material;
            var name = SurfaceMaterialName(source) + suffix;
            var id = ElementId.InvalidElementId;
            try
            {
                Material target = null;
                foreach (var el in new FilteredElementCollector(_doc).OfClass(typeof(Material)))
                {
                    if (el is Material m && string.Equals(SafeName(m), name, StringComparison.OrdinalIgnoreCase))
                    {
                        target = m;
                        break;
                    }
                }

                if (target == null)
                {
                    var newId = Material.Create(_doc, name);
                    target = _doc.GetElement(newId) as Material;
                    changed = true;
                }

                if (target != null)
                {
                    if (SyncMaterial(source, target, placement, ref changed)) changed = true;
                    id = target.Id;
                }
            }
            catch
            {
                id = ElementId.InvalidElementId;
            }

            _surfaceMaterialBySource[key] = id;
            return id;
        }

        /// <summary>Copies the surface pattern settings (and shading) from source to target; true if anything changed.</summary>
        private bool SyncMaterial(Material source, Material target, Geometry.PatternPlacement placement,
            ref bool patternsChanged)
        {
            var changed = false;
            var none = ElementId.InvalidElementId;

            var fgId = source == null ? none : Safe(() => source.SurfaceForegroundPatternId, none);
            var bgId = source == null ? none : Safe(() => source.SurfaceBackgroundPatternId, none);
            if (placement != null)
            {
                fgId = GetPlacedPatternId(fgId, placement, ref patternsChanged);
                bgId = GetPlacedPatternId(bgId, placement, ref patternsChanged);
            }
            var fgColor = source == null ? null : Safe<Color>(() => source.SurfaceForegroundPatternColor, null);
            var bgColor = source == null ? null : Safe<Color>(() => source.SurfaceBackgroundPatternColor, null);

            changed |= Try(() => { if (target.SurfaceForegroundPatternId != fgId) { target.SurfaceForegroundPatternId = fgId; return true; } return false; });
            changed |= Try(() => { if (target.SurfaceBackgroundPatternId != bgId) { target.SurfaceBackgroundPatternId = bgId; return true; } return false; });
            if (IsValidColor(fgColor))
                changed |= Try(() => { if (!SameColor(target.SurfaceForegroundPatternColor, fgColor)) { target.SurfaceForegroundPatternColor = Copy(fgColor); return true; } return false; });
            if (IsValidColor(bgColor))
                changed |= Try(() => { if (!SameColor(target.SurfaceBackgroundPatternColor, bgColor)) { target.SurfaceBackgroundPatternColor = Copy(bgColor); return true; } return false; });

            if (source != null)
            {
                var shade = Safe<Color>(() => source.Color, null);
                if (IsValidColor(shade))
                    changed |= Try(() => { if (!SameColor(target.Color, shade)) { target.Color = Copy(shade); return true; } return false; });
                var tr = Safe(() => source.Transparency, -1);
                if (tr >= 0)
                    changed |= Try(() => { if (target.Transparency != tr) { target.Transparency = tr; return true; } return false; });
            }
            return changed;
        }

        // ------------------------------------------------------------------ rotated / anchored pattern copies

        /// <summary>Short, stable tag for names: "[R30 #ab12]" (hash covers angle and anchor).</summary>
        public static string PlacementTag(Geometry.PatternPlacement p)
        {
            var deg = (p.NormalizedAngle * 180.0 / Math.PI).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var text = p.ToStorage();
            uint h = 2166136261;
            foreach (var ch in text) { h ^= ch; h *= 16777619; }
            return "[R" + deg + (p.HasAnchor ? " #" + (h & 0xFFFF).ToString("x4") : string.Empty) + "]";
        }

        private readonly Dictionary<string, ElementId> _placedPatterns = new Dictionary<string, ElementId>();

        /// <summary>
        /// A copy of fill pattern <paramref name="sourceId"/> with every grid rotated by the placement angle and its
        /// origin moved to the anchor (model patterns only; drafting patterns are rotated but not moved, their
        /// spacing is paper-space). Solid fill / none are returned unchanged.
        /// </summary>
        private ElementId GetPlacedPatternId(ElementId sourceId, Geometry.PatternPlacement placement, ref bool changed)
        {
            if (sourceId == null || sourceId == ElementId.InvalidElementId) return sourceId;
            var key = sourceId + "|" + placement.ToStorage();
            if (_placedPatterns.TryGetValue(key, out var cached)) return cached;

            var result = sourceId;
            try
            {
                if (_doc.GetElement(sourceId) is FillPatternElement src)
                {
                    var fp = src.GetFillPattern();
                    if (fp != null && !fp.IsSolidFill)
                    {
                        var isModel = fp.Target == FillPatternTarget.Model;
                        var grids = new List<FillGrid>();
                        foreach (var g in fp.GetFillGrids())
                        {
                            double ox, oy;
                            if (isModel) placement.Map(g.Origin.U, g.Origin.V, out ox, out oy);
                            else
                            {
                                var rot = new Geometry.PatternPlacement { AngleRad = placement.AngleRad };
                                rot.Map(g.Origin.U, g.Origin.V, out ox, out oy);
                            }
                            var ng = new FillGrid
                            {
                                Angle = Wrap(g.Angle + placement.AngleRad),
                                Origin = new UV(ox, oy),
                                Offset = g.Offset,
                                Shift = g.Shift
                            };
                            var segs = g.GetSegments();
                            if (segs != null && segs.Count > 0) ng.SetSegments(segs);
                            grids.Add(ng);
                        }

                        var name = "FPF " + SafeName(src) + " " + PlacementTag(placement);
                        var existing = FillPatternElement.GetFillPatternElementByName(_doc, fp.Target, name);
                        if (existing == null)
                        {
                            var nfp = new FillPattern(name, fp.Target, fp.HostOrientation);
                            nfp.SetFillGrids(grids);
                            existing = FillPatternElement.Create(_doc, nfp);
                            changed = true;
                        }
                        else if (!SameGrids(existing.GetFillPattern(), grids))
                        {
                            var nfp = new FillPattern(name, fp.Target, fp.HostOrientation);
                            nfp.SetFillGrids(grids);
                            existing.SetFillPattern(nfp);
                            changed = true;
                        }
                        if (existing != null) result = existing.Id;
                    }
                }
            }
            catch
            {
                result = sourceId; // fall back to the unrotated pattern
            }

            _placedPatterns[key] = result;
            return result;
        }

        private static double Wrap(double a)
        {
            a %= 2 * Math.PI;
            return a < 0 ? a + 2 * Math.PI : a;
        }

        private static bool SameGrids(FillPattern fp, IList<FillGrid> grids)
        {
            try
            {
                var cur = fp?.GetFillGrids();
                if (cur == null || cur.Count != grids.Count) return false;
                for (var i = 0; i < cur.Count; i++)
                {
                    var a = cur[i];
                    var b = grids[i];
                    if (Math.Abs(Wrap(a.Angle) - Wrap(b.Angle)) > 1e-7 || Math.Abs(a.Offset - b.Offset) > 1e-7 ||
                        Math.Abs(a.Shift - b.Shift) > 1e-7 || Math.Abs(a.Origin.U - b.Origin.U) > 1e-6 ||
                        Math.Abs(a.Origin.V - b.Origin.V) > 1e-6) return false;
                    var sa = a.GetSegments();
                    var sb = b.GetSegments();
                    if ((sa?.Count ?? 0) != (sb?.Count ?? 0)) return false;
                    for (var k = 0; k < (sa?.Count ?? 0); k++)
                        if (Math.Abs(sa[k] - sb[k]) > 1e-7) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool Try(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        private static T Safe<T>(Func<T> f, T fallback)
        {
            try { return f(); } catch { return fallback; }
        }

        private static bool IsValidColor(Color c)
        {
            try { return c != null && c.IsValid; } catch { return false; }
        }

        private static bool SameColor(Color a, Color b)
        {
            if (!IsValidColor(a) || !IsValidColor(b)) return IsValidColor(a) == IsValidColor(b);
            return a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;
        }

        private static Color Copy(Color c) => new Color(c.Red, c.Green, c.Blue);

        private static string SafeName(Element e)
        {
            try { return e.Name ?? string.Empty; } catch { return string.Empty; }
        }
    }
}
