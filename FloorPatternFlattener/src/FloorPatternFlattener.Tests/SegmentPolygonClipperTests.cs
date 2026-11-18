using System.Collections.Generic;
using System.Linq;
using FloorPatternFlattener.Geometry;
using Xunit;

namespace FloorPatternFlattener.Tests
{
    public class SegmentPolygonClipperTests
    {
        private static RegionXY Square(double x0, double y0, double size)
        {
            var r = new RegionXY();
            r.AddEdge(x0, y0, x0 + size, y0);
            r.AddEdge(x0 + size, y0, x0 + size, y0 + size);
            r.AddEdge(x0 + size, y0 + size, x0, y0 + size);
            r.AddEdge(x0, y0 + size, x0, y0);
            return r;
        }

        [Fact]
        public void SegmentFullyInsideIsRemoved()
        {
            var hole = Square(2, 2, 2);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(2.5, 3, 3.5, 3, new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Empty(parts);
        }

        [Fact]
        public void SegmentCrossingHoleIsClippedToTwoPieces()
        {
            var hole = Square(4, 4, 2);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(0, 5, 10, 5, new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Equal(2, parts.Count);
            Assert.True(parts[0][2] < 4.01);
            Assert.True(parts[1][0] > 5.99);
        }

        [Fact]
        public void DiagonalCrossing64GonCircleClipsToTwoPieces()
        {
            var hole = new RegionXY();
            hole.AddPolyline(CutoutCurveMath.TessellateCircle(0, 0, 5, 64).ToList(), close: true);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(-20, -10, 20, 10,
                new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Equal(2, parts.Count);
            Assert.True(parts[0][0] < -4.9 || parts[1][2] > 4.9);
        }

        [Fact]
        public void DiagonalCrossingRectangleClipsToTwoPieces()
        {
            var hole = Square(4, 4, 2);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(0, 0, 10, 10,
                new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Equal(2, parts.Count);
        }

        [Fact]
        public void MultipleRegionsAllApply()
        {
            var left = Square(0, 4, 2);
            var right = Square(8, 4, 2);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(1, 5, 9, 5,
                new List<RegionXY> { left, right }, 1.0e-6, ref stats);
            Assert.Equal(1, parts.Count);
            Assert.InRange(parts[0][0], 2.0, 2.1);
            Assert.InRange(parts[0][2], 8.0, 8.01);
        }

        [Fact]
        public void SegmentPassingThroughBBoxWithEndpointsOutsideStillClips()
        {
            var hole = Square(4, 4, 2);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(3, 0, 7, 10,
                new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Equal(2, parts.Count);
        }

        [Fact]
        public void ShortenedCrossingSegmentCountsAsClippedInList()
        {
            var hole = Square(4, 4, 2);
            var stats = new SegmentPolygonClipper.ClipStats { Input = 1 };
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(0, 5, 10, 5,
                new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Equal(2, parts.Count);
        }

        [Fact]
        public void BboxPrefilterSkipsDistantHole()
        {
            var hole = Square(100, 100, 2);
            var stats = new SegmentPolygonClipper.ClipStats();
            var parts = SegmentPolygonClipper.ClipSegmentRemoveInteriors(0, 0, 10, 0, new List<RegionXY> { hole }, 1.0e-6, ref stats);
            Assert.Single(parts);
            Assert.Equal(0, parts[0][0], 5);
            Assert.Equal(10, parts[0][2], 5);
        }

        [Fact]
        public void CutoutCodecRoundTripsSketch()
        {
            var data = new FloorPatternFlattener.Storage.FloorCutoutData();
            data.Picked.Add(new FloorPatternFlattener.Storage.PickedCutout { ElementUniqueId = "abc", OffsetMm = 5 });
            data.Sketches.Add(new FloorPatternFlattener.Storage.SketchCutout
            {
                Points = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 10.0, 0.0 }, new[] { 10.0, 5.0 } }
            });
            data.EmitCutoutOutline = true;
            var text = FloorPatternFlattener.Storage.CutoutStorageCodec.Encode(data);
            var back = FloorPatternFlattener.Storage.CutoutStorageCodec.Decode(text);
            Assert.Single(back.Picked);
            Assert.Equal("abc", back.Picked[0].ElementUniqueId);
            Assert.Equal(5, back.Picked[0].OffsetMm);
            Assert.Single(back.Sketches);
            Assert.Equal(3, back.Sketches[0].Points.Count);
            Assert.True(back.EmitCutoutOutline);
        }
    }
}