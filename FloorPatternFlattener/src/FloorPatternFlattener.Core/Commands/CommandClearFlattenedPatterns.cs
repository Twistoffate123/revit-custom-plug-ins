using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Commands
{
    /// <summary>
    /// Removes flat patterns from the selected flattened floors (or flat-pattern shapes), or from all floors in the
    /// document when nothing flattened is selected. Restores only the native surface pattern visibility.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandClearFlattenedPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var floors = CommandSupport.SelectedFlattenedFloors(uidoc);
                var all = false;
                if (floors.Count == 0)
                {
                    floors = FpfStorage.GetFlattenedFloors(doc);
                    var orphans = FpfStorage.GetGeneratedDirectShapes(doc).Count;
                    if (floors.Count == 0 && orphans == 0)
                    {
                        TaskDialog.Show(FpfOptions.ProductName, "There are no flattened floors in this document.");
                        return Result.Cancelled;
                    }

                    var answer = TaskDialog.Show(FpfOptions.ProductName,
                        "No flattened floor is selected.\n\nClear the flat pattern from ALL " + floors.Count +
                        " flattened floor(s) in this document?",
                        TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.No);
                    if (answer != TaskDialogResult.Yes) return Result.Cancelled;
                    all = true;
                }

                var canEdit = CommandSupport.PrepareEditability(doc, floors);
                var report = new FpfReport();

                var ok = CommandSupport.RunTransaction(doc, "Clear Flattened Patterns", report, () =>
                {
                    var svc = new FlatPatternService(doc, report, canEdit);
                    foreach (var f in floors)
                    {
                        if (f.IsValidObject) svc.Clear(f);
                    }
                    if (all) svc.CleanupOrphans();
                });

                CommandSupport.Show(ok ? "Clear Flattened Patterns" : "Clear Flattened Patterns - failed", report,
                    "Native surface patterns are visible again on cleared floors (other overrides untouched).");
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
