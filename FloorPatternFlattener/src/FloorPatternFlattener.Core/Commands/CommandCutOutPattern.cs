using System;
using System.Collections.Generic;
using System.Globalization;
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
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandCutOutPattern : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var opts = CutOutWorkflow.AskOptions(LastCutoutOffsetSetting.Load(), LastCutoutOutlineSetting.Load());
                if (opts == null) return Result.Cancelled;
                LastCutoutOffsetSetting.Save(opts.OffsetMm);
                LastCutoutOutlineSetting.Save(opts.EmitOutline);

                Floor floor;
                var data = new FloorCutoutData { EmitCutoutOutline = opts.EmitOutline };

                if (opts.SketchMode)
                {
                    if (!TryPickFloor(uidoc, out floor)) return Result.Cancelled;
                    data = FpfStorage.ReadCutouts(floor);
                    data.EmitCutoutOutline = opts.EmitOutline;
                    if (!CutOutWorkflow.TryAddSketch(uidoc, data))
                    {
                        TaskDialog.Show(FpfOptions.ProductName, "Need a closed sketch with at least three points.");
                        return Result.Cancelled;
                    }
                }
                else
                {
                    var obstructionRefs = CollectObstructions(uidoc, doc);
                    if (obstructionRefs.Count == 0)
                    {
                        TaskDialog.Show(FpfOptions.ProductName, "Pick at least one obstruction to cut around.");
                        return Result.Cancelled;
                    }
                    if (!TryPickFloor(uidoc, out floor)) return Result.Cancelled;
                    data = FpfStorage.ReadCutouts(floor);
                    data.EmitCutoutOutline = opts.EmitOutline;
                    foreach (var reference in obstructionRefs)
                    {
                        var key = CutoutFootprint.StableRefKey(doc, reference);
                        if (string.IsNullOrEmpty(key)) continue;
                        if (data.Picked.Any(p => p.ElementUniqueId == key)) continue;
                        data.Picked.Add(new PickedCutout { ElementUniqueId = key, OffsetMm = opts.OffsetMm });
                    }
                }

                if (data.IsEmpty)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "No cut-outs to apply.");
                    return Result.Cancelled;
                }

                var report = new FpfReport();
                using (FpfRefreshLog.BeginSession("Cut Out Pattern"))
                {
                    var canEdit = CommandSupport.PrepareEditability(doc, new List<Floor> { floor });
                    if (!canEdit(floor.Id))
                    {
                        TaskDialog.Show(FpfOptions.ProductName, "The flattened floor is not editable.");
                        return Result.Failed;
                    }
                    FpfRefreshLog.Current?.BeginFloor(floor);
                    CommandSupport.RunTransaction(doc, "Cut Out Pattern", report, () =>
                    {
                        FpfStorage.WriteCutouts(floor, data);
                        var svc = new FlatPatternService(doc, report, canEdit, uidoc.ActiveView);
                        svc.Apply(floor, ApplyMode.Force, applyViewGraphics: false);
                    });
                }

                TaskDialog.Show(FpfOptions.ProductName, report.ToText("Cut-outs applied to " + FpfReport.Describe(floor) + "."));
                return report.HasProblems ? Result.Failed : Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static List<Reference> CollectObstructions(UIDocument uidoc, Document doc)
        {
            var refs = new List<Reference>();
            var floors = CommandSupport.SelectedFlattenedFloors(uidoc);
            var ids = uidoc.Selection.GetElementIds();
            if (floors.Count == 1 && ids.Count > 1)
            {
                var floor = floors[0];
                foreach (var id in ids)
                {
                    if (id == floor.Id) continue;
                    var el = doc.GetElement(id);
                    if (el is DirectShape || el is Group) continue;
                    if (el != null) refs.Add(new Reference(el));
                }
                if (refs.Count > 0) return refs;
            }

            try
            {
                refs.AddRange(uidoc.Selection.PickObjects(ObjectType.Element, new CutObstructionFilter(),
                    "Pick obstructions on the host model (Finish)"));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return refs;
            }

            return refs;
        }

        private static bool TryPickFloor(UIDocument uidoc, out Floor floor)
        {
            floor = null;
            var flattened = CommandSupport.SelectedFlattenedFloors(uidoc);
            if (flattened.Count == 1)
            {
                floor = flattened[0];
                return true;
            }
            try
            {
                var r = uidoc.Selection.PickObject(ObjectType.Element, new FlattenedOutputFilter(),
                    "Pick the flattened floor, output group, or a pattern line");
                floor = ResolveFloor(uidoc.Document, r);
                return floor != null;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }
        }

        internal static Floor ResolveFloor(Document doc, Reference reference)
        {
            if (doc == null || reference == null) return null;
            var el = doc.GetElement(reference.ElementId);
            if (el is Floor f && FpfStorage.IsFlattened(f)) return f;
            if (el is DirectShape ds)
            {
                var link = FpfStorage.ReadDirectShape(ds);
                if (link != null && doc.GetElement(link.SourceFloorUniqueId) is Floor src && FpfStorage.IsFlattened(src))
                    return src;
            }
            if (el is Group group)
            {
                foreach (var id in group.GetMemberIds())
                {
                    if (doc.GetElement(id) is DirectShape member)
                    {
                        var link = FpfStorage.ReadDirectShape(member);
                        if (link != null && doc.GetElement(link.SourceFloorUniqueId) is Floor src && FpfStorage.IsFlattened(src))
                            return src;
                    }
                }
            }
            return null;
        }

        private sealed class CutObstructionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem == null) return false;
                if (elem is Floor) return false;
                if (elem is DirectShape ds && FpfStorage.ReadDirectShape(ds) != null) return false;
                if (elem is Group) return false;
                return true;
            }

            public bool AllowReference(Reference reference, XYZ position) => reference != null;
        }

        private sealed class FlattenedOutputFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) =>
                (elem is Floor && FpfStorage.IsFlattened(elem)) ||
                (elem is DirectShape && FpfStorage.ReadDirectShape(elem) != null) ||
                elem is Group;

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }

    internal static class CutOutWorkflow
    {
        public sealed class Options
        {
            public bool SketchMode;
            public double OffsetMm;
            public bool EmitOutline;
        }

        public static Options AskOptions(double defaultOffset, bool defaultOutline)
        {
            var td = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = "Cut Out Pattern",
                MainContent = "Remove flat hatch and edge lines under other elements.\n\n" +
                              "Pick mode (default): obstructions, then flattened output.\n" +
                              "Sketch mode: closed loop on the floor."
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Pick obstructions");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sketch cut-out loop");
            var result = td.Show();
            if (result == TaskDialogResult.Cancel) return null;

            var sketch = result == TaskDialogResult.CommandLink2;
            var offsetMm = 0.0;
            if (!sketch)
            {
                var dlg = new LiftMmDialog(defaultOffset) { Title = FpfOptions.ProductName + " – cut-out offset" };
                if (dlg.ShowDialog() != true) return null;
                offsetMm = Math.Max(0, dlg.ValueMm);
            }

            var outlineTd = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = "Draw cut-out edge lines?",
                MainContent = "When enabled, each cut footprint is also drawn as edge lines."
            };
            outlineTd.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "No");
            outlineTd.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Yes");
            var outlineResult = outlineTd.Show();
            var emit = outlineResult == TaskDialogResult.CommandLink2;
            if (outlineResult == TaskDialogResult.Cancel) emit = defaultOutline;

            return new Options { SketchMode = sketch, OffsetMm = offsetMm, EmitOutline = emit };
        }

        public static bool TryAddSketch(UIDocument uidoc, FloorCutoutData data)
        {
            var doc = uidoc.Document;
            var td = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = "Sketch source",
                MainContent = "Pick model/detail lines forming a loop, or click polygon corners."
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Pick lines");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Pick points");
            var sketchMode = td.Show();
            if (sketchMode == TaskDialogResult.Cancel) return false;

            try
            {
                SketchCutout sketch;
                if (sketchMode == TaskDialogResult.CommandLink1)
                {
                    var refs = uidoc.Selection.PickObjects(ObjectType.Element, new LineSketchFilter(),
                        "Pick model or detail lines (Finish)");
                    var curves = new List<Curve>();
                    foreach (var r in refs)
                    {
                        var curve = CurveFromElement(doc.GetElement(r.ElementId));
                        if (curve != null) curves.Add(curve);
                    }
                    var sketches = CutoutPlanCurve.SketchesFromModelCurves(curves);
                    if (sketches.Count == 0) return false;
                    foreach (var s in sketches)
                        data.Sketches.Add(s);
                    return true;
                }
                else
                {
                    sketch = new SketchCutout();
                    while (true)
                    {
                        try
                        {
                            var p = uidoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Intersections,
                                "Click polygon corners (Esc when done)");
                            sketch.Points.Add(new[] { p.X, p.Y });
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                if (sketch == null || sketch.Points.Count < 3) return false;
                if (CutoutCurveMath.IsDegenerateLoop(sketch.Points, out _)) return false;
                data.Sketches.Add(sketch);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }
        }

        private static Curve CurveFromElement(Element el)
        {
            if (el is ModelLine ml) return ml.GeometryCurve;
            if (el is DetailLine dl) return dl.GeometryCurve;
            return null;
        }

        private sealed class LineSketchFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is ModelLine || elem is DetailLine;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }

    internal static class LastCutoutOffsetSetting
    {
        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloorPatternFlattener", "cutout-offset-mm.txt");

        public static double Load()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return 0;
                if (double.TryParse(System.IO.File.ReadAllText(FilePath).Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && v >= 0) return v;
            }
            catch { /* ignore */ }
            return 0;
        }

        public static void Save(double mm)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                System.IO.File.WriteAllText(FilePath, mm.ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch { /* ignore */ }
        }
    }

    internal static class LastCutoutOutlineSetting
    {
        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloorPatternFlattener", "cutout-outline.txt");

        public static bool Load()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return false;
                var t = System.IO.File.ReadAllText(FilePath).Trim();
                return t == "1" || string.Equals(t, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Save(bool emit)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                System.IO.File.WriteAllText(FilePath, emit ? "1" : "0");
            }
            catch { /* ignore */ }
        }
    }
}