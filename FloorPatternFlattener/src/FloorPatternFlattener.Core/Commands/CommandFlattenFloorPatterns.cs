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
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show("Floor Pattern Flattener", "Open a model first.");
                    return Result.Cancelled;
                }

                var doc = uidoc.Document;
                if (doc.IsReadOnly)
                {
                    TaskDialog.Show("Floor Pattern Flattener", "This document is read-only. Overlay graphics can still draw after Flatten, but native pattern hide will be skipped.");
                }

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

                if (floorIds == null || floorIds.Count == 0)
                {
                    TaskDialog.Show("Floor Pattern Flattener",
                        "No Floor elements selected.\n\nSelect one or more Floors, then run Flatten Floor Patterns again.");
                    return Result.Cancelled;
                }

                FlattenSession.AddFloors(doc, floorIds);

                try
                {
                    FloorPatternVisibility.HideNativeSurfacePatterns(doc, uidoc.ActiveView, floorIds);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Floor Pattern Flattener",
                        "Overlay is active, but hiding native surface patterns failed:\n" + ex.Message +
                        "\n\nWorkshared elements owned by another user, or a detached model, can cause this.");
                }

                FloorPatternOverlayServer.InvalidateCache();
                FloorPatternOverlayServer.RefreshViews(doc, uidoc);

                TaskDialog.Show("Floor Pattern Flattener",
                    $"Flattened hatch overlay active for {floorIds.Count} floor(s).\n\n" +
                    "Open or refresh a 3D view to see plan-flat patterns. " +
                    "Native surface patterns are hidden in non-template 3D views when the document is writable.\n\n" +
                    "This overlay is session-only. It is not saved in the RVT. Re-run Flatten after reopen.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Floor Pattern Flattener", ex.Message);
                return Result.Failed;
            }
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
