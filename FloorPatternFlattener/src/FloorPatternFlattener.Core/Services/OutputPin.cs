using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>
    /// Output DirectShapes, edges and the model group stay unpinned. A pinned member makes
    /// Revit refuse Align, Move and Rotate of the group ("Can't move pinned element").
    /// </summary>
    internal static class OutputPin
    {
        public static int Unpin(Element element)
        {
            if (element == null) return 0;
            try
            {
                if (!element.IsValidObject || !element.Pinned) return 0;
                element.Pinned = false;
                return 1;
            }
            catch
            {
                return 0;
            }
        }

        public static int UnpinAll(IEnumerable<Element> elements)
        {
            var n = 0;
            if (elements == null) return 0;
            foreach (var element in elements) n += Unpin(element);
            return n;
        }

        /// <summary>Unpins the group and every member so a later MoveElement can translate the whole group.</summary>
        public static int UnpinGroup(Document doc, Group group)
        {
            if (doc == null || group == null) return 0;
            var n = Unpin(group);
            IList<ElementId> members = null;
            try { members = group.GetMemberIds(); }
            catch { members = null; }
            if (members == null) return n;
            foreach (var id in members)
            {
                try { n += Unpin(doc.GetElement(id)); }
                catch { /* member not editable */ }
            }
            return n;
        }
    }
}
