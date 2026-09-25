using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>Worksharing helpers. In non-workshared documents everything is editable.</summary>
    public static class Editability
    {
        /// <summary>
        /// True if the current user can modify the element without a worksharing failure
        /// (not owned by someone else and not out of date with central). Safe inside updaters and transactions.
        /// </summary>
        public static bool CanEdit(Document doc, ElementId id)
        {
            if (doc == null || id == null || id == ElementId.InvalidElementId) return false;
            if (!doc.IsWorkshared) return true;
            try
            {
                var status = WorksharingUtils.GetCheckoutStatus(doc, id);
                if (status == CheckoutStatus.OwnedByOtherUser) return false;

                var upd = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                if (upd == ModelUpdatesStatus.DeletedInCentral || upd == ModelUpdatesStatus.UpdatedInCentral)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Must be called OUTSIDE a transaction. Borrows (checks out) the elements that can be edited and returns
        /// the ids that could NOT be acquired. Non-workshared documents return an empty set.
        /// </summary>
        public static HashSet<ElementId> AcquireOrGetDenied(Document doc, IEnumerable<ElementId> ids)
        {
            var denied = new HashSet<ElementId>();
            if (doc == null || ids == null || !doc.IsWorkshared) return denied;

            var candidates = new HashSet<ElementId>();
            foreach (var id in ids.Where(i => i != null && i != ElementId.InvalidElementId).Distinct())
            {
                if (CanEdit(doc, id)) candidates.Add(id);
                else denied.Add(id);
            }

            if (candidates.Count == 0) return denied;

            try
            {
                var got = WorksharingUtils.CheckoutElements(doc, candidates);
                var gotSet = new HashSet<ElementId>(got ?? (ICollection<ElementId>)new List<ElementId>());
                foreach (var id in candidates)
                {
                    if (gotSet.Contains(id)) continue;
                    // Not returned: may still be editable (e.g. never synchronised) - re-check the live status.
                    if (!CanEdit(doc, id)) denied.Add(id);
                }
            }
            catch (Exception)
            {
                // Central unreachable etc.: fall back to status check only; Revit will borrow on edit.
            }

            return denied;
        }
    }
}
