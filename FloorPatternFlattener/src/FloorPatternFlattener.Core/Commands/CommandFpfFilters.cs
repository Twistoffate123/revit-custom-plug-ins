using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Services;

namespace FloorPatternFlattener.Commands
{
    /// <summary>
    /// Creates / updates ready-made view filters for flat patterns and adds them to view templates or the active view
    /// (or removes them). Rules use Comments / Type Comments of Generic Models, which work on linked elements too, so
    /// run it in the sheet (host) file and set the links to "By Host View".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandFpfFilters : IExternalCommand
    {
        private static readonly (string Name, BuiltInParameter Param, string Value)[] Definitions =
        {
            (FpfOptions.FilterAllName, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, FpfOptions.CommentsTag),
            (FpfOptions.FilterForegroundName, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, FpfOptions.ForegroundTypeName),
            (FpfOptions.FilterBackgroundName, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, FpfOptions.BackgroundTypeName)
        };

        /// <summary>Old FPF filters that are no longer created but are still removed by "Remove everywhere".</summary>
        private static readonly string[] LegacyNames = { FpfOptions.LegacyFilterSurfaceName };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!CommandSupport.TryGetDocument(commandData, out _, out var doc))
                    return Result.Cancelled;

                var td = new TaskDialog(FpfOptions.ProductName)
                {
                    MainInstruction = "Flat pattern view filters",
                    MainContent = "Creates or updates the filters \"" + string.Join("\", \"", Definitions.Select(d => d.Name)) +
                                  "\" (Generic Models, rules on Comments / Type Comments). They are added with no overrides - " +
                                  "set lines, patterns or visibility in Visibility/Graphics.\n\n\"" + FpfOptions.FilterLiveFloorName +
                                  "\" (Floors, Comments contains \"" + FpfOptions.HideLiveMark + "\") is added WITH overrides: " +
                                  "surface and cut patterns hidden on live floors flattened with \"Hide live floor pattern\".\n\n" +
                                  "All work in this file and on linked models whose link display is \"By Host View\", so run " +
                                  "this in your sheet file. Plan, 3D and other views / templates are all included.",
                    VerificationText = "Also hide fold lines: turn off Floors > Interior Edges in these views / templates " +
                                       "(affects ALL floors there; floor outlines stay)",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                    AllowCancellation = true
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Add to all view templates");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Add to the active view",
                    "If the view has a template controlling filters, add them to the template instead.");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Remove everywhere",
                    "Deletes the FPF filters from this file (and so from every view and template).");
                var choice = td.Show();
                var hideFoldLines = td.WasVerificationChecked();
                if (choice != TaskDialogResult.CommandLink1 && choice != TaskDialogResult.CommandLink2 &&
                    choice != TaskDialogResult.CommandLink3)
                    return Result.Cancelled;

                var report = new FpfReport();
                string summary = string.Empty;
                var ok = CommandSupport.RunTransaction(doc, "Flat Pattern Filters", report, () =>
                {
                    if (choice == TaskDialogResult.CommandLink3)
                    {
                        var ids = ExistingFilters(doc)
                            .Where(p => Definitions.Any(d => d.Name == p.Key) ||
                                        string.Equals(p.Key, FpfOptions.FilterLiveFloorName, StringComparison.OrdinalIgnoreCase) ||
                                        LegacyNames.Any(n => string.Equals(n, p.Key, StringComparison.OrdinalIgnoreCase))).Select(p => p.Value.Id).ToList();
                        if (ids.Count > 0) doc.Delete(ids);
                        summary = ids.Count + " FPF filter(s) removed.";
                        return;
                    }

                    var filters = EnsureFilters(doc, out var created);
                    var liveId = EnsureLiveFloorFilter(doc, ref created);
                    var views = choice == TaskDialogResult.CommandLink1
                        ? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList()
                        : new List<View> { doc.ActiveView };
                    int added = 0, skipped = 0, edgesOff = 0;
                    foreach (var v in views)
                    {
                        if (v == null || !AddTo(v, filters, ref added)) { skipped++; continue; }
                        if (liveId != ElementId.InvalidElementId) AddLiveFilter(v, liveId, ref added);
                        if (hideFoldLines && HideInteriorEdges(doc, v)) edgesOff++;
                    }
                    filters.Add(liveId);
                    summary = (hideFoldLines ? "Floors > Interior Edges turned off in " + edgesOff + " view(s)/template(s). " : "") +
                              created + " filter(s) created, " + (filters.Count - created) + " updated. Added to " +
                              (views.Count - skipped) + " view(s)/template(s) (" + added + " filter slot(s))" +
                              (skipped > 0 ? "; " + skipped + " skipped (no filters allowed, or controlled by a template)." : ".") +
                              (views.Count == 0 ? " There are no view templates in this file." : "");
                });

                CommandSupport.Show("Flat Pattern Filters", report, ok ? summary : "Failed.");
                return ok ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static Dictionary<string, ParameterFilterElement> ExistingFilters(Document doc)
        {
            var d = new Dictionary<string, ParameterFilterElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                d[p.Name] = p;
            return d;
        }

        private static List<ElementId> EnsureFilters(Document doc, out int created)
        {
            created = 0;
            var cats = new List<ElementId> { new ElementId(BuiltInCategory.OST_GenericModel) };
            var existing = ExistingFilters(doc);
            var ids = new List<ElementId>();
            foreach (var d in Definitions)
            {
                var rule = ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(d.Param), d.Value);
                var filter = new ElementParameterFilter(rule);
                if (existing.TryGetValue(d.Name, out var pfe))
                {
                    pfe.SetCategories(cats);
                    pfe.SetElementFilter(filter);
                }
                else
                {
                    pfe = ParameterFilterElement.Create(doc, d.Name, cats, filter);
                    created++;
                }
                ids.Add(pfe.Id);
            }
            return ids;
        }

        /// <summary>Floors filter: instance Comments contains the hide-live mark.</summary>
        private static ElementId EnsureLiveFloorFilter(Document doc, ref int created)
        {
            try
            {
                var cats = new List<ElementId> { new ElementId(BuiltInCategory.OST_Floors) };
                var rule = ParameterFilterRuleFactory.CreateContainsRule(
                    new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), FpfOptions.HideLiveMark);
                var filter = new ElementParameterFilter(rule);
                if (ExistingFilters(doc).TryGetValue(FpfOptions.FilterLiveFloorName, out var pfe))
                {
                    pfe.SetCategories(cats);
                    pfe.SetElementFilter(filter);
                }
                else
                {
                    pfe = ParameterFilterElement.Create(doc, FpfOptions.FilterLiveFloorName, cats, filter);
                    created++;
                }
                return pfe.Id;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>Overrides of the live-floor filter: surface + cut foreground / background patterns hidden.</summary>
        private static OverrideGraphicSettings LiveFloorOverrides()
        {
            var o = new OverrideGraphicSettings();
            o.SetSurfaceForegroundPatternVisible(false);
            o.SetSurfaceBackgroundPatternVisible(false);
            o.SetCutForegroundPatternVisible(false);
            o.SetCutBackgroundPatternVisible(false);
            return o;
        }

        private static void AddLiveFilter(View v, ElementId id, ref int added)
        {
            try
            {
                if (!v.GetFilters().Contains(id))
                {
                    v.AddFilter(id);
                    added++;
                }
                v.SetFilterVisibility(id, true);
                v.SetFilterOverrides(id, LiveFloorOverrides());
            }
            catch
            {
                // view does not accept it - ignore
            }
        }

        /// <summary>Turns off Floors > Interior Edges (fold / crease lines of shape-edited floors) in the view.</summary>
        private static bool HideInteriorEdges(Document doc, View v)
        {
            try
            {
                var floors = Category.GetCategory(doc, BuiltInCategory.OST_Floors);
                if (floors == null) return false;
                Category edges = null;
                try { edges = Category.GetCategory(doc, BuiltInCategory.OST_FloorsInteriorEdges); } catch { }
                if (edges == null)
                    foreach (Category c in floors.SubCategories)
                        if (string.Equals(c.Name, "Interior Edges", StringComparison.OrdinalIgnoreCase)) { edges = c; break; }
                if (edges == null || !v.CanCategoryBeHidden(edges.Id)) return false;
                if (!v.GetCategoryHidden(edges.Id)) v.SetCategoryHidden(edges.Id, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool AddTo(View v, List<ElementId> filters, ref int added)
        {
            try
            {
                if (!v.AreGraphicsOverridesAllowed()) return false;
                var have = new HashSet<ElementId>(v.GetFilters());
                foreach (var id in filters)
                {
                    if (have.Contains(id)) continue;
                    v.AddFilter(id);
                    v.SetFilterVisibility(id, true);
                    added++;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
