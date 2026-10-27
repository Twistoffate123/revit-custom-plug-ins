using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using FloorPatternFlattener.UI;

namespace FloorPatternFlattener.Commands
{
    /// <summary>Flattens the selected floors: dialog (style, rotate/align, scope) with Preview / Apply / Discard.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandFlattenFloorPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var floors = CommandSupport.SelectedFloors(uidoc);
                if (floors.Count == 0)
                {
                    try
                    {
                        var refs = uidoc.Selection.PickObjects(ObjectType.Element,
                            new CommandSupport.FloorSelectionFilter(),
                            "Select floors to flatten patterns (Finish when done)");
                        floors = refs.Select(r => doc.GetElement(r.ElementId)).OfType<Floor>()
                            .GroupBy(f => f.Id).Select(g => g.First()).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                if (floors.Count == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "No floors selected.");
                    return Result.Cancelled;
                }

                var state = new FlattenDialogState
                {
                    Title = "Flatten Floor Patterns",
                    Style = LastStyleSetting.Load(),
                    Placement = PatternPlacement.Identity,
                    Info = floors.Count + " floor(s) selected. With angle 0 and no anchor the flat pattern matches the " +
                           "live floor pattern exactly. Already flattened floors are replaced. Use Edit to change one " +
                           "keeping its stored settings."
                };
                return new FlattenSession(commandData.Application, floors, state).Run();
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Re-opens one flattened floor with its stored style / rotation / anchor; Apply replaces, Discard keeps.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandEditFlattenedPattern : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var floors = CommandSupport.SelectedFlattenedFloors(uidoc);
                if (floors.Count == 0)
                {
                    try
                    {
                        var r = uidoc.Selection.PickObject(ObjectType.Element, new FlattenedFloorFilter(),
                            "Pick a flattened floor (or its flat pattern) to edit");
                        var f = CommandSupport.SelectedFlattenedFloors(uidoc, new[] { r.ElementId }).FirstOrDefault();
                        if (f != null) floors.Add(f);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }
                if (floors.Count == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "That is not a flattened floor. Use Flatten first.");
                    return Result.Cancelled;
                }

                var first = floors[0];
                var state = new FlattenDialogState
                {
                    Title = "Edit Flattened Pattern",
                    Style = FpfStorage.ReadStyle(first),
                    Placement = FpfStorage.ReadPlacement(first),
                    Info = (floors.Count == 1 ? FpfReport.Describe(first) : floors.Count + " flattened floors (settings of " +
                               FpfReport.Describe(first) + " loaded)") +
                           ". Change the style, angle or alignment, Preview, then Apply to replace it - " +
                           "or Discard to keep the current flat pattern."
                };
                return new FlattenSession(commandData.Application, floors, state).Run();
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private sealed class FlattenedFloorFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) =>
                (elem is Floor && FpfStorage.IsFlattened(elem)) ||
                (elem is DirectShape && FpfStorage.ReadDirectShape(elem) != null);

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
