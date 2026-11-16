using System;
using System.Globalization;
namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// Optional rotation + anchor of the flat pattern, in plan. Pattern space is rotated by <see cref="AngleRad"/>
    /// (anticlockwise from project +X) about the internal origin and then moved so its origin sits on the anchor.
    /// A second alignment slides along the first pick's line so a grid line of the other family meets a second pick.
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
                var mm = Math.Abs(SlideAlongPrimary) * 304.8;
                s += ", slid " + mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm along line 1 (alignment 2)";
            }
            return s;
        }

        /// <summary>
        /// Picks the grid family most perpendicular to alignment 1 and returns the slide along the primary
        /// direction so the nearest grid line in that family passes through <paramref name="pointOnLine2"/>.
        /// </summary>
        public bool TrySlideFromSecondLine(PatternSource pattern, double line2X0, double line2Y0, double line2Dx,
            double line2Dy, out double slideFeet, out string error)
        {
            slideFeet = 0;
            error = null;
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
            var vlen = Math.Sqrt(len2);
            var vx = line2Dx / vlen;
            var vy = line2Dy / vlen;
            if (Math.Abs(ux * vx + uy * vy) > 0.996)
            {
                error = "Alignment 2 must not be parallel to alignment 1. Pick a line at an angle (typically perpendicular).";
                return false;
            }

            PatternGridDef pick = null;
            var bestParallel = 2.0;
            foreach (var layer in pattern.Layers)
            {
                foreach (var g0 in layer.Grids)
                {
                    var g = ApplyGridNoSlide(g0);
                    var gx = Math.Cos(g.Angle);
                    var gy = Math.Sin(g.Angle);
                    var parallel = Math.Abs(gx * ux + gy * uy);
                    if (parallel < bestParallel)
                    {
                        bestParallel = parallel;
                        pick = g;
                    }
                }
            }
            if (pick == null || pick.Offset < 1.0e-9)
            {
                error = "The pattern has no usable grid spacing for alignment 2.";
                return false;
            }

            var nx = -Math.Sin(pick.Angle);
            var ny = Math.Cos(pick.Angle);
            var sPos = (line2X0 - pick.OriginX) * nx + (line2Y0 - pick.OriginY) * ny;
            var k = Math.Round(sPos / pick.Offset);
            var target = k * pick.Offset;
            var misalign = sPos - target;
            var denom = ux * nx + uy * ny;
            if (Math.Abs(denom) < 1.0e-6)
            {
                error = "Alignment 2 is parallel to the joint lines that need to move. Pick a different line.";
                return false;
            }
            slideFeet = misalign / denom;
            return true;
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
