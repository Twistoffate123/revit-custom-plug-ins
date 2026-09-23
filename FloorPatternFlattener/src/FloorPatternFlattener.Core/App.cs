using System;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Overlay;
using FloorPatternFlattener.Ribbon;

namespace FloorPatternFlattener
{
    /// <summary>
    /// Add-in entry point: ribbon + DirectContext3D server registration.
    /// </summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RibbonBuilder.Build(application);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Floor Pattern Flattener", "Ribbon setup failed:\n" + ex.Message);
            }

            try
            {
                FloorPatternOverlayServer.Register();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Floor Pattern Flattener",
                    "DirectContext3D server registration failed:\n" + ex.Message +
                    "\n\nCommands may still register the server on first use.");
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
