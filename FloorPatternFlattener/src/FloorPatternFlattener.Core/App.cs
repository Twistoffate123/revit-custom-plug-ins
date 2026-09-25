using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Ribbon;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using FloorPatternFlattener.Updater;

namespace FloorPatternFlattener
{
    /// <summary>
    /// Add-in entry point: ribbon, Dynamic Model Update registration and an optional stale-hatch check on open.
    /// </summary>
    public class App : IExternalApplication
    {
        private AddInId _addInId;

        public Result OnStartup(UIControlledApplication application)
        {
            _addInId = application.ActiveAddInId;

            try
            {
                RibbonBuilder.Build(application);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(FpfOptions.ProductName, "Ribbon setup failed:\n" + ex.Message);
            }

            try
            {
                FlatPatternUpdater.Register(_addInId);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(FpfOptions.ProductName,
                    "Updater registration failed - flat patterns will not auto-update this session:\n" + ex.Message);
            }

            if (FpfOptions.CheckStaleOnOpen)
            {
                try { application.ControlledApplication.DocumentOpened += OnDocumentOpened; }
                catch { /* ignore */ }
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { application.ControlledApplication.DocumentOpened -= OnDocumentOpened; }
            catch { /* ignore */ }

            if (_addInId != null) FlatPatternUpdater.Unregister(_addInId);
            return Result.Succeeded;
        }

        /// <summary>
        /// Read-only: counts flattened floors whose hatch no longer matches the model (e.g. edited on a machine
        /// without the add-in) and suggests Refresh. Never modifies the document.
        /// </summary>
        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;

                var floors = FpfStorage.GetFlattenedFloors(doc);
                if (floors.Count == 0) return;

                var patterns = new PatternReader(doc);
                var stale = 0;
                foreach (var f in floors)
                    if (FlatPatternService.IsStale(f, patterns)) stale++;

                if (stale == 0) return;

                TaskDialog.Show(FpfOptions.ProductName,
                    stale + " of " + floors.Count + " flattened floor(s) in '" + doc.Title +
                    "' have an out-of-date flat pattern (the model was probably edited without the add-in).\n\n" +
                    "Run Custom Tools > Floor Patterns > Refresh Flattened Patterns to regenerate them.");
            }
            catch
            {
                // never block opening
            }
        }
    }
}
