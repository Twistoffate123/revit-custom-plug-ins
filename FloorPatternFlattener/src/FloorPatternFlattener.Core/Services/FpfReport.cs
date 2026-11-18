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
        public int BackgroundSegments;
        public int EdgeSegments;
        public int StyleFallbacks;
        public int LineFloors;
        /// <summary>Floors that were flattened with the removed Surface style and were rebuilt as Lines.</summary>
        public int LegacySurfaceReplaced;

        /// <summary>Floors whose "hide live pattern" Comments mark was added or removed.</summary>
        public int HideLiveChanged;

        public List<string> NotEditable { get; } = new List<string>();
        public List<ElementId> NotEditableIds { get; } = new List<ElementId>();
        public List<string> DefaultGrid { get; } = new List<string>();
        public List<string> DraftingScaled { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Floors whose full hatch is large. Informational only: nothing is truncated or blocked.</summary>
        public List<string> Heavy { get; } = new List<string>();
        /// <summary>Per-floor live vs flattened pattern angle (or "angle unverified").</summary>
        public List<string> PatternAngles { get; } = new List<string>();
        /// <summary>Read-back world Z of generated curves vs draped floor top (post-group).</summary>
        public List<string> ElevationAudits { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();

        public bool HasProblems =>
            NotEditable.Count > 0 || Skipped.Count > 0 || Warnings.Count > 0 || Errors.Count > 0 || SkippedGrids > 0;

        public void AddSkipped(Element e, string reason) => Skipped.Add(Describe(e) + ": " + reason);

        public void AddWarning(Element e, string reason) => Warnings.Add(Describe(e) + ": " + reason);

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
            var hatch = Segments - EdgeSegments;
            if (hatch < 0) hatch = 0;
            if (hatch > 0) sb.AppendLine("Hatch line segments written: " + hatch +
                                            (BackgroundSegments > 0 ? " (background pattern: " + BackgroundSegments + ")" : "") +
                                            " (full hatch, no segment limit)");
            if (EdgeSegments > 0)
                sb.AppendLine("Floor edge lines written: " + EdgeSegments +
                              " (subcategory \"" + FpfOptions.EdgeSubcategoryName + "\", default weight " +
                              FpfOptions.EdgeLineWeight + ", black)");

            Section(sb, "Floors skipped", Skipped, 40);
            if (HideLiveChanged > 0)
                sb.AppendLine("Live floor \"hide pattern\" mark changed on: " + HideLiveChanged + " floor(s)");
            if (LegacySurfaceReplaced > 0)
                sb.AppendLine("Old Surface-style flat patterns replaced by Lines: " + LegacySurfaceReplaced);
            if (OrphansDeleted > 0) sb.AppendLine("Orphaned flat-pattern shapes removed: " + OrphansDeleted);

            Section(sb, "Skipped - not editable (worksharing: owned by another user or out of date; reload latest / request, then Refresh)", NotEditable);
            Section(sb, "Heavy pattern (full hatch was still written, split across multiple shapes)", Heavy);
            Section(sb, "Pattern angle (live vs flat)", PatternAngles);
            Section(sb, "Elevation verification (read-back)", ElevationAudits);
            Section(sb, "No pattern on material - default 300 mm grid used", DefaultGrid);
            Section(sb, "Drafting pattern converted at 1:" + FpfOptions.DraftingPatternScale, DraftingScaled);
            Section(sb, "Warnings", Warnings);
            Section(sb, "Errors", Errors);

            if (NonPlanarFaces > 0)
                sb.AppendLine().AppendLine("Non-planar top faces approximated by triangulation: " + NonPlanarFaces);
            if (SkippedGrids > 0)
                sb.AppendLine().AppendLine("Pattern grids skipped (degenerate spacing): " + SkippedGrids);
            if (StyleFallbacks > 0)
                sb.AppendLine().AppendLine("Shapes drawn without the FPF Pattern subcategory (style not accepted): " + StyleFallbacks);
            if (ViewFailures > 0)
                sb.AppendLine().AppendLine("Views where overrides could not be set: " + ViewFailures);
            if (ViewsSkippedNotEditable.Count > 0)
                sb.AppendLine().AppendLine("Views skipped (not editable in worksharing): " + ViewsSkippedNotEditable.Count);
            return sb.ToString().TrimEnd();
        }

        private static void Section(StringBuilder sb, string title, List<string> items, int max = 15)
        {
            if (items.Count == 0) return;
            sb.AppendLine().AppendLine(title + ":");
            for (var i = 0; i < items.Count && i < max; i++) sb.AppendLine("  - " + items[i]);
            if (items.Count > max) sb.AppendLine("  ... and " + (items.Count - max) + " more");
        }
    }
}
