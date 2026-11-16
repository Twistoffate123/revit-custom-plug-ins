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
        /// Generic Models subcategory for the floor outline and opening edges. Line weight and colour are set
        /// when the subcategory is created (black, weight 5). Later edits in Object Styles are kept.
        /// </summary>
        public const string EdgeSubcategoryName = "FPF Floor Edge";

        /// <summary>DirectShapeType / Type Comments for the boundary shapes.</summary>
        public const string EdgeTypeName = "FPF Flat Pattern - Edge";

        /// <summary>Projection line weight used the first time <see cref="EdgeSubcategoryName"/> is created (1–16).</summary>
        public const int EdgeLineWeight = 5;

        /// <summary>Pattern strokes within this distance of a boundary, and parallel to it, are cut away (2 mm).</summary>
        public const double BoundaryOverlapFeet = 2.0 / 304.8;

        /// <summary>Hatch is clipped this far inside the floor outline and outside opening edges (1.5 mm).</summary>
        public const double HatchInsetFeet = 1.5 / 304.8;

        /// <summary>Boundary is lifted this far above the hatch lines, along the face normal (2 mm).</summary>
        public const double EdgeLiftAboveHatchFeet = 2.0 / 304.8;

        /// <summary>
        /// Legacy: DirectShapeType / Type Comments of the removed "Surface" style. Only used to recognise and clean up
        /// old elements and the old "FPF - Flat Pattern Surface" filter; nothing new is created with it.
        /// </summary>
        public const string LegacySurfaceTypeName = "FPF Flat Pattern - Surface";

        /// <summary>
        /// false (default): the floor's live surface pattern stays visible in every view; the flat pattern is drawn on top.
        /// true: old behaviour, the live pattern is switched off per view with an element override (note: element
        /// overrides win over view filters, so the floor's pattern then cannot be controlled by filters).
        /// </summary>
        public static readonly bool HideNativeSurfacePatterns = false;

        /// <summary>DirectShape name prefix: "Flat Pattern - Floor &lt;id&gt;".</summary>
        public const string DirectShapeNamePrefix = "Flat Pattern - Floor ";

        /// <summary>Bump when hatch generation changes so stored signatures no longer match.</summary>
        public const int AlgorithmVersion = 6; // 6: hatch inset off the boundary; edge lifted and joined

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

        /// <summary>
        /// Segments per DirectShape. The full hatch is always written; a floor with more lines is split
        /// across several shapes so each element stays a manageable size. There is no segment cap.
        /// </summary>
        public const int SegmentsPerDirectShape = 25000;

        /// <summary>Cracks narrower than this between triangulated faces are joined so the hatch stays continuous (1 mm).</summary>
        public const double MeshCrackBridgeFeet = 1.0 / 304.8;

        /// <summary>
        /// A draped hatch line may leave the true surface by up to this much before it is split (5 mm).
        /// Keeps one running-bond dash as one line across a gentle slope instead of a fragment per triangle.
        /// </summary>
        public const double DrapeToleranceFeet = 5.0 / 304.8;

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
        /// <summary>
        /// Mark appended to the live floor's instance Comments when "Hide live floor pattern and fold lines" is on.
        /// Bracketed so it never matches normal user text; any existing Comments are kept in front of it
        /// ("Paving zone A [FPF Hide Live]"). The filter below uses "Comments contains" this mark, which works on
        /// linked floors when the link is "By Host View".
        /// </summary>
        public const string HideLiveMark = "[FPF Hide Live]";

        /// <summary>Floors filter (Comments contains <see cref="HideLiveMark"/>) that hides the live pattern.</summary>
        public const string FilterLiveFloorName = "FPF - Live Floor (hidden pattern)";

        /// <summary>Legacy filter of the removed Surface style: no longer created, still deleted by "Remove everywhere".</summary>
        public const string LegacyFilterSurfaceName = "FPF - Flat Pattern Surface";
    }
}
