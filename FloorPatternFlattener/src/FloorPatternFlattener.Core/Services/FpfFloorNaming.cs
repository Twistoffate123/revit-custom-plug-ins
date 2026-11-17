using Autodesk.Revit.DB;
using FloorPatternFlattener.Geometry;

namespace FloorPatternFlattener.Services
{
    /// <summary>Display names for flat-pattern DirectShapes and groups tied to a source floor.</summary>
    internal static class FpfFloorNaming
    {
        /// <summary>What schedules and Properties should show: floor type, instance name, or Mark — nothing else.</summary>
        public static string FloorDisplayName(Floor floor)
        {
            if (floor == null) return "Floor";
            try
            {
                var n = floor.Name;
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            }
            catch
            {
                // ignore
            }
            try
            {
                var ft = floor.FloorType;
                if (ft != null && !string.IsNullOrWhiteSpace(ft.Name)) return ft.Name.Trim();
            }
            catch
            {
                // ignore
            }
            var mark = SafeMark(floor);
            if (!string.IsNullOrWhiteSpace(mark)) return mark;
            return "Floor";
        }

        /// <summary>Instance Comments: floor label only.</summary>
        public static string InstanceComments(Floor floor) => FloorDisplayName(floor);

        /// <summary>
        /// DirectShapeType name shown in Properties (floor name). Layer kinds use invisible suffixes so types stay unique.
        /// </summary>
        public static string DirectShapeTypeName(Floor floor, PatternLayerKind kind)
        {
            var label = FloorDisplayName(floor);
            if (kind == PatternLayerKind.Background) return label + "\u200B";
            if (kind == PatternLayerKind.Edge) return label + "\u200B\u200B";
            return label;
        }

        /// <summary>Type Comments carry the FPF filter marker and layer id (not shown as the type name).</summary>
        public static string TypeComments(PatternLayerKind kind) =>
            FpfOptions.CommentsTag + "|" + PatternStyles.TypeName(kind);

        public static string GroupTypeName(Floor floor) => FloorDisplayName(floor);

        public static string LayerLabel(PatternLayerKind kind)
        {
            if (kind == PatternLayerKind.Background) return "Background";
            if (kind == PatternLayerKind.Edge) return "Edge";
            return "Foreground";
        }

        private static string SafeMark(Floor floor)
        {
            try
            {
                var p = floor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                var s = p?.AsString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            catch
            {
                // ignore
            }
            return null;
        }
    }
}