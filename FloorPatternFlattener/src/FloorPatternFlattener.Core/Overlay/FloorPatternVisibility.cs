using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Overlay
{
    /// <summary>
    /// Best-effort: hide native model surface patterns on flattened floors in the active view
    /// so the DirectContext3D overlay is not doubled with the skewed pattern.
    /// Availability of surface-pattern visibility overrides varies by Revit year.
    /// </summary>
    public static class FloorPatternVisibility
    {
        public static void HideNativeSurfacePatterns(Document doc, View view, IEnumerable<ElementId> floorIds)
        {
            if (doc == null || view == null || floorIds == null) return;
            if (view.IsTemplate) return;

            using (var tx = new Transaction(doc, "Hide floor surface patterns (overlay)"))
            {
                tx.Start();
                foreach (var id in floorIds)
                {
                    try
                    {
                        var ogs = view.GetElementOverrides(id) ?? new OverrideGraphicSettings();
                        TrySetSurfacePatternVisible(ogs, false);
                        view.SetElementOverrides(id, ogs);
                    }
                    catch
                    {
                    }
                }
                tx.Commit();
            }
        }

        public static void RestoreNativeSurfacePatterns(Document doc, View view, IEnumerable<ElementId> floorIds)
        {
            if (doc == null || view == null || floorIds == null) return;
            if (view.IsTemplate) return;

            using (var tx = new Transaction(doc, "Restore floor surface patterns"))
            {
                tx.Start();
                foreach (var id in floorIds)
                {
                    try
                    {
                        view.SetElementOverrides(id, new OverrideGraphicSettings());
                    }
                    catch
                    {
                    }
                }
                tx.Commit();
            }
        }

        private static void TrySetSurfacePatternVisible(OverrideGraphicSettings ogs, bool visible)
        {
            try
            {
                ogs.SetSurfaceForegroundPatternVisible(visible);
            }
            catch
            {
            }

            try
            {
                ogs.SetSurfaceBackgroundPatternVisible(visible);
            }
            catch
            {
            }

            try
            {
                var mi = typeof(OverrideGraphicSettings).GetMethod(
                    "SetSurfacePatternVisible",
                    new[] { typeof(bool) });
                mi?.Invoke(ogs, new object[] { visible });
            }
            catch
            {
            }
        }
    }
}
