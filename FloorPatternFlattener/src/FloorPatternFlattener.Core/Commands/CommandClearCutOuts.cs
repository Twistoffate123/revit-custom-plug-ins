using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandClearCutOuts : IExternalCommand
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
                    TaskDialog.Show(FpfOptions.ProductName,
                        "Select a flattened floor, its output group, or a pattern DirectShape, then run Clear Cut-Outs.");
                    return Result.Cancelled;
                }

                var report = new FpfReport();
                using (FpfRefreshLog.BeginSession("Clear Cut-Outs"))
                {
                    var canEdit = CommandSupport.PrepareEditability(doc, floors);
                    foreach (var floor in floors)
                    {
                        FpfRefreshLog.Current?.BeginFloor(floor);
                        if (!canEdit(floor.Id))
                        {
                            report.AddNotEditable(floor, "floor not editable");
                            continue;
                        }
                        FpfStorage.DeleteCutouts(floor);
                        var svc = new FlatPatternService(doc, report, canEdit, uidoc.ActiveView);
                        svc.Apply(floor, ApplyMode.Force, applyViewGraphics: false);
                    }
                }

                TaskDialog.Show(FpfOptions.ProductName, report.ToText("Cleared cut-outs on " + floors.Count + " floor(s)."));
                return report.HasProblems ? Result.Failed : Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}