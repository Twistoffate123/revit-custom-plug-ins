using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>
    /// Per-view graphics: keeps the live surface pattern of flattened floors visible (removing the per-element
    /// "pattern hidden" override older versions set; all other element overrides are preserved) and hides
    /// generated DirectShapes in section-like views.
    /// All methods must run inside an open transaction or an updater's Execute.
    /// </summary>
    public static class ViewGraphics
    {
        /// <summary>Non-template views that accept element graphic overrides.</summary>
        public static List<View> CollectOverrideViews(Document doc)
        {
            var list = new List<View>();
            foreach (var el in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                if (el is View v && IsOverrideCandidate(v)) list.Add(v);
            }
            return list;
        }

        public static bool IsOverrideCandidate(View v)
        {
            if (v == null) return false;
            try
            {
                if (v.IsTemplate) return false;
                if (v is ViewSheet || v is ViewSchedule || v is ViewDrafting) return false;
                if (v.ViewType == ViewType.Legend || v.ViewType == ViewType.ProjectBrowser ||
                    v.ViewType == ViewType.SystemBrowser || v.ViewType == ViewType.Undefined ||
                    v.ViewType == ViewType.Internal || v.ViewType == ViewType.Report)
                    return false;
                return v.AreGraphicsOverridesAllowed();
            }
            catch
            {
                return false;
            }
        }

        public static bool IsSectionLike(View v)
        {
            if (v == null) return false;
            var t = v.ViewType;
            return t == ViewType.Section || t == ViewType.Elevation || t == ViewType.Detail;
        }

        /// <summary>
        /// Sets only the surface foreground/background pattern visibility of the element override in the view.
        /// Returns true if something changed.
        /// </summary>
        public static bool SetNativeSurfacePatternVisible(View view, ElementId elementId, bool visible)
        {
            var ogs = view.GetElementOverrides(elementId) ?? new OverrideGraphicSettings();
            if (ogs.IsSurfaceForegroundPatternVisible == visible && ogs.IsSurfaceBackgroundPatternVisible == visible)
                return false;
            ogs.SetSurfaceForegroundPatternVisible(visible);
            ogs.SetSurfaceBackgroundPatternVisible(visible);
            view.SetElementOverrides(elementId, ogs);
            return true;
        }

        /// <summary>
        /// Keeps the live floor surface pattern visible unless <see cref="FpfOptions.HideNativeSurfacePatterns"/> is on.
        /// The add-in must never leave a flattened floor with patterns turned off via its own element overrides.
        /// </summary>
        public static bool SyncNativeSurfacePattern(View view, ElementId elementId)
        {
            if (FpfOptions.HideNativeSurfacePatterns)
                return SetNativeSurfacePatternVisible(view, elementId, false);
            return SetNativeSurfacePatternVisible(view, elementId, true);
        }

        /// <summary>
        /// Restores live pattern + unhides flat-pattern DirectShapes in a plan-like view; returns a one-line diagnostic.
        /// </summary>
        public static string EnsurePlanViewVisibility(Document doc, View view, Floor floor, IEnumerable<DirectShape> shapes,
            double outputSampleZFeet)
        {
            if (doc == null || view == null || !view.IsValidObject) return "view=invalid";
            var sb = new StringBuilder();
            sb.Append("viewId=").Append(view.Id.ToString());
            try { sb.Append(" name=").Append(view.Name ?? "?"); } catch { /* ignore */ }

            if (floor != null)
            {
                var live = SyncNativeSurfacePattern(view, floor.Id);
                sb.Append(" livePattern=").Append(live ? "set-visible" : "unchanged");
                try
                {
                    var ogs = view.GetElementOverrides(floor.Id);
                    if (ogs != null)
                        sb.Append(" floorOvrFg=").Append(ogs.IsSurfaceForegroundPatternVisible)
                            .Append(" floorOvrBg=").Append(ogs.IsSurfaceBackgroundPatternVisible);
                }
                catch { /* ignore */ }
            }

            var gm = new ElementId(BuiltInCategory.OST_GenericModel);
            try { sb.Append(" genericModelHidden=").Append(view.GetCategoryHidden(gm)); } catch { /* ignore */ }

            var unhide = new List<ElementId>();
            if (floor != null) unhide.Add(floor.Id);
            var hiddenShapes = 0;
            if (shapes != null)
            {
                foreach (var ds in shapes)
                {
                    if (ds == null || !ds.IsValidObject) continue;
                    try
                    {
                        if (ds.IsHidden(view)) { unhide.Add(ds.Id); hiddenShapes++; }
                    }
                    catch { /* ignore */ }
                }
            }

            if (hiddenShapes > 0)
            {
                try
                {
                    view.UnhideElements(unhide);
                    sb.Append(" unhidShapes=").Append(hiddenShapes.ToString(CultureInfo.InvariantCulture));
                }
                catch
                {
                    sb.Append(" unhideFailed=");
                }
            }

            foreach (var fid in view.GetFilters())
            {
                try
                {
                    if (!(doc.GetElement(fid) is ParameterFilterElement pfe)) continue;
                    var name = pfe.Name ?? string.Empty;
                    if (!name.StartsWith("FPF", StringComparison.OrdinalIgnoreCase)) continue;
                    var vis = view.GetFilterVisibility(fid);
                    var ogs = view.GetFilterOverrides(fid);
                    sb.Append(" filter[").Append(name).Append("] vis=").Append(vis);
                    if (ogs != null)
                        sb.Append(" surfFg=").Append(ogs.IsSurfaceForegroundPatternVisible)
                            .Append(" surfBg=").Append(ogs.IsSurfaceBackgroundPatternVisible);
                }
                catch { /* ignore */ }
            }

            if (double.IsFinite(outputSampleZFeet))
                sb.Append(" outputZ=").Append(outputSampleZFeet.ToString("0.####", CultureInfo.InvariantCulture));
            if (view is ViewPlan && GeneratedElevationVerifier.TryGetPlanCutZ(view, doc, out var cut))
                sb.Append(' ').Append(cut);

            return sb.ToString();
        }

        /// <summary>Hides the given elements in the view (skipping ones already hidden / not hideable).</summary>
        public static void HideInView(View view, IEnumerable<Element> elements)
        {
            var ids = new List<ElementId>();
            foreach (var e in elements)
            {
                if (e == null) continue;
                try
                {
                    if (!e.IsHidden(view) && e.CanBeHidden(view)) ids.Add(e.Id);
                }
                catch
                {
                    // ignore element
                }
            }
            if (ids.Count > 0) view.HideElements(ids);
        }

        /// <summary>
        /// Applies an action to every candidate view, isolating failures per view and skipping views that are
        /// not editable (worksharing).
        /// </summary>
        public static void ForEachView(IEnumerable<View> views, Func<ElementId, bool> canEdit, FpfReport report,
            Action<View> action)
        {
            foreach (var v in views)
            {
                if (v == null || !v.IsValidObject) continue;
                if (canEdit != null && !canEdit(v.Id))
                {
                    report?.ViewsSkippedNotEditable.Add(v.Id);
                    continue;
                }
                try
                {
                    action(v);
                }
                catch (Exception)
                {
                    if (report != null) report.ViewFailures++;
                }
            }
        }

        public static List<View> SectionLike(IEnumerable<View> views) => views.Where(IsSectionLike).ToList();
    }
}
