using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>Display names for flat-pattern DirectShapes and groups tied to a source floor.</summary>
    internal static class FpfFloorNaming
    {
        /// <summary>FPF - &lt;type name&gt; - &lt;Mark&gt; (Id …) or without Mark when empty.</summary>
        public static string BaseName(Floor floor)
        {
            if (floor == null) return "FPF - Floor";
            var typeName = "Floor";
            try
            {
                var ft = floor.FloorType;
                if (ft != null && !string.IsNullOrWhiteSpace(ft.Name)) typeName = ft.Name.Trim();
            }
            catch
            {
                // ignore
            }
            var mark = SafeMark(floor);
            var id = floor.Id.Value;
            if (!string.IsNullOrWhiteSpace(mark))
                return "FPF - " + typeName + " - " + mark + " (Id " + id + ")";
            return "FPF - " + typeName + " (Id " + id + ")";
        }

        /// <summary>Filter tag plus floor label so view filters (contains) and schedules both work.</summary>
        public static string CommentsValue(Floor floor) => FpfOptions.CommentsTag + " | " + BaseName(floor);

        public static string GroupTypeName(Floor floor) => BaseName(floor) + " pattern";

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