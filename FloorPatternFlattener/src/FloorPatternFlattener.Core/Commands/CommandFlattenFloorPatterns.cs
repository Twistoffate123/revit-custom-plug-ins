using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Services;

namespace FloorPatternFlattener.Commands
{
    /// <summary>Flattens the selected floors (or picked floors): creates / refreshes their flat-pattern DirectShapes.</summary>
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

                var canEdit = CommandSupport.PrepareEditability(doc, floors);
                var report = new FpfReport();

                var ok = CommandSupport.RunTransaction(doc, "Flatten Floor Patterns", report, () =>
                {
                    var svc = new FlatPatternService(doc, report, canEdit);
                    foreach (var f in floors)
                    {
                        if (f.IsValidObject) svc.Apply(f, ApplyMode.IfChanged, true);
                    }
                });

                CommandSupport.Show(ok ? "Flatten Floor Patterns" : "Flatten Floor Patterns - failed", report,
                    floors.Count + " floor(s) processed.\nThe flat pattern is saved with the model and updates " +
                    "automatically. Style it with a view filter on Generic Models where Comments = \"" +
                    FpfOptions.CommentsTag + "\".");
                return ok ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
