using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Overlay
{
    /// <summary>
    /// Hide or restore native model surface patterns on flattened floors in 3D views
    /// so the DirectContext3D overlay is not doubled with the skewed pattern.
    /// All writes run inside a Transaction unless the document is already modifiable.
    /// </summary>
    public static class FloorPatternVisibility
    {
        public static void HideNativeSurfacePatterns(Document doc, View preferredView, IEnumerable<ElementId> floorIds)
        {
            Apply(doc, preferredView, floorIds, hide: true);
        }

        public static void RestoreNativeSurfacePatterns(Document doc, View preferredView, IEnumerable<ElementId> floorIds)
        {
            Apply(doc, preferredView, floorIds, hide: false);
        }

        private static void Apply(Document doc, View preferredView, IEnumerable<ElementId> floorIds, bool hide)
        {
            if (doc == null || floorIds == null) return;
            if (doc.IsReadOnly) return;

            var ids = floorIds.Where(id => id != null && id != ElementId.InvalidElementId).ToList();
            if (ids.Count == 0) return;

            var views = CollectTargetViews(doc, preferredView);
            if (views.Count == 0) return;

            var name = hide ? "Hide floor surface patterns" : "Restore floor surface patterns";
            var startedHere = false;
            Transaction tx = null;

            try
            {
                if (!doc.IsModifiable)
                {
                    tx = new Transaction(doc, name);
                    if (tx.Start() != TransactionStatus.Started)
                        return;
                    startedHere = true;
                }

                foreach (var view in views)
                {
                    foreach (var id in ids)
                    {
                        if (doc.GetElement(id) == null) continue;

                        if (hide)
                        {
                            var ogs = view.GetElementOverrides(id) ?? new OverrideGraphicSettings();
                            SetSurfacePatternVisible(ogs, false);
                            view.SetElementOverrides(id, ogs);
                        }
                        else
                        {
                            view.SetElementOverrides(id, new OverrideGraphicSettings());
                        }
                    }
                }

                if (startedHere)
                    tx.Commit();
            }
            catch
            {
                if (startedHere && tx != null && tx.GetStatus() == TransactionStatus.Started)
                    tx.RollBack();
                throw;
            }
            finally
            {
                if (startedHere)
                    tx?.Dispose();
            }
        }

        private static List<View> CollectTargetViews(Document doc, View preferredView)
        {
            var list = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => v != null && !v.IsTemplate)
                .Cast<View>()
                .ToList();

            if (preferredView is View3D live && !live.IsTemplate && list.All(v => v.Id != live.Id))
                list.Add(live);

            return list;
        }

        private static void SetSurfacePatternVisible(OverrideGraphicSettings ogs, bool visible)
        {
            ogs.SetSurfaceForegroundPatternVisible(visible);
            ogs.SetSurfaceBackgroundPatternVisible(visible);
        }
    }
}
