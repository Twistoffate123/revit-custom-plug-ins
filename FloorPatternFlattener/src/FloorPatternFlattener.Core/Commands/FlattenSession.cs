using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.UI;

namespace FloorPatternFlattener.Commands
{
    /// <summary>
    /// The Flatten / Edit workflow: dialog -> optional Preview (built inside a TransactionGroup and shown in the view)
    /// -> Apply (group assimilated into one undo step) or Discard (group rolled back, model untouched).
    /// </summary>
    internal sealed class FlattenSession
    {
        private readonly UIApplication _uiapp;
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private readonly List<Floor> _picked;
        private readonly FlattenDialogState _state;

        private TransactionGroup _group;
        private FlattenDialogState _previewState;
        private FpfReport _report;
        private int _floorCount;
        private int _maxFloorSegments;
        private string _maxFloor = string.Empty;

        public FlattenSession(UIApplication uiapp, List<Floor> picked, FlattenDialogState state)
        {
            _uiapp = uiapp;
            _uidoc = uiapp.ActiveUIDocument;
            _doc = _uidoc.Document;
            _picked = picked;
            _state = state;
        }

        public Result Run()
        {
            try
            {
                return Loop();
            }
            finally
            {
                // Anything still open (exception, Revit closing the command) is rolled back: nothing half-applied.
                RollBackPreview();
            }
        }

        private Result Loop()
        {
            while (true)
            {
                var dlg = new FlattenDialog(_state, MainWindow());
                dlg.ShowDialog();
                switch (dlg.Action)
                {
                    case DialogAction.PickEdge:
                    case DialogAction.PickLine:
                        PickAlignment(dlg.Action == DialogAction.PickEdge);
                        continue;

                    case DialogAction.Preview:
                        RollBackPreview();
                        if (!Build()) continue;
                        try { _uidoc.RefreshActiveView(); } catch { /* ignore */ }
                        continue;

                    case DialogAction.Apply:
                        if (_group == null || !_state.SameSettings(_previewState))
                        {
                            RollBackPreview();
                            if (!Build()) continue;
                        }
                        if (_maxFloorSegments > FpfOptions.HeavyPatternConfirmSegments && !ConfirmHeavy())
                        {
                            try { _uidoc.RefreshActiveView(); } catch { /* ignore */ }
                            continue; // back to the dialog with the preview still shown
                        }
                        return Commit();

                    default: // Cancel / Discard / window closed
                        RollBackPreview();
                        return Result.Cancelled;
                }
            }
        }

        // ------------------------------------------------------------------ build / commit / discard

        private bool Build()
        {
            var floors = ResolveScope();
            if (floors.Count == 0)
            {
                _state.Status = "No floors to flatten in this scope.";
                return false;
            }

            var canEdit = CommandSupport.PrepareEditability(_doc, floors);
            _report = new FpfReport();
            _maxFloorSegments = 0;
            _maxFloor = string.Empty;
            _floorCount = floors.Count;

            _group = new TransactionGroup(_doc, "Flatten Floor Patterns");
            if (_group.Start() != TransactionStatus.Started)
            {
                _group.Dispose();
                _group = null;
                _state.Status = "Could not start the preview.";
                return false;
            }

            var style = _state.Style;
            var placement = _state.Placement.Clone();
            var hideLive = _state.HideLive;
            var ok = CommandSupport.RunTransaction(_doc, "Flatten Floor Patterns", _report, () =>
            {
                var svc = new FlatPatternService(_doc, _report, canEdit);
                foreach (var f in floors)
                {
                    if (!f.IsValidObject) continue;
                    svc.Apply(f, ApplyMode.IfChanged, true, style, placement, hideLive);
                    if (svc.LastFloorSegments > _maxFloorSegments)
                    {
                        _maxFloorSegments = svc.LastFloorSegments;
                        _maxFloor = FpfReport.Describe(f);
                    }
                }
            });

            if (!ok)
            {
                RollBackPreview();
                _state.Status = "Failed: " + string.Join("; ", _report.Errors.Take(2));
                return false;
            }

            _previewState = _state.Clone();
            _state.PreviewLive = true;
            _state.Status = "Preview (not kept until Apply): " + Summary() + "\nPlacement: " + placement.Describe() + ".";
            return true;
        }

        private string Summary()
        {
            return _floorCount + " floor(s), " + _report.Segments.ToString("N0") + " lines" +
                   (_maxFloorSegments > FpfOptions.HeavyPatternConfirmSegments
                       ? " - heavy: " + _maxFloor + " has " + _maxFloorSegments.ToString("N0") + " lines"
                       : "") +
                   (_report.Unchanged > 0 ? " (" + _report.Unchanged + " already up to date)" : "");
        }

        private bool ConfirmHeavy()
        {
            var td = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = _maxFloor + " gets " + _maxFloorSegments.ToString("N0") + " lines.",
                MainContent = "More than " + FpfOptions.HeavyPatternConfirmSegments.ToString("N0") +
                              " modelled lines on one floor slow down views and file size. Is modelled geometry really " +
                              "needed here? (A coarser pattern is lighter.)",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            return td.Show() == TaskDialogResult.Yes;
        }

