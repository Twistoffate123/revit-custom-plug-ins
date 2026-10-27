using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using FloorPatternFlattener.Services;

namespace FloorPatternFlattener.Commands
{
    /// <summary>Flattens the selected floors (or picked floors): creates / refreshes their flat-pattern DirectShapes.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandFlattenFloorPatterns : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out var uidoc, out var doc))
                    return Result.Cancelled;

                var floors = CommandSupport.SelectedFloors(uidoc);
                if (floors.Count == 0)
                {
                    try
                    {
                        var refs = uidoc.Selection.PickObjects(ObjectType.Element,
                            new CommandSupport.FloorSelectionFilter(),
                            "Select floors to flatten patterns (Finish when done)");
                        floors = refs.Select(r => doc.GetElement(r.ElementId)).OfType<Floor>()
                            .GroupBy(f => f.Id).Select(g => g.First()).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                if (floors.Count == 0)
                {
                    TaskDialog.Show(FpfOptions.ProductName, "No floors selected.");
                    return Result.Cancelled;
                }

                var style = AskStyle(floors.Count);
                if (style == null) return Result.Cancelled;
                LastStyleSetting.Save(style.Value);

                var canEdit = CommandSupport.PrepareEditability(doc, floors);
                var report = new FpfReport();

                var ok = CommandSupport.RunTransaction(doc, "Flatten Floor Patterns (" + style.Value + ")", report, () =>
                {
                    var svc = new FlatPatternService(doc, report, canEdit);
                    foreach (var f in floors)
                    {
                        if (f.IsValidObject) svc.Apply(f, ApplyMode.IfChanged, true, style.Value);
                    }
                });

                var hint = style.Value == FlattenStyle.Surface
                    ? "Surface: a thin Generic Model surface with a copy of the floor's material patterns sits on the floor. " +
                      "View filters on Generic Models where Comments = \"" + FpfOptions.CommentsTag + "\" or Type Comments = \"" +
                      FpfOptions.SurfaceTypeName + "\" can override its Surface Patterns (pattern, colour, visibility). " +
                      "If the pattern is offset from the live floor, use Align on the surface's model pattern " +
                      "(redo it if the floor changes and the surface is rebuilt)."
                    : "Lines: style them with a view filter on Generic Models where Comments = \"" + FpfOptions.CommentsTag +
                      "\" (both layers), or Type Comments = \"" + FpfOptions.ForegroundTypeName + "\" / \"" +
                      FpfOptions.BackgroundTypeName + "\" (one layer), using Projection Lines. The floor's live pattern stays visible.";

                CommandSupport.Show(ok ? "Flatten Floor Patterns - " + style.Value : "Flatten Floor Patterns - failed", report,
                    floors.Count + " floor(s) processed.\nThe flat pattern is saved with the model and updates " +
                    "automatically (each floor keeps its style).\n\n" + hint);
                return ok ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Asks Lines or Surface (last choice is the default). Null when cancelled.</summary>
        private static FlattenStyle? AskStyle(int floorCount)
        {
            var last = LastStyleSetting.Load();
            var td = new TaskDialog(FpfOptions.ProductName)
            {
                MainInstruction = "How should the pattern of " + floorCount + " floor(s) be flattened?",
                MainContent = "Floors that are already flattened switch to the style you pick. " +
                              "Refresh and automatic updates keep each floor's style.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                AllowCancellation = true
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Lines" + (last == FlattenStyle.Lines ? " (last used)" : ""),
                "Exact match to the live floor pattern (plan-true on slopes), foreground and background as model lines. " +
                "View filters control line colour, weight, line pattern and visibility, not the hatch itself.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Surface" + (last == FlattenStyle.Surface ? " (last used)" : ""),
                "A thin flat Generic Model surface carrying a copy of the floor's material patterns. View filters can " +
                "override its Surface Patterns (swap or recolour the pattern, hide it) and Align works on its model " +
                "pattern. Alignment can differ slightly from the live floor.");
            td.DefaultButton = last == FlattenStyle.Surface ? TaskDialogResult.CommandLink2 : TaskDialogResult.CommandLink1;

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1: return FlattenStyle.Lines;
                case TaskDialogResult.CommandLink2: return FlattenStyle.Surface;
                default: return null;
            }
        }
    }
}
