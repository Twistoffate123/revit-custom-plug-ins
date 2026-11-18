using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>Moves flat-pattern geometry up or down. Group members cannot be MoveElement'd.</summary>
    internal static class ShapeZOffset
    {
        public static bool TryOffset(Document doc, DirectShape ds, double deltaFeet)
        {
            if (doc == null || ds == null || !ds.IsValidObject) return false;
            if (System.Math.Abs(deltaFeet) < 1.0e-9) return true;

            var grouped = ds.GroupId != null && ds.GroupId != ElementId.InvalidElementId;
            if (!grouped)
            {
                OutputPin.Unpin(ds);
                ElementTransformUtils.MoveElement(doc, ds.Id, new XYZ(0, 0, deltaFeet));
                return true;
            }

            var geo = ds.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geo == null) return false;
            var moved = Transform.CreateTranslation(new XYZ(0, 0, deltaFeet));
            var list = new List<GeometryObject>();
            foreach (GeometryObject obj in geo)
            {
                if (!(obj is Curve curve)) continue;
                var copy = curve.CreateTransformed(moved);
                try
                {
                    if (curve.GraphicsStyleId != null && curve.GraphicsStyleId != ElementId.InvalidElementId)
                        copy.SetGraphicsStyleId(curve.GraphicsStyleId);
                }
                catch
                {
                    // keep the translated curve
                }
                list.Add(copy);
            }

            if (list.Count == 0) return false;
            ds.SetShape(list);
            return true;
        }
    }
}