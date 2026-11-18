using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using FloorPatternFlattener.UI;
using FloorPatternFlattener.Updater;

namespace FloorPatternFlattener.Commands
{
    /// <summary>Regenerates every flattened floor in the document (old Surface-style floors are rebuilt as Lines), re-applies view graphics, removes orphans.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandRefreshFlattenedPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var floors = FpfStorage.GetFlattenedFloors(doc);
                var orphans = FpfStorage.GetGeneratedDirectShapes(doc).Count;
                if (floors.Count == 0 && orphans == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "There are no flattened floors in the document.");
                    return Result.Cancelled;
                }

                var canEdit = CommandSupport.PrepareEditability(doc, floors);
                var report = new FpfReport();
                var svc = new FlatPatternService(doc, report, canEdit, uidoc?.ActiveView);

                IntPtr owner;
                try { owner = commandData.Application.MainWindowHandle; } catch { owner = IntPtr.Zero; }

                using (FpfRefreshLog.BeginSession("Refresh Flattened Patterns"))
                using (FlatPatternUpdaterGate.EnterBulkOperation())
                {
                    ProgressWindow progress = null;
                    var done = 0;
                    var cancelled = false;
                    try
                    {
                        progress = new ProgressWindow("Refresh Flattened Patterns", floors.Count + 1, owner);
                        progress.Show();
                    }
                    catch
                    {
                        progress = null;
                    }

                    var ok = CommandSupport.RunRefreshFloorsTransactionGroup(doc, "Refresh Flattened Patterns", report, floors,
                        f =>
                        {
                            if (progress != null && !progress.Step(done, "Floor " + (done + 1) + " of " + floors.Count +
                                                                         ": " + (f.IsValidObject ? FpfReport.Describe(f) : "?")))
                            {
                                cancelled = true;
                                return;
                            }

                            if (!cancelled) svc.Apply(f, ApplyMode.Force, false);
                            done++;
                        },
                        () =>
                        {
                            if (cancelled) return;
                            if (progress != null && !progress.Step(done, "View cleanup")) cancelled = true;
                            if (cancelled) return;
                            foreach (var f in floors)
                            {
                                if (f.IsValidObject) svc.SyncNativePatternInActiveViewOnly(f.Id);
                            }

                            svc.CleanupOrphans();
                            svc.HideAllShapesInSectionViews();
                        });

                    progress?.Close();

                    if (cancelled)
                    {
                        TaskDialog.Show(FpfOptions.ProductName, "Refresh cancelled.");
                        return Result.Cancelled;
                    }

                    CommandSupport.Show(ok ? "Refresh Flattened Patterns" : "Refresh Flattened Patterns - failed", report,
                        floors.Count + " flattened floor(s) regenerated.");
                    return ok ? Result.Succeeded : Result.Failed;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}