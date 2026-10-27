using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using FloorPatternFlattener.UI;

namespace FloorPatternFlattener.Commands
{
    /// <summary>Ribbon toggle "Auto: On / Auto: Off" for the automatic rebuild (remembered between sessions).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class CommandToggleAutoUpdate : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            AutoUpdateSetting.Enabled = !AutoUpdateSetting.Enabled;
            Ribbon.RibbonBuilder.RefreshAutoButton();
            return Result.Succeeded;
        }
    }

    /// <summary>Rebuilds selected flattened floors, one picked floor, or all of them, with progress and Cancel.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandUpdateFlattenedPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var all = FpfStorage.GetFlattenedFloors(doc);
                if (all.Count == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "There are no flattened floors in this document.");
                    return Result.Cancelled;
                }

                var selected = CommandSupport.SelectedFlattenedFloors(uidoc);
                var td = new TaskDialog(FpfOptions.ProductName)
                {
                    MainInstruction = "Update which flat patterns?",
                    MainContent = "Each floor is rebuilt with its stored style, rotation and anchor. Auto update is " +
                                  (AutoUpdateSetting.Enabled ? "On." : "Off - this is how to bring floors up to date."),
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                    AllowCancellation = true
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    selected.Count > 0 ? "Selected (" + selected.Count + ")" : "Pick one floor");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "All flattened floors (" + all.Count + ")");

                List<Floor> floors;
                switch (td.Show())
                {
                    case TaskDialogResult.CommandLink1:
                        floors = selected;
                        if (floors.Count == 0)
                        {
                            try
                            {
                                var r = uidoc.Selection.PickObject(ObjectType.Element, "Pick a flattened floor (or its flat pattern)");
                                floors = CommandSupport.SelectedFlattenedFloors(uidoc, new[] { r.ElementId });
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                return Result.Cancelled;
                            }
                            if (floors.Count == 0)
                            {
                                TaskDialog.Show(FpfOptions.ProductName, "That is not a flattened floor.");
                                return Result.Cancelled;
                            }
                        }
                        break;
                    case TaskDialogResult.CommandLink2:
                        floors = all;
                        break;
                    default:
                        return Result.Cancelled;
                }

                return Run(commandData.Application, doc, floors, floors.Count == all.Count);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static Result Run(UIApplication uiapp, Document doc, List<Floor> floors, bool everything)
        {
            var canEdit = CommandSupport.PrepareEditability(doc, floors);
            var report = new FpfReport();
            IntPtr owner;
            try { owner = uiapp.MainWindowHandle; } catch { owner = IntPtr.Zero; }

            var done = 0;
            var cancelled = false;
            var progress = new ProgressWindow("Update Flattened Patterns", floors.Count, owner);
            progress.Show();
            using (var group = new TransactionGroup(doc, "Update Flattened Patterns"))
            {
                group.Start();
                try
                {
                    foreach (var f in floors)
                    {
                        if (!progress.Step(done, "Floor " + (done + 1) + " of " + floors.Count + ": " +
                                                 (f.IsValidObject ? FpfReport.Describe(f) : "?")))
                        {
                            cancelled = true;
                            break;
                        }
                        if (f.IsValidObject)
                        {
                            CommandSupport.RunTransaction(doc, "Update Flat Pattern", report, () =>
                            {
                                var svc = new FlatPatternService(doc, report, canEdit);
                                svc.Apply(f, ApplyMode.Force, true);
                            });
                        }
                        done++;
                    }
                    if (everything && !cancelled)
                    {
                        CommandSupport.RunTransaction(doc, "Clean up flat patterns", report, () =>
                        {
                            var svc = new FlatPatternService(doc, report, canEdit);
                            svc.CleanupOrphans();
                            svc.HideAllShapesInSectionViews();
                        });
                    }
                    progress.Step(done, "Done.");
                }
                finally
                {
                    if (group.HasStarted()) group.Assimilate(); // keep what is done, even after Cancel: one undo step
                    progress.Close();
                }
            }

            CommandSupport.Show("Update Flattened Patterns", report,
                done + " of " + floors.Count + " flattened floor(s) updated" +
                (cancelled ? " - cancelled, the rest were left as they were." : "."));
            return Result.Succeeded;
        }
    }
}
