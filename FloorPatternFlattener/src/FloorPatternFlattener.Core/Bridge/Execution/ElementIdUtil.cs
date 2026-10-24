using System;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Bridge
{
    internal static class ElementIdUtil
    {
        public static long GetValue(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return -1;
#if REVIT_2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        public static bool TryFromValue(long value, out ElementId id, out string error)
        {
            id = null;
            error = null;
            if (value <= 0)
            {
                error = "Element ids are the positive numbers Revit uses. Run query_elements to find some.";
                return false;
            }
#if REVIT_2023
            if (value > int.MaxValue)
            {
                error = "That element id is too large for Revit 2023.";
                return false;
            }
            id = new ElementId((int)value);
#else
            id = new ElementId(value);
#endif
            return true;
        }
    }
}
