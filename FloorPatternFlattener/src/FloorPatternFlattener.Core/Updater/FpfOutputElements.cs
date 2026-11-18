using System.Collections.Generic;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Updater
{
    /// <summary>Recognises add-in DirectShapes and pattern model groups (not source floors).</summary>
    internal static class FpfOutputElements
    {
        public static bool IsFlattenedDirectShape(DirectShape ds) =>
            ds != null && ds.IsValidObject &&
            (ds.ApplicationId == FpfOptions.ApplicationId || FpfStorage.ReadDirectShape(ds) != null);

        public static bool IsPatternGroup(Group group, Document doc)
        {
            if (group == null || !group.IsValidObject || doc == null) return false;
            try
            {
                var ids = group.GetMemberIds();
                if (ids == null || ids.Count == 0) return false;
                foreach (var id in ids)
                {
                    if (doc.GetElement(id) is DirectShape ds && IsFlattenedDirectShape(ds))
                        return true;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        public static bool IsIgnorableOutputElement(Element el, Document doc)
        {
            if (el == null || !el.IsValidObject) return false;
            if (el is DirectShape ds) return IsFlattenedDirectShape(ds);
            if (el is Group g) return IsPatternGroup(g, doc);
            return false;
        }

        /// <summary>Changed ids are only the floor, its flat-pattern shapes, and their group.</summary>
        public static bool OnlyOutputHierarchyTouched(Floor floor, Document doc, ICollection<ElementId> changedIds)
        {
            if (floor == null || changedIds == null || changedIds.Count == 0) return false;
            var allowed = new HashSet<ElementId> { floor.Id };
            var link = FpfStorage.ReadFloor(floor);
            if (link?.DirectShapeUniqueIds != null)
            {
                foreach (var uid in link.DirectShapeUniqueIds)
                {
                    if (!(doc.GetElement(uid) is DirectShape ds) || !ds.IsValidObject) continue;
                    allowed.Add(ds.Id);
                    try
                    {
                        var gid = ds.GroupId;
                        if (gid != null && gid != ElementId.InvalidElementId) allowed.Add(gid);
                    }
                    catch { /* ignore */ }
                }
            }

            foreach (var id in changedIds)
            {
                if (!allowed.Contains(id)) return false;
            }

            return true;
        }

        public static void CollectRelatedOutputIds(Floor floor, Document doc, ISet<ElementId> into)
        {
            if (floor == null || into == null || doc == null) return;
            var link = FpfStorage.ReadFloor(floor);
            if (link?.DirectShapeUniqueIds == null) return;
            foreach (var uid in link.DirectShapeUniqueIds)
            {
                if (!(doc.GetElement(uid) is DirectShape ds) || !ds.IsValidObject) continue;
                into.Add(ds.Id);
                try
                {
                    var gid = ds.GroupId;
                    if (gid != null && gid != ElementId.InvalidElementId) into.Add(gid);
                }
                catch { /* ignore */ }
            }
        }
    }
}