using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Commands
{
    internal static class CommandSupport
    {
        public static bool TryGetDocument(ExternalCommandData data, out UIDocument uidoc, out Document doc)
        {
            uidoc = data?.Application?.ActiveUIDocument;
            doc = uidoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show(FpfOptions.ProductName, "Open a project model first.");
                return false;
            }
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(FpfOptions.ProductName, "This tool works in project documents, not in the Family Editor.");
                return false;
            }
            if (doc.IsReadOnly)
            {
                TaskDialog.Show(FpfOptions.ProductName, "This document is read-only.");
                return false;
            }
            return true;
        }

        /// <summary>DirectShapes currently linked to the floors (for worksharing checkout).</summary>
        public static List<ElementId> LinkedShapeIds(Document doc, IEnumerable<Floor> floors)
        {
            var ids = new List<ElementId>();
            foreach (var f in floors)
            {
                var link = FpfStorage.ReadFloor(f);
                if (link == null) continue;
                foreach (var uid in link.DirectShapeUniqueIds)
                    if (doc.GetElement(uid) is DirectShape ds) ids.Add(ds.Id);
            }
            return ids;
        }

        /// <summary>
        /// Checks out floors + their shapes (outside any transaction) and returns an edit predicate for the service.
        /// Views are not bulk-borrowed (that would lock colleagues out); they are checked individually and Revit
        /// borrows them on edit.
        /// </summary>
        public static Func<ElementId, bool> PrepareEditability(Document doc, IList<Floor> floors)
        {
            var ids = floors.Select(f => f.Id).Concat(LinkedShapeIds(doc, floors)).ToList();
            var denied = Editability.AcquireOrGetDenied(doc, ids);
            return id => !denied.Contains(id) && Editability.CanEdit(doc, id);
        }

        /// <summary>Runs <paramref name="body"/> in one named, undoable transaction.</summary>
        public static bool RunTransaction(Document doc, string name, FpfReport report, Action body)
        {
            using (var t = new Transaction(doc, name))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(fho);

                if (t.Start() != TransactionStatus.Started)
                {
                    report.Errors.Add("Could not start transaction '" + name + "'.");
                    return false;
                }

                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    report.Errors.Add(ex.Message);
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    return false;
                }

                var status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    report.Errors.Add("Transaction '" + name + "' was not committed (" + status + "). No changes were made.");
                    return false;
                }
                return true;
            }
        }

        public static void Show(string title, FpfReport report, string headline)
        {
            var td = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = title,
                MainContent = report.ToText(headline),
                MainIcon = report.HasProblems ? TaskDialogIcon.TaskDialogIconWarning : TaskDialogIcon.TaskDialogIconNone
            };
            td.Show();
        }

        public static List<Floor> SelectedFloors(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            return uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<Floor>()
                .ToList();
        }

        /// <summary>Selected flattened floors, plus source floors of any selected flat-pattern DirectShapes.</summary>
        public static List<Floor> SelectedFlattenedFloors(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var result = new Dictionary<ElementId, Floor>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el is Floor f && FpfStorage.IsFlattened(f))
                {
                    result[f.Id] = f;
                }
                else if (el is DirectShape ds)
                {
                    var dl = FpfStorage.ReadDirectShape(ds);
                    if (dl != null && doc.GetElement(dl.SourceFloorUniqueId) is Floor src && FpfStorage.IsFlattened(src))
                        result[src.Id] = src;
                }
            }
            return result.Values.ToList();
        }

        internal sealed class FloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Floor;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
