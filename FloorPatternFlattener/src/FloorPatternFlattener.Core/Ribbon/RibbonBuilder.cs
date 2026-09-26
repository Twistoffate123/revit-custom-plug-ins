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
            var tabName = TryCreateTab(app, PreferredTabName) ? PreferredTabName : FallbackTabName;

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
                ToolTip = "Replace the native surface pattern of the selected floors with a permanent, plan-true " +
                          "flat pattern (real model lines) that follows the slope.",
                LongDescription =
                    "Generates the material's fill pattern in plan (aligned to the project internal origin), projects it " +
                    "vertically onto every top face of the floor and stores it as a pinned Generic Model DirectShape " +
                    "(Comments = \"" + FpfOptions.CommentsTag + "\"). The native surface pattern is hidden per view. " +
                    "The flat pattern is saved with the model and regenerates automatically when the floor, its type, " +
                    "material or fill pattern changes."
            });

            panel.AddItem(new PushButtonData(
                "FPF_ClearFlattenedPatterns",
                "Clear Flattened\nPatterns",
                asm,
                "FloorPatternFlattener.Commands.CommandClearFlattenedPatterns")
            {
                ToolTip = "Remove flat patterns from the selected flattened floors (or all floors in the document " +
                          "if none are selected) and show the native surface pattern again.",
                LongDescription =
                    "Deletes the generated DirectShapes, removes the add-in data from the floors and turns the native " +
                    "surface pattern visibility back on. Other view overrides are left untouched. Use this before " +
                    "uninstalling the add-in."
            });

            panel.AddItem(new PushButtonData(
                "FPF_RefreshFlattenedPatterns",
                "Refresh Flattened\nPatterns",
                asm,
                "FloorPatternFlattener.Commands.CommandRefreshFlattenedPatterns")
            {
                ToolTip = "Force-regenerate the flat patterns of all flattened floors in this document.",
                LongDescription =
                    "Useful after the model was edited without the add-in, after an add-in upgrade, or when some " +
                    "elements were skipped because they were not editable (worksharing). Also re-applies view overrides " +
                    "and removes orphaned flat-pattern shapes."
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
