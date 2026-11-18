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

                    case DialogAction.PickEdge2:
                    case DialogAction.PickLine2:
                        PickSecondaryAlignment(dlg.Action == DialogAction.PickEdge2);
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
            var hideLiveInView = _state.HideLiveInActiveView;
            var ok = CommandSupport.RunTransaction(_doc, "Flatten Floor Patterns", _report, () =>
            {
                View active = null;
                try { active = _uidoc?.ActiveView; } catch { /* ignore */ }
                var svc = new FlatPatternService(_doc, _report, canEdit, active);
                foreach (var f in floors)
                {
                    if (!f.IsValidObject) continue;
                    svc.Apply(f, ApplyMode.IfChanged, true, style, placement, hideLive, hideLiveInView);
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
            _state.Status = "Preview (not kept until Apply): " + Summary() + "\nPlacement: " + placement.Describe() + "." +
                            (_report.Skipped.Count > 0
                                ? "\nSkipped " + _report.Skipped.Count + " floor(s). Apply shows each id and the reason."
                                : "");
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
            LastHideLiveInActiveViewSetting.Save(_state.HideLiveInActiveView);
            var hint = "Control the hatch with a view filter (Filter button) using Projection Lines. " +
                       "Hatch lines are written in chunks (~25k lines per DirectShape); Tab still picks individual line references inside a chunk where Revit allows. " +
                       "Each FPF Floor Edge segment is its own element so you can delete one edge without losing the rest. " +
                       "When the pattern is small enough, chunks are grouped so one Move/Rotate can drag the whole hatch; otherwise move each chunk. " +
                       "To align: Align, pick the element, then Tab to the hatch line. Pattern rotation: this dialog (Align does not rotate the hatch). " +
                       "The floor outline is subcategory \"" + FpfOptions.EdgeSubcategoryName +
                       "\" (Object Styles → Model Objects → Generic Models). " +
                       "Refresh rebuilds edges you deleted. In a sheet file set the link to \"By Host View\" so its filters apply." +
                       (_state.HideLive ? "\n\nLive pattern hiding: run Flat Pattern Filters in the sheet file (adds \"" +
                                          FpfOptions.FilterLiveFloorName + "\" with its overrides; tick the fold-lines option)." : "");
            var title = _state.Title;
            if (report.Skipped.Count > 0)
                title += " (" + report.Skipped.Count + " floor(s) skipped)";
            CommandSupport.Show(title, report,
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
                if (!TryPickStraightLine(edge,
                        edge ? "Pick a straight edge with a direction in plan (slab edge, kerb, wall...)" :
                        "Pick a straight model line, detail line or grid",
                        requirePlanDirection: true,
                        out var line, out var click, out var dx, out var dy))
                    return;

                _state.Placement = new PatternPlacement
                {
                    AngleRad = Math.Atan2(dy, dx),
                    HasAnchor = true,
                    AnchorX = line.GetEndPoint(0).X,
                    AnchorY = line.GetEndPoint(0).Y
                };
                _state.Status = "Alignment 1 (" + (edge ? "edge" : "line") + "): " +
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

        private void PickSecondaryAlignment(bool edge)
        {
            try
            {
                if (!_state.Placement.HasAnchor)
                {
                    _state.Status = "Pick alignment 1 first (Align to Edge or Align to Line).";
                    return;
                }
                if (_picked == null || _picked.Count == 0)
                {
                    _state.Status = "Select at least one floor before alignment 2.";
                    return;
                }

                if (!TryPickStraightLine(edge,
                        edge ? "Pick a second straight edge (typically across the pattern joints)..." :
                        "Pick a second straight line or grid (typically perpendicular to alignment 1)...",
                        requirePlanDirection: false,
                        out var line, out var click, out var dx, out var dy))
                    return;

                var reader = new PatternReader(_doc);
                var hatchIn = FloorHatchBuilder.ReadInput(_picked[0], reader);
                PatternSource pattern = null;
                foreach (var piece in hatchIn.Pieces)
                {
                    if (piece?.Pattern != null) { pattern = piece.Pattern; break; }
                }
                pattern ??= reader.ForMaterial(FloorHatchBuilder.GetTypeTopMaterial(_picked[0]));
                var placement = _state.Placement.Clone();
                if (!placement.TrySlideFromSecondLine(pattern, click.X, click.Y, dx, dy,
                        out var slide, out var error, out var detail))
                {
                    _state.Status = error;
                    return;
                }

                placement.SlideAlongPrimary = slide;
                placement.HasSecondaryAlign = true;
                _state.Placement = placement;
                _state.Status = "Alignment 2 (" + (edge ? "edge" : "line") + "): " +
                                placement.Describe() + " (" + detail + "). Press Preview or Apply." +
                                (_state.PreviewLive ? " (The preview shown is still the previous one.)" : "");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // back to the dialog
            }
            catch (Exception ex)
            {
                _state.Status = "Alignment 2 failed: " + ex.Message;
            }
        }

        private bool TryPickStraightLine(bool edge, string prompt, bool requirePlanDirection, out Line line, out XYZ click,
            out double dx, out double dy)
        {
            line = null;
            click = null;
            dx = dy = 0;
            Reference r;
            Curve curve;
            if (edge)
            {
                r = _uidoc.Selection.PickObject(ObjectType.Edge, prompt);
                var el = _doc.GetElement(r);
                curve = (el?.GetGeometryObjectFromReference(r) as Edge)?.AsCurve();
                click = r.GlobalPoint;
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
                r = _uidoc.Selection.PickObject(ObjectType.Element, new LineFilter(), prompt);
                var el = _doc.GetElement(r);
                curve = el is CurveElement ce ? ce.GeometryCurve : (el as Grid)?.Curve;
                click = r.GlobalPoint;
            }

            if (!(curve is Line picked) || !picked.IsBound)
            {
                _state.Status = "Align: pick a straight edge or line (arcs and splines are not supported).";
                return false;
            }

            var a = picked.GetEndPoint(0);
            var b = picked.GetEndPoint(1);
            if (click != null && click.DistanceTo(b) < click.DistanceTo(a))
            {
                var t = a;
                a = b;
                b = t;
                picked = Line.CreateBound(a, b);
            }

            dx = b.X - a.X;
            dy = b.Y - a.Y;
            var planLen = Math.Sqrt(dx * dx + dy * dy);
            if (requirePlanDirection &&
                (planLen < 1.0e-6 || planLen < 0.01 * picked.Length))
            {
                _state.Status = "Align: that edge is vertical (no direction in plan). Pick another.";
                return false;
            }
            if (planLen < 1.0e-9)
            {
                _state.Status = "Align: the pick has no direction in plan.";
                return false;
            }

            line = picked;
            return true;
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
