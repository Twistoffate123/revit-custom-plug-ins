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

        private static PushButton _autoButton;

        /// <summary>Sets the Auto toggle's caption from the remembered setting.</summary>
        public static void RefreshAutoButton()
        {
            if (_autoButton == null) return;
            try
            {
                _autoButton.ItemText = AutoUpdateSetting.Enabled ? "Auto:\nOn" : "Auto:\nOff";
            }
            catch
            {
                // ignore
            }
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
                          "They are drawn as model lines at the live pattern's exact positions; view filters control them. " +
                          "Dialog: angle, Align to Edge / Line, Clear, scope (selected / all visible of this type), " +
                          "Preview, Apply, Discard. The live floor pattern is left as it is.",
                LongDescription =
                    "The material's fill pattern is generated in plan (aligned to the project internal origin), " +
                    "projected onto every top face and stored as pinned Generic Model lines (Comments = \"" +
                    FpfOptions.CommentsTag + "\", types \"" + FpfOptions.ForegroundTypeName + "\" / \"" +
                    FpfOptions.BackgroundTypeName + "\"); filters control Projection Lines.\n" +
                    "Each floor remembers its rotation and anchor; the flat pattern is saved with the model and regenerates " +
                    "automatically when the floor, its type, material or fill pattern changes."
            });

            panel.AddItem(new PushButtonData(
                "FPF_EditFlattenedPattern",
                "Edit Flattened\nPattern",
                asm,
                "FloorPatternFlattener.Commands.CommandEditFlattenedPattern")
            {
                ToolTip = "Re-open a flattened floor with its stored rotation and anchor. Preview, then Apply to " +
                          "replace its flat pattern or Discard to keep the current one.",
                LongDescription = "Select or pick a flattened floor (or its flat pattern). The same dialog as Flatten: " +
                                  "angle, Align to Edge / Line, Clear, scope, Preview."
            });

            panel.AddItem(new PushButtonData(
                "FPF_UpdateFlattenedPatterns",
                "Update\nFlattened",
                asm,
                "FloorPatternFlattener.Commands.CommandUpdateFlattenedPatterns")
            {
                ToolTip = "Rebuild the selected flattened floors, one picked floor or all of them, with progress, Cancel " +
                          "and a summary. Use it when Auto is Off.",
                LongDescription = "Each floor is rebuilt with its stored rotation and anchor (one undo step)."
            });

            _autoButton = panel.AddItem(new PushButtonData(
                "FPF_ToggleAutoUpdate",
                "Auto:\nOn",
                asm,
                "FloorPatternFlattener.Commands.CommandToggleAutoUpdate")
            {
                ToolTip = "Turn the automatic rebuild of flat patterns on or off (remembered between sessions).",
                LongDescription = "Auto: On - flat patterns follow floor, type, material and fill pattern changes " +
                                  "automatically. Auto: Off - nothing is rebuilt until you press Update (faster while " +
                                  "editing big models)."
            }) as PushButton;
            RefreshAutoButton();

            panel.AddItem(new PushButtonData(
                "FPF_Filters",
                "Flat Pattern\nFilters",
                asm,
                "FloorPatternFlattener.Commands.CommandFpfFilters")
            {
                ToolTip = "Create the ready-made view filters (" + FpfOptions.FilterAllName + ", Foreground, Background) " +
                          "and add them to all view templates or the active view, or remove them everywhere.",
                LongDescription = "Rules use Comments / Type Comments of Generic Models, so the filters also work on " +
                                  "linked models whose display is \"By Host View\": run this in your sheet file. " +
                                  "Filters are added with no overrides; set them in Visibility/Graphics."
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
                ToolTip = "Force-regenerate the flat patterns of all flattened floors in this document. Floors " +
                          "flattened with the old Surface style are rebuilt as lines.",
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
