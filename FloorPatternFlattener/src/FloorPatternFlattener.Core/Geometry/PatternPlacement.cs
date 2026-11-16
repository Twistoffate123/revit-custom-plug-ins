using System;
using System.Globalization;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// Optional rotation + anchor of the flat pattern, in plan. Pattern space is rotated by <see cref="AngleRad"/>
    /// (anticlockwise from project +X) about the internal origin and then moved so its origin sits on the anchor.
    /// A second alignment slides along the first pick's line so a grid line of the chosen family meets a second pick.
    /// Identity (0 deg, no anchor) = today's behaviour: exactly the live floor pattern.
    /// </summary>
    public sealed class PatternPlacement
    {
        public double AngleRad { get; set; }
        public bool HasAnchor { get; set; }
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }

        /// <summary>Slide along <see cref="AngleRad"/> direction (feet), from the first anchor, after alignment 2.</summary>
        public double SlideAlongPrimary { get; set; }

        /// <summary>Alignment 2 was applied (even when the computed slide is 0).</summary>
        public bool HasSecondaryAlign { get; set; }

        public static PatternPlacement Identity => new PatternPlacement();

        public bool IsIdentity => Math.Abs(NormalizedAngle) < 1.0e-9 && !HasAnchor && !HasSecondaryAlign;

        public double AngleDeg
        {
            get => AngleRad * 180.0 / Math.PI;
            set => AngleRad = value * Math.PI / 180.0;
        }

        /// <summary>Angle wrapped to [0, 2pi).</summary>
        public double NormalizedAngle
        {
            get
            {
                var a = AngleRad % (2 * Math.PI);
                if (a < 0) a += 2 * Math.PI;
                if (Math.Abs(a - 2 * Math.PI) < 1.0e-9) a = 0;
                return a;
            }
        }

        public PatternPlacement Clone() => new PatternPlacement
        {
            AngleRad = AngleRad, HasAnchor = HasAnchor, AnchorX = AnchorX, AnchorY = AnchorY,
            SlideAlongPrimary = SlideAlongPrimary, HasSecondaryAlign = HasSecondaryAlign
        };

        /// <summary>Anchor point used for translation after rotation (includes slide along the primary line).</summary>
        public void EffectiveAnchor(out double x, out double y)
        {
            x = HasAnchor ? AnchorX : 0;
            y = HasAnchor ? AnchorY : 0;
            if (HasAnchor && HasSecondaryAlign)
            {
                x += SlideAlongPrimary * Math.Cos(AngleRad);
                y += SlideAlongPrimary * Math.Sin(AngleRad);
            }
        }

        /// <summary>Maps a pattern-space point to plan: rotate, then translate to the anchor.</summary>
        public void Map(double u, double v, out double x, out double y)
        {
            var c = Math.Cos(AngleRad);
            var s = Math.Sin(AngleRad);
            EffectiveAnchor(out var ax, out var ay);
            x = u * c - v * s + ax;
            y = u * s + v * c + ay;
        }

        /// <summary>The grid as it lies in plan after this placement (identity returns the same instance).</summary>
        public PatternGridDef Apply(PatternGridDef g)
        {
            if (IsIdentity || g == null) return g;
            Map(g.OriginX, g.OriginY, out var ox, out var oy);
            return new PatternGridDef
            {
                Angle = g.Angle + AngleRad,
                OriginX = ox,
                OriginY = oy,
                Offset = g.Offset,
                Shift = g.Shift,
                Dashes = g.Dashes
            };
        }

        /// <summary>Stable text for storage and the signature ("" for identity).</summary>
        public string ToStorage()
        {
            if (IsIdentity) return string.Empty;
            var sb = "A=" + F(NormalizedAngle);
            if (HasAnchor) sb += ";X=" + F(AnchorX) + ";Y=" + F(AnchorY);
            if (HasSecondaryAlign) sb += ";S=" + F(SlideAlongPrimary);
            return sb;
        }

        public static PatternPlacement FromStorage(string text)
        {
            var p = new PatternPlacement();
            if (string.IsNullOrWhiteSpace(text)) return p;
            bool hx = false, hy = false;
            foreach (var part in text.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length != 2 ||
                    !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                switch (kv[0].Trim())
                {
                    case "A": p.AngleRad = v; break;
                    case "X": p.AnchorX = v; hx = true; break;
                    case "Y": p.AnchorY = v; hy = true; break;
                    case "S":
                        p.SlideAlongPrimary = v;
                        p.HasSecondaryAlign = true;
                        break;
                }
            }
            p.HasAnchor = hx && hy;
            return p;
        }

        public string Describe()
        {
            if (IsIdentity) return "matches the live floor pattern (no rotation, project origin)";
            var d = (NormalizedAngle * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " deg";
            var s = "rotated " + d + (HasAnchor ? ", anchored on alignment 1" : ", about the project origin");
            if (HasSecondaryAlign)
            {
                var mm = SlideAlongPrimary * 304.8;
                s += ", slid " + mm.ToString("0.##", CultureInfo.InvariantCulture) +
                     " mm along line 1 (alignment 2)";
            }
            return s;
        }

        /// <summary>
        /// Chooses the grid family most parallel to pick line 2 and slides along alignment 1 so that grid line
        /// passes through the pick point. Running-bond shift is included (parallel family uses along-line slide).
        /// </summary>
        public bool TrySlideFromSecondLine(PatternSource pattern, double qx, double qy, double line2Dx, double line2Dy,
            out double slideFeet, out string error, out string detail)
        {
            slideFeet = 0;
            error = null;
            detail = null;
            if (!HasAnchor)
            {
                error = "Pick alignment 1 (Align to Edge or Line) before alignment 2.";
                return false;
            }
            if (pattern == null || pattern.Layers.Count == 0)
            {
                error = "Could not read the floor material pattern.";
                return false;
            }

            var ux = Math.Cos(AngleRad);
            var uy = Math.Sin(AngleRad);
            var len2 = line2Dx * line2Dx + line2Dy * line2Dy;
            if (len2 < 1.0e-12)
            {
                error = "Alignment 2: the picked line has no direction in plan.";
                return false;
            }
            var vx = line2Dx / Math.Sqrt(len2);
            var vy = line2Dy / Math.Sqrt(len2);
            var parallelToOne = Math.Abs(ux * vx + uy * vy);
            var perpScore = Math.Abs(-uy * vx + ux * vy);
            // Parallel to line 1: staggered joints along the course (running bond). Otherwise: lines across the courses.
            var useParallelFamily = parallelToOne > perpScore;

            PatternGridDef pick = null;
            var bestScore = -1.0;
            foreach (var layer in pattern.Layers)
            {
                foreach (var g0 in layer.Grids)
                {
                    var g = ApplyGridNoSlide(g0);
                    var gx = Math.Cos(g.Angle);
                    var gy = Math.Sin(g.Angle);
                    var alongOne = Math.Abs(gx * ux + gy * uy);
                    if (useParallelFamily)
                    {
                        if (alongOne < 0.996) continue;
                        var score = alongOne * g.Offset;
                        if (score > bestScore) { bestScore = score; pick = g; }
                    }
                    else
                    {
                        if (alongOne > 0.15) continue;
                        var score = Math.Abs(gx * vx + gy * vy);
                        if (score > bestScore) { bestScore = score; pick = g; }
                    }
                }
            }
            if (pick == null || pick.Offset < 1.0e-9)
            {
                error = "The pattern has no usable grid family for alignment 2.";
                return false;
            }

            var dx = Math.Cos(pick.Angle);
            var dy = Math.Sin(pick.Angle);
            var nx = -dy;
            var ny = dx;
            var ox = pick.OriginX;
            var oy = pick.OriginY;

            var sPos = (qx - ox) * nx + (qy - oy) * ny;
            var tPos = (qx - ox) * dx + (qy - oy) * dy;
            var k0 = (long)Math.Round(sPos / pick.Offset);
            const int search = 8;
            var bestSlide = 0.0;
            var bestDist = double.MaxValue;
            long bestK = k0;
            var alongOneGrid = Math.Abs(dx * ux + dy * uy) > 0.996;

            for (var dk = -search; dk <= search; dk++)
            {
                var k = k0 + dk;
                double s;
                if (alongOneGrid)
                    s = tPos - k * pick.Shift;
                else
                {
                    var denom = ux * nx + uy * ny;
                    if (Math.Abs(denom) < 1.0e-6) continue;
                    s = (sPos - k * pick.Offset) / denom;
                }

                var px = ox + s * ux + k * (pick.Shift * dx + pick.Offset * nx);
                var py = oy + s * uy + k * (pick.Shift * dy + pick.Offset * ny);
                var dist = Math.Abs((qx - px) * nx + (qy - py) * ny);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestSlide = s;
                    bestK = k;
                }
            }

            slideFeet = bestSlide;
            var mm = slideFeet * 304.8;
            detail = (useParallelFamily ? "joint family parallel to line 1" : "joint family across line 1") +
                     ", course k=" + bestK.ToString(CultureInfo.InvariantCulture) +
                     ", residual " + (bestDist * 304.8).ToString("0.##", CultureInfo.InvariantCulture) + " mm";
            if (Math.Abs(bestDist) > 25.0 / 304.8)
                error = "Alignment 2: the pick is farther than 25 mm from any pattern joint (" + detail + "). " +
                        "Check alignment 1 and pick a line on the joint.";
            return true;
        }

        /// <summary>Quick numeric self-check for running-bond slide math (no Revit).</summary>
        public static string RunAlignmentSelfCheck()
        {
            var pat = new PatternSource();
            var layer = new PatternLayer { Kind = PatternLayerKind.Foreground };
            const double w = 200.0 / 304.8;
            const double l = 400.0 / 304.8;
            layer.Grids.Add(new PatternGridDef { Angle = 0, OriginX = 0, OriginY = 0, Offset = l, Shift = 0 });
            layer.Grids.Add(new PatternGridDef { Angle = Math.PI / 2, OriginX = 0, OriginY = 0, Offset = w, Shift = l / 2 });
            pat.Layers.Add(layer);
            var p = new PatternPlacement { HasAnchor = true, AngleRad = 0, AnchorX = 0, AnchorY = 0 };
            if (!p.TrySlideFromSecondLine(pat, w / 2, 10.0 / 304.8, 0, 1, out var slide, out var err, out var det))
                return "FAIL: " + err;
            if (Math.Abs(slide) < 1.0e-9)
                return "FAIL: expected non-zero slide for staggered pick, got 0. " + det;
            return "OK running-bond slide=" + (slide * 304.8).ToString("0.##", CultureInfo.InvariantCulture) + " mm " + det;
        }

        private PatternGridDef ApplyGridNoSlide(PatternGridDef g)
        {
            if (g == null) return null;
            var c = Math.Cos(AngleRad);
            var s = Math.Sin(AngleRad);
            var ax = HasAnchor ? AnchorX : 0;
            var ay = HasAnchor ? AnchorY : 0;
            var ox = g.OriginX * c - g.OriginY * s + ax;
            var oy = g.OriginX * s + g.OriginY * c + ay;
            return new PatternGridDef
            {
                Angle = g.Angle + AngleRad,
                OriginX = ox,
                OriginY = oy,
                Offset = g.Offset,
                Shift = g.Shift,
                Dashes = g.Dashes
            };
        }

        public bool SameAs(PatternPlacement o) => o != null && ToStorage() == o.ToStorage();

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}