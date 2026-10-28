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
                                  "set lines, patterns or visibility in Visibility/Graphics. They work in this file and on " +
                                  "linked models whose link display is \"By Host View\", so run this in your sheet file.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                    AllowCancellation = true
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Add to all view templates");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Add to the active view",
                    "If the view has a template controlling filters, add them to the template instead.");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Remove everywhere",
                    "Deletes the FPF filters from this file (and so from every view and template).");
                var choice = td.Show();
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
                                        LegacyNames.Any(n => string.Equals(n, p.Key, StringComparison.OrdinalIgnoreCase))).Select(p => p.Value.Id).ToList();
                        if (ids.Count > 0) doc.Delete(ids);
                        summary = ids.Count + " FPF filter(s) removed.";
                        return;
                    }

                    var filters = EnsureFilters(doc, out var created);
                    var views = choice == TaskDialogResult.CommandLink1
                        ? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList()
                        : new List<View> { doc.ActiveView };
                    int added = 0, skipped = 0;
                    foreach (var v in views)
                    {
                        if (v == null || !AddTo(v, filters, ref added)) skipped++;
                    }
                    summary = created + " filter(s) created, " + (filters.Count - created) + " updated. Added to " +
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
