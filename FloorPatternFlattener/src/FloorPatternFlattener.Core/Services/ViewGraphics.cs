using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>
    /// Per-view graphics: hides ONLY the native surface patterns of flattened floors (all other element
    /// overrides are preserved) and hides generated DirectShapes in section-like views.
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
