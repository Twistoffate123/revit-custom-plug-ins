using System;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// Pure decisions for hatch clipping and group Z. No Revit types, so tests can run without Revit.
    /// </summary>
    public static class HatchClipPolicy
    {
        /// <summary>
        /// Per-triangle face loops must not be fed to an even-odd clip: each shared edge is stored twice
        /// and the interior of the floor flips to "outside", which leaves only hatch patches.
        /// A handful of real outer/inner sketch loops is safe.
        /// </summary>
        public static bool ClipHatchToFaceLoops(int authorityLoopCount, int pieceCount)
        {
            if (authorityLoopCount <= 0) return false;
            if (pieceCount > 64) return false;
            if (authorityLoopCount > 48) return false;
            return true;
        }

        /// <summary>
        /// Vertical correction after <c>NewGroup</c>. Only undoes a shift of the whole group away from the
        /// DirectShapes as they were created. Does not pull a sloped pattern up to one sample Z
        /// (that lifts the low end by metres and reads as a sheet in the wrong place).
        /// </summary>
        public static double GroupingShiftCorrection(double preMinZ, double groupMinZ, double toleranceFeet)
        {
            if (!double.IsFinite(preMinZ) || !double.IsFinite(groupMinZ)) return 0;
            var delta = preMinZ - groupMinZ;
            if (Math.Abs(delta) <= toleranceFeet) return 0;
            return delta;
        }
    }
}