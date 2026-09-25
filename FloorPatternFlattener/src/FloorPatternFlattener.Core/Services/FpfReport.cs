using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>Collects outcomes and warnings of one command / updater run.</summary>
    public sealed class FpfReport
    {
        public int Created;
        public int Updated;
        public int Unchanged;
        public int Cleared;
        public int Segments;
        public int OrphansDeleted;
        public int ViewFailures;
        public HashSet<ElementId> ViewsSkippedNotEditable { get; } = new HashSet<ElementId>();
        public int NonPlanarFaces;
        public int SkippedGrids;

        public List<string> NotEditable { get; } = new List<string>();
        public List<ElementId> NotEditableIds { get; } = new List<ElementId>();
        public List<string> Truncated { get; } = new List<string>();
        public List<string> DefaultGrid { get; } = new List<string>();
        public List<string> DraftingScaled { get; } = new List<string>();
        public List<string> NoTopFace { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();

        public bool HasProblems =>
            NotEditable.Count > 0 || Truncated.Count > 0 || NoTopFace.Count > 0 || Errors.Count > 0 || SkippedGrids > 0;

        public void AddNotEditable(Element e, string what)
        {
            NotEditable.Add(Describe(e) + " - " + what);
            if (e != null) NotEditableIds.Add(e.Id);
        }

        public static string Describe(Element e)
        {
            if (e == null) return "<deleted element>";
            string name;
            try { name = e.Name; } catch { name = string.Empty; }
            return (e is Floor ? "Floor " : e.GetType().Name + " ") + e.Id + (string.IsNullOrEmpty(name) ? "" : " (" + name + ")");
        }

        public string ToText(string headline)
        {
            var sb = new StringBuilder();
            sb.AppendLine(headline);
            sb.AppendLine();
            if (Created > 0) sb.AppendLine("Flattened (new): " + Created);
            if (Updated > 0) sb.AppendLine("Regenerated: " + Updated);
            if (Unchanged > 0) sb.AppendLine("Already up to date: " + Unchanged);
            if (Cleared > 0) sb.AppendLine("Cleared: " + Cleared);
            if (Segments > 0) sb.AppendLine("Hatch line segments written: " + Segments);
            if (OrphansDeleted > 0) sb.AppendLine("Orphaned flat-pattern shapes removed: " + OrphansDeleted);

            Section(sb, "Skipped - not editable (worksharing: owned by another user or out of date; reload latest / request, then Refresh)", NotEditable);
            Section(sb, "Hatch truncated at " + FpfOptions.MaxSegmentsPerFloor + " segments", Truncated);
            Section(sb, "No pattern on material - default 300 mm grid used", DefaultGrid);
            Section(sb, "Drafting pattern converted at 1:" + FpfOptions.DraftingPatternScale, DraftingScaled);
            Section(sb, "No upward-facing top face found", NoTopFace);
            Section(sb, "Errors", Errors);

            if (NonPlanarFaces > 0)
                sb.AppendLine().AppendLine("Non-planar top faces approximated by triangulation: " + NonPlanarFaces);
            if (SkippedGrids > 0)
                sb.AppendLine().AppendLine("Pattern grids skipped (too dense for the floor size): " + SkippedGrids);
            if (ViewFailures > 0)
                sb.AppendLine().AppendLine("Views where overrides could not be set: " + ViewFailures);
            if (ViewsSkippedNotEditable.Count > 0)
                sb.AppendLine().AppendLine("Views skipped (not editable in worksharing): " + ViewsSkippedNotEditable.Count);
            return sb.ToString().TrimEnd();
        }

        private static void Section(StringBuilder sb, string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine().AppendLine(title + ":");
            var max = 15;
            for (var i = 0; i < items.Count && i < max; i++) sb.AppendLine("  - " + items[i]);
            if (items.Count > max) sb.AppendLine("  ... and " + (items.Count - max) + " more");
        }
    }
}