        private Result Commit()
        {
            var report = _report;
            var status = _group.Assimilate();
            _group.Dispose();
            _group = null;
            if (status != TransactionStatus.Committed)
            {
                TaskDialog.Show(FpfOptions.ProductName, "The flat pattern could not be applied (" + status + ").");
                return Result.Failed;
            }

            LastStyleSetting.Save(_state.Style);
            LastHideLiveSetting.Save(_state.HideLive);
            var hint = "Control the lines with a view filter (Filter button) using Projection Lines; in a sheet file " +
                       "set the link to \"By Host View\" so its filters apply." +
                       (_state.HideLive ? "\n\nLive pattern hiding: run Flat Pattern Filters in the sheet file (adds \"" +
                                          FpfOptions.FilterLiveFloorName + "\" with its overrides; tick the fold-lines option)." : "");
            CommandSupport.Show(_state.Title, report,
                Summary() + "\nPlacement: " + _state.Placement.Describe() + ".\nEach floor keeps its style, rotation and " +
                "anchor for Refresh, Update and automatic rebuilds.\n\n" + hint);
            return Result.Succeeded;
        }

        private void RollBackPreview()
        {
            if (_group == null) return;
            try
            {
                if (_group.HasStarted()) _group.RollBack();
            }
            catch
            {
                // ignore
            }
            _group.Dispose();
            _group = null;
            _previewState = null;
            _state.PreviewLive = false;
            try { _uidoc.RefreshActiveView(); } catch { /* ignore */ }
        }

        // ------------------------------------------------------------------ scope

        private List<Floor> ResolveScope()
        {
            var result = _picked.Where(f => f.IsValidObject).ToDictionary(f => f.Id, f => f);
            if (_state.Scope == ApplyScope.AllVisibleOfType)
            {
                var types = new HashSet<ElementId>(result.Values.Select(f => f.GetTypeId()));
                var view = _doc.ActiveView;
                try
                {
                    if (view != null && !(view is ViewSheet) && !(view is ViewSchedule))
                    {
                        foreach (var f in new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Floor)).Cast<Floor>())
                            if (types.Contains(f.GetTypeId())) result[f.Id] = f;
                    }
                }
                catch
                {
                    // view cannot be collected - selected floors only
                }
            }
            return result.Values.ToList();
        }

        // ------------------------------------------------------------------ Align to Edge / Line

        private void PickAlignment(bool edge)
        {
            try
            {
                Reference r;
                Curve curve;
                XYZ click;
                if (edge)
                {
                    r = _uidoc.Selection.PickObject(ObjectType.Edge,
                        "Pick a straight edge with a direction in plan (slab edge, kerb, wall...)");
                    var el = _doc.GetElement(r);
                    curve = (el?.GetGeometryObjectFromReference(r) as Edge)?.AsCurve();
                    click = r.GlobalPoint;
                    // Edges of family instances may come back in family coordinates: move them if they miss the click.
                    if (curve != null && el is FamilyInstance fi && click != null)
                    {
                        try
                        {
                            if (curve.Distance(click) > 0.05)
                                curve = curve.CreateTransformed(fi.GetTotalTransform());
                        }
                        catch
                        {
                            // keep as is
                        }
                    }
                }
                else
                {
                    r = _uidoc.Selection.PickObject(ObjectType.Element, new LineFilter(),
                        "Pick a straight model line, detail line or grid");
                    var el = _doc.GetElement(r);
                    curve = el is CurveElement ce ? ce.GeometryCurve : (el as Grid)?.Curve;
                    click = r.GlobalPoint;
                }

                if (!(curve is Line line) || !line.IsBound)
                {
                    _state.Status = "Align: pick a straight edge or line (arcs and splines are not supported).";
                    return;
                }

                var a = line.GetEndPoint(0);
                var b = line.GetEndPoint(1);
                if (click != null && click.DistanceTo(b) < click.DistanceTo(a))
                {
                    var t = a;
                    a = b;
                    b = t;
                }
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < 1.0e-6 || Math.Sqrt(dx * dx + dy * dy) < 0.01 * line.Length)
                {
                    _state.Status = "Align: that edge is vertical (no direction in plan). Pick another.";
                    return;
                }

                _state.Placement = new PatternPlacement
                {
                    AngleRad = Math.Atan2(dy, dx),
                    HasAnchor = true,
                    AnchorX = a.X,
                    AnchorY = a.Y
                };
                _state.Status = "Aligned to the picked " + (edge ? "edge" : "line") + ": " +
                                _state.Placement.Describe() + ". Press Preview or Apply." +
                                (_state.PreviewLive ? " (The preview shown is still the previous one.)" : "");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // back to the dialog
            }
            catch (Exception ex)
            {
                _state.Status = "Align failed: " + ex.Message;
            }
        }

        private sealed class LineFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) =>
                (elem is CurveElement ce && ce.GeometryCurve is Line) || (elem is Grid g && g.Curve is Line);

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private IntPtr MainWindow()
        {
            try { return _uiapp.MainWindowHandle; } catch { return IntPtr.Zero; }
        }
    }
}
