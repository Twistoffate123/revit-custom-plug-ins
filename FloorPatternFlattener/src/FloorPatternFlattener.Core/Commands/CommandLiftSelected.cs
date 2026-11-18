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

                var picked = CollectFlattenedDirectShapes(uidoc);
                if (picked.Count == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName,
                        "Select one or more flattened pattern Generic Model elements " +
                        "(DirectShapes — Tab into the floor group if needed), then run Lift Selected again.");
                    return Result.Cancelled;
                }

                var mmText = AskLiftMm(commandData);
                if (mmText == null) return Result.Cancelled;
                if (!double.TryParse(mmText.Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var deltaMm) || double.IsNaN(deltaMm) || Math.Abs(deltaMm) < 1.0e-9)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "Enter a non-zero lift in millimetres (positive up, negative down).");
                    return Result.Cancelled;
                }

                var deltaFeet = FpfProjectParameters.MmToFeet(deltaMm);
                var canEdit = CommandSupport.PrepareEditability(doc, picked);
                var moved = 0;
                var report = new FpfReport();

                CommandSupport.RunTransaction(doc, "Lift Selected Flat Pattern", report, () =>
                {
                    foreach (var ds in picked)
                    {
                        if (!canEdit(ds.Id)) continue;
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

                TaskDialog.Show(FpfOptions.ProductName,
                    moved + " DirectShape(s) moved " + deltaMm.ToString("0.##", CultureInfo.InvariantCulture) +
                    " mm in world Z.\nExtra lift is stored on each element (FPF Extra Lift (mm)) and kept on Refresh.");
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

        private static List<DirectShape> CollectFlattenedDirectShapes(UIDocument uidoc)
        {
            var result = new List<DirectShape>();
            var doc = uidoc.Document;
            var seen = new HashSet<ElementId>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                AddIfDirectShape(doc, id, result, seen);
            }
            if (result.Count > 0) return result;
            try
            {
                var picked = uidoc.Selection.PickObjects(ObjectType.Element,
                    new DirectShapeFilter(), "Pick flat pattern DirectShape(s) (Tab to select inside a group)");
                foreach (var r in picked)
                    AddIfDirectShape(doc, r.ElementId, result, seen);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // user cancelled pick
            }
            return result;
        }

        private static void AddIfDirectShape(Document doc, ElementId id, List<DirectShape> result, HashSet<ElementId> seen)
        {
            if (id == null || seen.Contains(id)) return;
            seen.Add(id);
            if (!(doc.GetElement(id) is DirectShape ds)) return;
            var link = FpfStorage.ReadDirectShape(ds);
            if (link == null && ds.ApplicationId != FpfOptions.ApplicationId) return;
            result.Add(ds);
        }

        private sealed class DirectShapeFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) =>
                e is DirectShape ds && (ds.ApplicationId == FpfOptions.ApplicationId || FpfStorage.ReadDirectShape(ds) != null);

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}