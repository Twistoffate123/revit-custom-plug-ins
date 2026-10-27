namespace FloorPatternFlattener
{
    /// <summary>
    /// Central tuning constants. Changing anything that affects generated geometry should also bump
    /// <see cref="AlgorithmVersion"/> so existing hatches are regenerated on the next change / Refresh.
    /// </summary>
    internal static class FpfOptions
    {
        public const string ProductName = "Floor Pattern Flattener";

        /// <summary>
        /// Must match &lt;VendorId&gt; in the .addin manifest. Also used for the extensible storage schemas, where
        /// Revit requires 4-253 characters (SchemaBuilder.VendorIdIsValid); the original "FPF" was rejected with
        /// "The vendorId is invalid". Revit stores it upper-cased.
        /// </summary>
        public const string VendorId = "TWISTOFFATE";

        /// <summary>DirectShape.ApplicationId.</summary>
        public const string ApplicationId = "FloorPatternFlattener";

        /// <summary>Written to the DirectShape "Comments" parameter so users can build a view filter.</summary>
        public const string CommentsTag = "FPF Flat Pattern";

        /// <summary>
        /// Generic Models subcategory prefix for the flattened pattern lines: "FPF Pattern Foreground" /
        /// "FPF Pattern Background", with " RGB r-g-b" appended when the material's pattern colour is not black.
        /// The subcategory carries the pattern colour (Object Styles / V/G); view filters override it.
        /// </summary>
        public const string SubcategoryPrefix = "FPF Pattern ";

        /// <summary>
        /// DirectShapeType names for the flattened pattern shapes, so view filters can tell the layers apart
        /// (rule "Type Name equals ..." or "Type Comments equals ..."). Comments stays <see cref="CommentsTag"/> on all.
        /// </summary>
        public const string ForegroundTypeName = "FPF Flat Pattern - Foreground";
        public const string BackgroundTypeName = "FPF Flat Pattern - Background";

        /// <summary>
        /// Surface mode: DirectShapeType (also its Type Comments) of the thin flat surface that carries the floor's
        /// pattern as a real material surface pattern, so view filters' Surface Patterns overrides act on it.
        /// </summary>
        public const string SurfaceTypeName = "FPF Flat Pattern - Surface";

        /// <summary>Surface mode: Generic Models subcategory of the flat surface (edges / Object Styles / V/G).</summary>
        public const string SurfaceSubcategoryName = "FPF Pattern Surface";

        /// <summary>Surface mode: material name prefix; one material per source material ("FPF Flat Pattern - &lt;name&gt;").</summary>
        public const string SurfaceMaterialPrefix = "FPF Flat Pattern - ";

        /// <summary>
        /// Surface mode: thickness of the flat surface (1.5 mm, just above Revit's short-curve tolerance). It sits
        /// on the floor's top face, so its top face is 1.5 mm above the live floor.
        /// </summary>
        public const double SurfaceThicknessFeet = 1.5 / 304.8;

        /// <summary>Bump when surface-mode geometry or material handling changes (part of the surface signature).</summary>
        public const int SurfaceAlgorithmVersion = 1;

        /// <summary>
        /// false (default): the floor's live surface pattern stays visible in every view; the flat pattern is drawn on top.
        /// true: old behaviour, the live pattern is switched off per view with an element override (note: element
        /// overrides win over view filters, so the floor's pattern then cannot be controlled by filters).
        /// </summary>
        public static readonly bool HideNativeSurfacePatterns = false;

        /// <summary>DirectShape name prefix: "Flat Pattern - Floor &lt;id&gt;".</summary>
        public const string DirectShapeNamePrefix = "Flat Pattern - Floor ";

        /// <summary>Bump when hatch generation changes so stored signatures no longer match.</summary>
        public const int AlgorithmVersion = 3; // 2: herringbone fix; 3: background pattern + per-layer shapes/styles

        /// <summary>Extensible storage schema version written into every entity.</summary>
        public const int SchemaDataVersion = 1;

        /// <summary>Hatch lines are lifted this far along the face normal to avoid z-fighting (0.5 mm in feet).</summary>
        public const double SurfaceOffsetFeet = 0.5 / 304.8;

        /// <summary>A top face must have normal.Z above this to be considered upward-facing (~87 deg max slope).</summary>
        public const double MinUpwardNormalZ = 0.05;

        /// <summary>Default orthogonal grid used when the material has no (or a solid) surface pattern: 300 mm.</summary>
        public const double DefaultGridSpacingFeet = 300.0 / 304.8;

        /// <summary>
        /// Drafting-target fill patterns are defined in paper space. They are converted to model size as if seen
        /// at this view scale (1:100). Model patterns are always used at true size.
        /// </summary>
        public const double DraftingPatternScale = 100.0;

        /// <summary>Zero-length pattern dashes (dots) are drawn as tiny dashes of this length (1.5 mm).</summary>
        public const double DotLengthFeet = 1.5 / 304.8;

        /// <summary>Hard cap on generated line segments per floor; beyond this the hatch is truncated and reported.</summary>
        public const int MaxSegmentsPerFloor = 100000;

        /// <summary>Segments per DirectShape element; larger hatches are split across several DirectShapes.</summary>
        public const int SegmentsPerDirectShape = 25000;

        /// <summary>Safety cap on candidate hatch lines per grid per face (protects against absurd pattern scales).</summary>
        public const int MaxLinesPerGrid = 200000;

        /// <summary>Hide generated DirectShapes in section / elevation / detail views so they never add a line there.</summary>
        public static readonly bool HideInSectionViews = true;

        /// <summary>On document open, count flattened floors whose stored signature is stale and suggest Refresh.</summary>
        public static readonly bool CheckStaleOnOpen = true;

        /// <summary>Numeric tolerance for 2D clipping (feet).</summary>
        public const double GeometryTolerance = 1.0e-9;
    
        /// <summary>Flatten / Edit ask for confirmation when one floor would get more modelled lines than this.</summary>
        public const int HeavyPatternConfirmSegments = 10000;

        /// <summary>View filters made by the Filter button (rules on Comments / Type Comments, visible through links).</summary>
        public const string FilterAllName = "FPF - Flat Pattern";
        public const string FilterForegroundName = "FPF - Flat Pattern Foreground";
        public const string FilterBackgroundName = "FPF - Flat Pattern Background";
        public const string FilterSurfaceName = "FPF - Flat Pattern Surface";
    }
}
