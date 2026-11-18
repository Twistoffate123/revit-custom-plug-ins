using System.Collections.Generic;
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

        /// <summary>Instance Comments on the model group (floor label only; unchanged).</summary>
        public static string InstanceComments(Floor floor) => FloorDisplayName(floor);

        /// <summary>DirectShape instance Comments: floor label plus layer tag for filters.</summary>
        public static string ChunkInstanceComments(Floor floor, IList<Segment3> segs, PatternLayerKind chunkKind)
        {
            var baseText = InstanceComments(floor);
            return baseText + ChunkLayerTag(segs, chunkKind);
        }

        private static string ChunkLayerTag(IList<Segment3> segs, PatternLayerKind chunkKind)
        {
            var hasFg = false;
            var hasBg = false;
            var hasEdge = false;
            if (segs != null)
            {
                foreach (var seg in segs)
                {
                    if (seg?.Layer == null) continue;
                    if (seg.Layer.Kind == PatternLayerKind.Foreground) hasFg = true;
                    else if (seg.Layer.Kind == PatternLayerKind.Background) hasBg = true;
                    else if (seg.Layer.Kind == PatternLayerKind.Edge) hasEdge = true;
                }
            }
            if (hasFg && hasBg) return " | Foreground+Background";
            if (hasEdge && !hasFg && !hasBg) return " | Edge";
            if (hasBg && !hasFg) return " | Background";
            if (chunkKind == PatternLayerKind.Background) return " | Background";
            if (chunkKind == PatternLayerKind.Edge) return " | Edge";
            return " | Foreground";
        }

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