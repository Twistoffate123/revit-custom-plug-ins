using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;

namespace FloorPatternFlattener.Updater
{
    /// <summary>Runs flat-pattern DMU work on the next Idling tick after the user's transaction ends.</summary>
    internal static class FlatPatternUpdaterDeferred
    {
        internal sealed class WorkItem
        {
            public string DocKey;
            public Document Doc;
            public readonly HashSet<ElementId> FloorIds = new HashSet<ElementId>();
            public readonly HashSet<ElementId> ChangedTypeIds = new HashSet<ElementId>();
            public readonly HashSet<string> ChangedDeps = new HashSet<string>();
            public readonly List<ElementId> NewViewIds = new List<ElementId>();
            public bool ValidateAll;
        }

        private static readonly Dictionary<string, WorkItem> _pending = new Dictionary<string, WorkItem>();
        private static readonly object _lock = new object();
        private static bool _idleHooked;
        private static bool _running;

        public static void Bind(UIControlledApplication application)
        {
            if (application == null || _idleHooked) return;
            application.Idling += OnIdling;
            _idleHooked = true;
        }

        public static void Enqueue(WorkItem item)
        {
            if (item?.Doc == null || string.IsNullOrEmpty(item.DocKey)) return;
            lock (_lock)
            {
                if (!_pending.TryGetValue(item.DocKey, out var existing))
                    _pending[item.DocKey] = item;
                else
                    Merge(existing, item);
            }
        }

        private static void Merge(WorkItem into, WorkItem from)
        {
            foreach (var id in from.FloorIds) into.FloorIds.Add(id);
            foreach (var id in from.ChangedTypeIds) into.ChangedTypeIds.Add(id);
            foreach (var uid in from.ChangedDeps) into.ChangedDeps.Add(uid);
            foreach (var id in from.NewViewIds) into.NewViewIds.Add(id);
            if (from.ValidateAll) into.ValidateAll = true;
        }

        private static void OnIdling(object sender, EventArgs e)
        {
            if (_running) return;
            List<WorkItem> batch;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                _running = true;
                batch = new List<WorkItem>(_pending.Values);
            }

            try
            {
                Drain(batch);
            }
            finally
            {
                _running = false;
            }
        }

        private static void Drain(List<WorkItem> batch)
        {

            foreach (var work in batch)
            {
                if (work.Doc == null || !work.Doc.IsValidObject)
                {
                    Remove(work.DocKey);
                    continue;
                }

                if (work.Doc.IsModifiable)
                    continue;

                Remove(work.DocKey);
                try
                {
                    FlatPatternUpdater.RunDeferred(work);
                }
                catch (Exception ex)
                {
                    FpfRefreshLog.LogUpdater("updater-deferred-failed doc=" + work.DocKey + " " + ex.Message);
                }
            }
        }

        private static void Remove(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return;
            lock (_lock) { _pending.Remove(docKey); }
        }

        public static WorkItem CreateWork(Document doc)
        {
            return new WorkItem
            {
                Doc = doc,
                DocKey = DocKey(doc)
            };
        }

        public static string DocKey(Document doc)
        {
            if (doc == null) return string.Empty;
            try { return doc.PathName ?? doc.Title ?? doc.GetHashCode().ToString(); }
            catch { return doc.GetHashCode().ToString(); }
        }
    }
}