using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace FloorPatternFlattener.Ribbon
{
    public static class RibbonBuilder
    {
        public const string PreferredTabName = "Custom Tools";
        public const string FallbackTabName = "Add-Ins";
        public const string PanelName = "Floor Patterns";

        public static void Build(UIControlledApplication app)
        {
            var tabName = TryCreateTab(app, PreferredTabName)
                ? PreferredTabName
                : FallbackTabName;

            if (tabName == PreferredTabName)
                EnsureTab(app, PreferredTabName);

            RibbonPanel panel;
            try
            {
                panel = GetOrCreatePanel(app, tabName, PanelName);
            }
            catch
            {
                panel = GetOrCreatePanel(app, FallbackTabName, PanelName);
            }

            AddButtons(panel);
        }

        private static void AddButtons(RibbonPanel panel)
        {
            var asm = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData(
                "FPF_FlattenFloorPatterns",
                "Flatten Floor\nPatterns",
                asm,
                "FloorPatternFlattener.Commands.CommandFlattenFloorPatterns")
            {
                ToolTip = "Draw plan-flat hatch overlays for selected floors in 3D views (DirectContext3D).",
                LongDescription =
                    "Registers selected Floor elements so their finish hatch is drawn on a horizontal plane " +
                    "at the floor top elevation, clipped to the plan outline. Does not create Floor elements " +
                    "or filled regions."
            });

            panel.AddItem(new PushButtonData(
                "FPF_ClearFlattenedPatterns",
                "Clear Flattened\nPatterns",
                asm,
                "FloorPatternFlattener.Commands.CommandClearFlattenedPatterns")
            {
                ToolTip = "Remove plan-flat hatch overlays for floors in this document.",
                LongDescription =
                    "Clears the session store for the active document (or for selected flattened floors) " +
                    "and restores native surface pattern visibility overrides when possible."
            });
        }

        private static bool TryCreateTab(UIControlledApplication app, string tabName)
        {
            try
            {
                app.CreateRibbonTab(tabName);
                return true;
            }
            catch
            {
                try
                {
                    app.GetRibbonPanels(tabName);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void EnsureTab(UIControlledApplication app, string tabName)
        {
            try { app.CreateRibbonTab(tabName); }
            catch { /* exists */ }
        }

        private static RibbonPanel GetOrCreatePanel(UIControlledApplication app, string tabName, string panelName)
        {
            foreach (var p in app.GetRibbonPanels(tabName))
            {
                if (string.Equals(p.Name, panelName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return app.CreateRibbonPanel(tabName, panelName);
        }
    }
}
