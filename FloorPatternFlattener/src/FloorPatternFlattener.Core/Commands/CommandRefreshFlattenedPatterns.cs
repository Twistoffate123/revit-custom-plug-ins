using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Commands
{
    /// <summary>Force-regenerates every flattened floor in the document, re-applies view graphics, removes orphans.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandRefreshFlattenedPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out _, out var doc))
                    return Result.Cancelled;

                var floors = FpfStorage.GetFlattenedFloors(doc);
                var orphans = FpfStorage.GetGeneratedDirectShapes(doc).Count;
                if (floors.Count == 0 && orphans == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "There are no flattened floors in this document.");
                    return Result.Cancelled;
                }

                var canEdit = CommandSupport.PrepareEditability(doc, floors);
                var report = new FpfReport();

                var ok = CommandSupport.RunTransaction(doc, "Refresh Flattened Patterns", report, () =>
                {
                    var svc = new FlatPatternService(doc, report, canEdit);
                    foreach (var f in floors)
                    {
                        if (f.IsValidObject) svc.Apply(f, ApplyMode.Force, true);
                    }
                    svc.CleanupOrphans();
                    svc.HideAllShapesInSectionViews();
                });

                CommandSupport.Show(ok ? "Refresh Flattened Patterns" : "Refresh Flattened Patterns - failed", report,
                    floors.Count + " flattened floor(s) regenerated.");
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
