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
    /// Everything is best effort: on failure the lines just use the plain Generic Models style.
    /// </summary>
    public sealed class PatternStyles
    {
        private readonly Document _doc;
        private readonly Dictionary<string, ElementId> _styleByKey = new Dictionary<string, ElementId>();
        private readonly Dictionary<PatternLayerKind, ElementId> _typeByKind = new Dictionary<PatternLayerKind, ElementId>();

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
        public ElementId GetTypeId(PatternLayerKind kind)
        {
            if (_typeByKind.TryGetValue(kind, out var cached)) return cached;
            var name = TypeName(kind);
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

            _typeByKind[kind] = id;
            return id;
        }
    }
}
