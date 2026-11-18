using FloorPatternFlattener.Geometry;
using Xunit;

namespace FloorPatternFlattener.Tests
{
    public class HatchGeometryTests
    {
        [Fact]
        public void TessellatedDrivewayDoesNotReclipEveryTriangle()
        {
            Assert.False(HatchClipPolicy.ClipHatchToFaceLoops(8000, 8035));
        }

        [Fact]
        public void SimpleFloorStillClipsOuterAndHoleLoops()
        {
            Assert.True(HatchClipPolicy.ClipHatchToFaceLoops(2, 3));
        }

        [Fact]
        public void GroupCorrectionUndoesOnlyAWholeGroupShift()
        {
            Assert.Equal(0, HatchClipPolicy.GroupingShiftCorrection(-15.31, -15.30, 0.02), 5);
            Assert.Equal(8.25, HatchClipPolicy.GroupingShiftCorrection(-15.0, -23.25, 0.02), 5);
        }

        [Fact]
        public void SlopedPatternIsNotLiftedToOneSampleElevation()
        {
            // Low end already matches the shapes that were created. A centre-line sample
            // several feet higher must not become a move.
            var move = HatchClipPolicy.GroupingShiftCorrection(-20.0, -20.0, 0.02);
            Assert.Equal(0, move, 5);
        }

        [Fact]
        public void SingleOutlineKeepsAnInteriorPoint()
        {
            var region = new RegionXY();
            region.AddEdge(0, 0, 10, 0);
            region.AddEdge(10, 0, 10, 4);
            region.AddEdge(10, 4, 0, 4);
            region.AddEdge(0, 4, 0, 0);
            Assert.True(PolygonClipper.ContainsPoint(region, 3, 2));
        }

        [Fact]
        public void OpeningIsOutsideWhileTheSlabStaysInside()
        {
            var region = new RegionXY();
            region.AddEdge(0, 0, 10, 0);
            region.AddEdge(10, 0, 10, 10);
            region.AddEdge(10, 10, 0, 10);
            region.AddEdge(0, 10, 0, 0);
            region.AddEdge(4, 4, 6, 4);
            region.AddEdge(6, 4, 6, 6);
            region.AddEdge(6, 6, 4, 6);
            region.AddEdge(4, 6, 4, 4);
            Assert.True(PolygonClipper.ContainsPoint(region, 1, 1));
            Assert.False(PolygonClipper.ContainsPoint(region, 5, 5));
        }

        [Fact]
        public void InteriorCrackEdgeFlipsEvenOdd()
        {
            var solid = new RegionXY();
            solid.AddEdge(0, 0, 3, 0);
            solid.AddEdge(3, 0, 3, 2);
            solid.AddEdge(3, 2, 0, 2);
            solid.AddEdge(0, 2, 0, 0);
            Assert.True(PolygonClipper.ContainsPoint(solid, 1, 1));

            // A triangle edge that does not coincide with the outline (mesh crack / second face loop).
            var cracked = new RegionXY();
            cracked.AddEdge(0, 0, 3, 0);
            cracked.AddEdge(3, 0, 3, 2);
            cracked.AddEdge(3, 2, 0, 2);
            cracked.AddEdge(0, 2, 0, 0);
            cracked.AddEdge(0.8, 0, 0.8, 2);
            Assert.False(PolygonClipper.ContainsPoint(cracked, 1, 1));
        }
    }
}