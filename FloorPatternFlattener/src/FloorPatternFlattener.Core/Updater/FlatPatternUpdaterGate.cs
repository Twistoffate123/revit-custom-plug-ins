using System;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Updater
{
    /// <summary>Suspends flat-pattern DMU during bulk Refresh / Flatten to avoid re-entrant regeneration.</summary>
    public static class FlatPatternUpdaterGate
    {
        private static int _suppressDepth;

        public static bool IsSuppressed => _suppressDepth > 0;

        public static IDisposable EnterBulkOperation()
        {
            _suppressDepth++;
            try
            {
                var id = FlatPatternUpdater.RegisteredUpdaterId;
                if (id != null) UpdaterRegistry.DisableUpdater(id);
            }
            catch
            {
                // API missing or updater not registered
            }

            return new Token();
        }

        private sealed class Token : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (_suppressDepth > 0) _suppressDepth--;
                try
                {
                    var id = FlatPatternUpdater.RegisteredUpdaterId;
                    if (id != null) UpdaterRegistry.EnableUpdater(id);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}