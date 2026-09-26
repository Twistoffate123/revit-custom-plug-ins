namespace FloorPatternFlattener
{
    /// <summary>
    /// Central tuning constants. Changing anything that affects generated geometry should also bump
    /// <see cref="AlgorithmVersion"/> so existing hatches are regenerated on the next change / Refresh.
    /// </summary>
    internal static class FpfOptions
    {
        public const string ProductName = "Floor Pattern Flattener";

        /// <summary>Must match &lt;VendorId&gt; in the .addin manifest.</summary>
        public const string VendorId = "FPF";

        /// <summary>DirectShape.ApplicationId.</summary>
        public const string ApplicationId = "FloorPatternFlattener";

        /// <summary>Written to the DirectShape "Comments" parameter so users can build a view filter.</summary>
        public const string CommentsTag = "FPF Flat Pattern";

        /// <summary>DirectShape name prefix: "Flat Pattern - Floor &lt;id&gt;".</summary>
        public const string DirectShapeNamePrefix = "Flat Pattern - Floor ";

        /// <summary>Bump when hatch generation changes so stored signatures no longer match.</summary>
        public const int AlgorithmVersion = 1;

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
    }
}
