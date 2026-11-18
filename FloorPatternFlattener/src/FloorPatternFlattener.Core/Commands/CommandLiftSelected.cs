using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using FloorPatternFlattener.UI;
using System.Windows.Interop;

namespace FloorPatternFlattener.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandLiftSelected : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                FpfProjectParameters.EnsureBound(doc);

                if (!CollectFlattenedDirectShapes(uidoc, out var picked, out var selectionNote))
                    return Result.Cancelled;

                var mmText = AskLiftMm(commandData);
                if (mmText == null) return Result.Cancelled;
                if (!double.TryParse(mmText.Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var deltaMm) || double.IsNaN(deltaMm) || Math.Abs(deltaMm) < 1.0e-9)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "Enter a non-zero lift in millimetres (positive up, negative down).");
                    return Result.Cancelled;
                }

                var pickedCount = picked.Count;
                var deltaFeet = FpfProjectParameters.MmToFeet(deltaMm);
                var canEdit = CommandSupport.PrepareEditability(doc, picked);
                var moved = 0;
                var skippedEdit = 0;
                var report = new FpfReport();

                CommandSupport.RunTransaction(doc, "Lift Selected Flat Pattern", report, () =>
                {
                    foreach (var ds in picked)
                    {
                        if (!canEdit(ds.Id))
                        {
                            skippedEdit++;
                            continue;
                        }
                        try
                        {
                            ElementTransformUtils.MoveElement(doc, ds.Id, new XYZ(0, 0, deltaFeet));
                            var prev = FpfProjectParameters.ReadExtraLiftMm(ds) ?? 0;
                            var next = prev + deltaMm;
                            FpfProjectParameters.WriteExtraLiftMm(ds, next);
                            var link = FpfStorage.ReadDirectShape(ds) ?? new DirectShapeLinkData();
                            link.ExtraLiftMm = next;
                            FpfStorage.WriteDirectShape(ds, link);
                            moved++;
                        }
                        catch (Exception ex)
                        {
                            report.Errors.Add(FpfReport.Describe(ds) + ": " + ex.Message);
                        }
                    }
                });

                var summary = pickedCount + " flat pattern element(s) picked; " + moved + " lifted " +
                              deltaMm.ToString("0.##", CultureInfo.InvariantCulture) + " mm in world Z.";
                if (skippedEdit > 0) summary += "\n" + skippedEdit + " could not be edited (worksharing).";
                if (!string.IsNullOrEmpty(selectionNote)) summary += "\n\n" + selectionNote;
                summary += "\n\nExtra lift is on each element (FPF Extra Lift (mm)) and kept on Refresh.";
                if (report.Errors.Count > 0)
                    summary += "\n\n" + string.Join("\n", report.Errors.Take(3));
                TaskDialog.Show(FpfOptions.ProductName, summary);
                return moved > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static string AskLiftMm(ExternalCommandData commandData)
        {
            var dlg = new LiftMmDialog(5);
            try
            {
                var hwnd = commandData.Application.MainWindowHandle;
                if (hwnd != IntPtr.Zero) new WindowInteropHelper(dlg).Owner = hwnd;
            }
            catch { /* ignore */ }
            if (dlg.ShowDialog() != true) return null;
            return dlg.ValueMm.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Pre-selection (including whole model groups), then window/crossing rectangle, then click pick.
        /// </summary>
        private static bool CollectFlattenedDirectShapes(UIDocument uidoc, out List<DirectShape> result, out string selectionNote)
        {
            result = new List<DirectShape>();
            selectionNote = string.Empty;
            var doc = uidoc.Document;
            var seen = new HashSet<ElementId>();
            var selectedIds = uidoc.Selection.GetElementIds().ToList();

            var groupsInSelection = 0;
            var directInSelection = 0;
            if (selectedIds.Count > 0)
            {
                foreach (var id in selectedIds)
                {
                    var el = doc.GetElement(id);
                    if (el is Group g)
                    {
                        groupsInSelection++;
                        AddFlattenedMembersFromGroup(doc, g, result, seen);
                    }
                    else if (AddIfDirectShape(doc, id, result, seen))
                        directInSelection++;
                }

                if (result.Count > 0 && groupsInSelection > 0 && directInSelection == 0)
                {
                    if (!ConfirmLiftAllInGroups(groupsInSelection, result.Count))
                        return false;
                    selectionNote = "Whole group: all " + result.Count + " flattened DirectShape(s) in the selected group(s).";
                    return true;
                }

                if (result.Count > 0)
                {
                    if (groupsInSelection > 0)
                        selectionNote = "Pre-selection: " + result.Count + " DirectShape(s) (including members of " +
                                        groupsInSelection + " selected group(s)).";
                    else
                        selectionNote = "Pre-selection: " + result.Count + " DirectShape(s).";
                    return true;
                }
            }

            try
            {
                var boxed = uidoc.Selection.PickElementsByRectangle(
                    new DirectShapeFilter(),
                    "Drag a window (left to right) or crossing (right to left) box over flat pattern lines. " +
                    "Select member DirectShapes inside the floor model group, not the group header.");
                var boxGroupHits = 0;
                foreach (var el in boxed)
                {
                    if (el is Group g)
                    {
                        boxGroupHits++;
                        AddFlattenedMembersFromGroup(doc, g, result, seen);
                    }
                    else
                        AddIfDirectShape(doc, el.Id, result, seen);
                }
                if (result.Count > 0)
                {
                    selectionNote = "Rectangle: " + result.Count + " DirectShape(s) in the box.";
                    if (boxGroupHits > 0)
                        selectionNote += " (expanded " + boxGroupHits + " group(s) to members.)";
                    return true;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }

            try
            {
                var refs = uidoc.Selection.PickObjects(ObjectType.Element, new DirectShapeFilter(),
                    "Pick flat pattern DirectShape(s), or Cancel and drag a rectangle on the next run");
                foreach (var r in refs)
                    AddIfDirectShape(doc, r.ElementId, result, seen);
                if (result.Count > 0)
                {
                    selectionNote = "Click pick: " + result.Count + " DirectShape(s).";
                    return true;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }

            TaskDialog.Show(FpfOptions.ProductName,
                "No flat pattern DirectShapes were found.\n\n" +
                "• Pre-select the floor model group to lift all lines, or Tab-select individual chunks.\n" +
                "• Or run again and drag a window/crossing box over the pattern lines inside the group.");
            return false;
        }

        private static bool ConfirmLiftAllInGroups(int groupCount, int shapeCount)
        {
            var td = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = "Lift all flat pattern lines in the selected group?",
                MainContent = shapeCount + " flattened DirectShape(s) in " + groupCount + " model group(s) " +
                              "will get the same lift amount.",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
            };
            return td.Show() == TaskDialogResult.Ok;
        }

        private static void AddFlattenedMembersFromGroup(Document doc, Group group, List<DirectShape> result, HashSet<ElementId> seen)
        {
            if (group == null || !group.IsValidObject) return;
            ICollection<ElementId> members;
            try { members = group.GetMemberIds(); }
            catch { return; }
            if (members == null) return;
            foreach (var mid in members)
                AddIfDirectShape(doc, mid, result, seen);
        }

        private static bool AddIfDirectShape(Document doc, ElementId id, List<DirectShape> result, HashSet<ElementId> seen)
        {
            if (id == null || seen.Contains(id)) return false;
            seen.Add(id);
            if (!(doc.GetElement(id) is DirectShape ds)) return false;
            if (!IsFlattenedOutput(ds)) return false;
            result.Add(ds);
            return true;
        }

        private static bool IsFlattenedOutput(DirectShape ds) =>
            ds != null && (ds.ApplicationId == FpfOptions.ApplicationId || FpfStorage.ReadDirectShape(ds) != null);

        private sealed class DirectShapeFilter : ISelectionFilter
        {
            public bool AllowElement(Element e)
            {
                if (e is DirectShape ds) return IsFlattenedOutput(ds);
                // Reject group headers so rectangle/pre-pick targets member geometry, not the group shell.
                return false;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}