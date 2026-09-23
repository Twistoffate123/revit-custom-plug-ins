using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Session
{
    /// <summary>
    /// Per-document store of Floor ElementIds that should receive a flat hatch overlay.
    /// Keyed by document hash so multiple open documents remain independent.
    /// In-memory only — not saved with the RVT.
    /// </summary>
    public static class FlattenSession
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, HashSet<ElementId>> Store =
            new Dictionary<int, HashSet<ElementId>>();
        private static int _generation;

        /// <summary>
        /// Bumps whenever the floor set changes so overlay caches can invalidate.
        /// </summary>
        public static int Generation
        {
            get { lock (Gate) return _generation; }
        }

        public static int DocumentKey(Document doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return doc.GetHashCode();
        }

        public static void AddFloors(Document doc, IEnumerable<ElementId> floorIds)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (floorIds == null) return;

            var key = DocumentKey(doc);
            lock (Gate)
            {
                if (!Store.TryGetValue(key, out var set))
                {
                    set = new HashSet<ElementId>();
                    Store[key] = set;
                }

                var changed = false;
                foreach (var id in floorIds)
                {
                    if (id != null && id != ElementId.InvalidElementId && set.Add(id))
                        changed = true;
                }

                if (changed)
                    _generation++;
            }
        }

        public static void RemoveFloors(Document doc, IEnumerable<ElementId> floorIds)
        {
            if (doc == null || floorIds == null) return;
            var key = DocumentKey(doc);
            lock (Gate)
            {
                if (!Store.TryGetValue(key, out var set)) return;
                var changed = false;
                foreach (var id in floorIds)
                {
                    if (set.Remove(id))
                        changed = true;
                }
                if (set.Count == 0)
                    Store.Remove(key);
                if (changed)
                    _generation++;
            }
        }

        public static void ClearDocument(Document doc)
        {
            if (doc == null) return;
            lock (Gate)
            {
                if (Store.Remove(DocumentKey(doc)))
                    _generation++;
            }
        }

        public static IReadOnlyCollection<ElementId> GetFloors(Document doc)
        {
            if (doc == null) return Array.Empty<ElementId>();
            lock (Gate)
            {
                if (!Store.TryGetValue(DocumentKey(doc), out var set) || set.Count == 0)
                    return Array.Empty<ElementId>();
                return new List<ElementId>(set);
            }
        }

        public static bool HasAny(Document doc)
        {
            if (doc == null) return false;
            lock (Gate)
            {
                return Store.TryGetValue(DocumentKey(doc), out var set) && set.Count > 0;
            }
        }

        public static void RemoveInvalidIds(Document doc)
        {
            if (doc == null) return;
            var key = DocumentKey(doc);
            lock (Gate)
            {
                if (!Store.TryGetValue(key, out var set)) return;
                var dead = new List<ElementId>();
                foreach (var id in set)
                {
                    if (doc.GetElement(id) == null)
                        dead.Add(id);
                }

                if (dead.Count == 0) return;

                foreach (var id in dead)
                    set.Remove(id);
                if (set.Count == 0)
                    Store.Remove(key);
                _generation++;
            }
        }
    }
}
