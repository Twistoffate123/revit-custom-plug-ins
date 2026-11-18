using System.Collections.Generic;
using System.Linq;
using FloorPatternFlattener.Geometry;
using Xunit;

namespace FloorPatternFlattener.Tests
{
    public class CutoutCurveMathTests
    {
        [Fact]
        public void FullCircleHasAtLeast32SegmentsAndExpectedArea()
        {
            var loop = CutoutCurveMath.TessellateCircle(0, 0, 10, CutoutCurveMath.MinFullCircleSegments).ToList();
            Assert.True(loop.Count >= CutoutCurveMath.MinFullCircleSegments);
            Assert.False(CutoutCurveMath.IsDegenerateLoop(loop, out var area));
            Assert.InRange(area, 310, 315);
        }

        [Fact]
        public void SingleCirclePolylineChainsToFullArea()
        {
            var full = CutoutCurveMath.TessellateCircle(0, 0, 5, 48).ToList();
            var loops = CutoutCurveMath.ChainPolylinesToLoops(new[] { full }, CutoutCurveMath.ChordToleranceFeet);
            Assert.Single(loops);
            var area = CutoutCurveMath.AbsoluteArea(loops[0]);
            Assert.True(area > 70, "n=" + loops[0].Count + " area=" + area);
        }

        [Fact]
        public void HalfArcIsDenselySampled()
        {
            var half = CutoutCurveMath.TessellateArc(0, 0, 5, 0, System.Math.PI, CutoutCurveMath.ChordToleranceFeet)
                .ToList();
            Assert.True(half.Count > 32, "half-arc count=" + half.Count);
            var apex = half[half.Count / 2];
            Assert.True(apex[1] > 4.0, "apex y=" + apex[1]);
            Assert.InRange(CutoutCurveMath.AbsoluteArea(half), 35, 45);
        }

        [Fact]
        public void TwoArcsChainToFullCircle()
        {
            var tol = CutoutCurveMath.ChordToleranceFeet;
            var halfA = CutoutCurveMath.TessellateArc(0, 0, 5, 0, System.Math.PI, tol).ToList();
            var halfB = CutoutCurveMath.TessellateArc(0, 0, 5, System.Math.PI, 2 * System.Math.PI, tol).ToList();
            Assert.True(CutoutCurveMath.PointsNear(halfA[halfA.Count - 1], halfB[0], tol));

            var expected = new List<double[]>();
            foreach (var p in halfA) expected.Add(new[] { p[0], p[1] });
            for (var i = 1; i < halfB.Count; i++) expected.Add(new[] { halfB[i][0], halfB[i][1] });
            Assert.InRange(CutoutCurveMath.AbsoluteArea(expected), 77, 79);

            var loops = CutoutCurveMath.ChainPolylinesToLoops(new[] { halfA, halfB }, tol);
            Assert.Single(loops);
            Assert.InRange(CutoutCurveMath.AbsoluteArea(loops[0]), 77, 79);
        }

        [Fact]
        public void LinePlusArcSlotFormsNonDegenerateLoop()
        {
            var line = new List<double[]>
            {
                new[] { 0.0, 0.0 },
                new[] { 10.0, 0.0 }
            };
            var arc = CutoutCurveMath.TessellateArc(10, 2.5, 2.5, -System.Math.PI / 2, System.Math.PI / 2,
                CutoutCurveMath.ChordToleranceFeet).ToList();
            var top = new List<double[]> { new[] { 10.0, 5.0 }, new[] { 0.0, 5.0 } };
            var closing = new List<double[]> { new[] { 0.0, 5.0 }, new[] { 0.0, 0.0 } };
            var loops = CutoutCurveMath.ChainPolylinesToLoops(new[] { line, arc, top, closing },
                CutoutCurveMath.ChordToleranceFeet);
            Assert.NotEmpty(loops);
            Assert.False(CutoutCurveMath.IsDegenerateLoop(loops[0], out var area));
            Assert.True(area > 30);
        }

        [Fact]
        public void TwoPointSketchIsDegenerate()
        {
            var loop = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 } };
            Assert.True(CutoutCurveMath.IsDegenerateLoop(loop, out _));
        }
    }
}