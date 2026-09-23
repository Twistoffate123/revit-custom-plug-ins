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
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "No active document.";
                return Result.Failed;
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
                toClear = selectedFloorIds.Where(id => set.Contains(id)).ToList();
                if (toClear.Count == 0)
                    toClear = existing;
            }
            else
            {
                toClear = existing;
            }

            var snapshot = toClear.ToList();

            try
            {
                if (uidoc.ActiveView != null)
                    FloorPatternVisibility.RestoreNativeSurfacePatterns(doc, uidoc.ActiveView, snapshot);
            }
            catch
            {
            }

            if (snapshot.Count == existing.Count)
                FlattenSession.ClearDocument(doc);
            else
                FlattenSession.RemoveFloors(doc, snapshot);

            FloorPatternOverlayServer.RefreshViews(doc, uidoc);

            TaskDialog.Show("Floor Pattern Flattener",
                $"Cleared flattened overlay for {snapshot.Count} floor(s).");

            return Result.Succeeded;
        }
    }
}
