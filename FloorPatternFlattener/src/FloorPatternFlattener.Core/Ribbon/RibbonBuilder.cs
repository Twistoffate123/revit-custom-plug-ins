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
                ToolTip = "Add a permanent flat copy of the selected floors' surface patterns (foreground and background). " +
                          "Asks for a style: Lines (exact match, filters control the lines) or Surface (thin surface with the " +
                          "floor's patterns, filters control the pattern itself). The live floor pattern is left as it is.",
                LongDescription =
                    "Lines: the material's fill pattern is generated in plan (aligned to the project internal origin), " +
                    "projected onto every top face and stored as pinned Generic Model lines (Comments = \"" +
                    FpfOptions.CommentsTag + "\", types \"" + FpfOptions.ForegroundTypeName + "\" / \"" +
                    FpfOptions.BackgroundTypeName + "\"); filters control Projection Lines.\n" +
                    "Surface: a 1.5 mm Generic Model surface on the floor's top faces (type \"" + FpfOptions.SurfaceTypeName +
                    "\") with material \"" + FpfOptions.SurfaceMaterialPrefix + "<floor material>\" copying its surface " +
                    "patterns and colours, so filters' Surface Patterns overrides and the Align tool work on it; alignment " +
                    "to the live pattern is best effort.\n" +
                    "Each floor remembers its style; the flat pattern is saved with the model and regenerates " +
                    "automatically when the floor, its type, material or fill pattern changes."
            });

            panel.AddItem(new PushButtonData(
                "FPF_ClearFlattenedPatterns",
                "Clear Flattened\nPatterns",
                asm,
                "FloorPatternFlattener.Commands.CommandClearFlattenedPatterns")
            {
                ToolTip = "Remove flat patterns from the selected flattened floors (or all floors in the document " +
                          "if none are selected).",
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
                ToolTip = "Force-regenerate the flat patterns of all flattened floors in this document (each in its own " +
                          "Lines or Surface style).",
                LongDescription =
                    "Useful after the model was edited without the add-in, after an add-in upgrade, or when some " +
                    "elements were skipped because they were not editable (worksharing). Also re-applies view overrides " +
                    "and removes orphaned flat-pattern shapes. Surface floors are only rebuilt when out of date, so a " +
                    "manual Align of their pattern is kept."
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
