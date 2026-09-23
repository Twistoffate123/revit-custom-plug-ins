using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Overlay;
using FloorPatternFlattener.Session;

namespace FloorPatternFlattener.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandClearFlattenedPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show("Floor Pattern Flattener", "Open a model first.");
                    return Result.Cancelled;
                }

                var doc = uidoc.Document;
                var existing = FlattenSession.GetFloors(doc);
                if (existing.Count == 0)
                {
                    TaskDialog.Show("Floor Pattern Flattener", "No flattened floor patterns in this document.");
                    return Result.Succeeded;
                }

                var selectedFloorIds = uidoc.Selection.GetElementIds()
                    .Where(id => doc.GetElement(id) is Floor)
                    .ToList();

                IReadOnlyCollection<ElementId> toClear;
                if (selectedFloorIds.Count > 0)
                {
                    var set = new HashSet<ElementId>(existing);
                    var subset = selectedFloorIds.Where(id => set.Contains(id)).ToList();
                    toClear = subset.Count > 0 ? (IReadOnlyCollection<ElementId>)subset : existing;
                }
                else
                {
                    toClear = existing;
                }

                var snapshot = toClear.ToList();
                string restoreError = null;

                try
                {
                    FloorPatternVisibility.RestoreNativeSurfacePatterns(doc, uidoc.ActiveView, snapshot);
                }
                catch (Exception ex)
                {
                    restoreError = ex.Message;
                }

                if (snapshot.Count == existing.Count)
                    FlattenSession.ClearDocument(doc);
                else
                    FlattenSession.RemoveFloors(doc, snapshot);

                FloorPatternOverlayServer.InvalidateCache();
                FloorPatternOverlayServer.RefreshViews(doc, uidoc);

                if (restoreError != null)
                {
                    TaskDialog.Show("Floor Pattern Flattener",
                        $"Cleared overlay for {snapshot.Count} floor(s), but restoring native patterns failed:\n" +
                        restoreError);
                    return Result.Succeeded;
                }

                TaskDialog.Show("Floor Pattern Flattener",
                    $"Cleared flattened overlay for {snapshot.Count} floor(s).");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Floor Pattern Flattener", ex.Message);
                return Result.Failed;
            }
        }
    }
}
