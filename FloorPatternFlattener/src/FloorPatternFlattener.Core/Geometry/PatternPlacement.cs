using System;
using System.Globalization;

namespace FloorPatternFlattener.Geometry
{
    /// <summary>
    /// Optional rotation + anchor of the flat pattern, in plan. Pattern space is rotated by <see cref="AngleRad"/>
    /// (anticlockwise from project +X) about the internal origin and then moved so its origin sits on the anchor.
    /// Identity (0 deg, no anchor) = today's behaviour: exactly the live floor pattern.
    /// </summary>
    public sealed class PatternPlacement
    {
        public double AngleRad { get; set; }
        public bool HasAnchor { get; set; }
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }

        public static PatternPlacement Identity => new PatternPlacement();

        public bool IsIdentity => Math.Abs(NormalizedAngle) < 1.0e-9 && !HasAnchor;

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
            { AngleRad = AngleRad, HasAnchor = HasAnchor, AnchorX = AnchorX, AnchorY = AnchorY };

        /// <summary>Maps a pattern-space point to plan: rotate, then translate to the anchor.</summary>
        public void Map(double u, double v, out double x, out double y)
        {
            var c = Math.Cos(AngleRad);
            var s = Math.Sin(AngleRad);
            x = u * c - v * s + (HasAnchor ? AnchorX : 0);
            y = u * s + v * c + (HasAnchor ? AnchorY : 0);
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
            return "A=" + F(NormalizedAngle) + (HasAnchor ? ";X=" + F(AnchorX) + ";Y=" + F(AnchorY) : string.Empty);
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
                }
            }
            p.HasAnchor = hx && hy;
            return p;
        }

        public string Describe()
        {
            if (IsIdentity) return "matches the live floor pattern (no rotation, project origin)";
            var d = (NormalizedAngle * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " deg";
            return "rotated " + d + (HasAnchor ? ", anchored on a picked edge" : ", about the project origin");
        }

        public bool SameAs(PatternPlacement o) => o != null && ToStorage() == o.ToStorage();

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
