using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Overlay;
using FloorPatternFlattener.Session;

namespace FloorPatternFlattener.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandFlattenFloorPatterns : IExternalCommand
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
            FloorPatternOverlayServer.EnsureRegistered();

            List<ElementId> floorIds;
            try
            {
                floorIds = CollectFloors(uidoc, doc);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            if (floorIds == null || floorIds.Count == 0)
            {
                TaskDialog.Show("Floor Pattern Flattener",
                    "No Floor elements selected.\n\nSelect one or more Floors, then run Flatten Floor Patterns again.");
                return Result.Cancelled;
            }

            FlattenSession.AddFloors(doc, floorIds);

            try
            {
                if (uidoc.ActiveView != null)
                    FloorPatternVisibility.HideNativeSurfacePatterns(doc, uidoc.ActiveView, floorIds);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Floor Pattern Flattener",
                    "Overlay registered, but native pattern hide failed:\n" + ex.Message);
            }

            FloorPatternOverlayServer.RefreshViews(doc, uidoc);

            TaskDialog.Show("Floor Pattern Flattener",
                $"Flattened hatch overlay active for {floorIds.Count} floor(s).\n\n" +
                "Open or refresh a 3D view to see plan-flat patterns. " +
                "Sheets that place that 3D view will show the overlay when DirectContext3D is available.");

            return Result.Succeeded;
        }

        private static List<ElementId> CollectFloors(UIDocument uidoc, Document doc)
        {
            var selected = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<Floor>()
                .Select(f => f.Id)
                .ToList();

            if (selected.Count > 0)
                return selected;

            var refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new FloorSelectionFilter(),
                "Select Floor elements to flatten patterns");

            return refs.Select(r => r.ElementId).Distinct().ToList();
        }

        private sealed class FloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Floor;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}
