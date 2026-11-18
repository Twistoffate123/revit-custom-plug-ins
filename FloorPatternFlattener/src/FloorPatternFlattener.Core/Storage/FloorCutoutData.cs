using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FloorPatternFlattener.Geometry;

namespace FloorPatternFlattener.Storage
{
    /// <summary>User-defined hatch cut-outs stored per flattened floor.</summary>
    public sealed class FloorCutoutData
    {
        public int Version = FpfOptions.SchemaDataVersion;
        public List<PickedCutout> Picked = new List<PickedCutout>();
        public List<SketchCutout> Sketches = new List<SketchCutout>();
        /// <summary>When true, each cut footprint is drawn as additional edge lines.</summary>
        public bool EmitCutoutOutline;

        public bool IsEmpty => (Picked == null || Picked.Count == 0) && (Sketches == null || Sketches.Count == 0);

        /// <summary>Stable hash for signature / change detection (ids, offsets, sketch points, outline flag).</summary>
        public string SignatureToken()
        {
            if (IsEmpty) return string.Empty;
            var sb = new StringBuilder();
            sb.Append('O').Append(EmitCutoutOutline ? '1' : '0');
            foreach (var p in Picked ?? new List<PickedCutout>())
            {
                if (p == null || string.IsNullOrEmpty(p.ElementUniqueId)) continue;
                sb.Append("|P").Append(p.ElementUniqueId).Append(':')
                  .Append(p.OffsetMm.ToString("0.###", CultureInfo.InvariantCulture));
            }
            foreach (var s in Sketches ?? new List<SketchCutout>())
            {
                if (s?.Points == null || s.Points.Count < 3) continue;
                sb.Append("|S").Append(s.Points.Count);
                foreach (var pt in s.Points)
                    sb.Append(':').Append(pt[0].ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(pt[1].ToString("R", CultureInfo.InvariantCulture));
            }
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        public IEnumerable<string> DependencyUniqueIds()
        {
            if (Picked == null) yield break;
            foreach (var p in Picked)
                if (!string.IsNullOrEmpty(p?.ElementUniqueId))
                    yield return p.ElementUniqueId;
        }
    }

    public sealed class PickedCutout
    {
        /// <summary>Host-document element UniqueId, or link:hostUniqueId|linkedUniqueId.</summary>
        public string ElementUniqueId = string.Empty;
        public double OffsetMm;
    }

  /// <summary>Closed polygon in internal feet (XY).</summary>
    public sealed class SketchCutout
    {
        public List<double[]> Points = new List<double[]>();
    }

    /// <summary>Tab-safe serialization for extensible storage.</summary>
    public static class CutoutStorageCodec
    {
        private const char RecordSep = '\n';

        public static string Encode(FloorCutoutData data)
        {
            if (data == null || data.IsEmpty) return string.Empty;
            var sb = new StringBuilder();
            sb.Append('V').Append(data.Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(RecordSep).Append('O').Append(data.EmitCutoutOutline ? '1' : '0');
            foreach (var p in data.Picked ?? new List<PickedCutout>())
            {
                if (p == null || string.IsNullOrEmpty(p.ElementUniqueId)) continue;
                sb.Append(RecordSep).Append('P').Append('\t').Append(p.ElementUniqueId).Append('\t')
                  .Append(p.OffsetMm.ToString("R", CultureInfo.InvariantCulture));
            }
            foreach (var s in data.Sketches ?? new List<SketchCutout>())
            {
                if (s?.Points == null || s.Points.Count < 3) continue;
                sb.Append(RecordSep).Append('S');
                foreach (var pt in s.Points)
                {
                    if (pt == null || pt.Length < 2) continue;
                    sb.Append('\t').Append(pt[0].ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(pt[1].ToString("R", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        public static FloorCutoutData Decode(string text)
        {
            var data = new FloorCutoutData();
            if (string.IsNullOrWhiteSpace(text)) return data;
            var lines = text.Split(new[] { RecordSep }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (line[0] == 'V' && line.Length > 1 &&
                    int.TryParse(line.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ver))
                {
                    data.Version = ver;
                    continue;
                }
                if (line[0] == 'O' && line.Length > 1)
                {
                    data.EmitCutoutOutline = line[1] == '1';
                    continue;
                }
                if (line[0] == 'P')
                {
                    var parts = line.Substring(1).Trim().Split('\t');
                    if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
                    {
                        var offset = 0.0;
                        if (parts.Length >= 2)
                            double.TryParse(parts[1].Replace(',', '.'), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out offset);
                        data.Picked.Add(new PickedCutout { ElementUniqueId = parts[0], OffsetMm = offset });
                    }
                    continue;
                }
                if (line[0] == 'S')
                {
                    var sketch = new SketchCutout();
                    var parts = line.Substring(1).Trim().Split('\t');
                    foreach (var part in parts)
                    {
                        if (string.IsNullOrEmpty(part)) continue;
                        var xy = part.Split(',');
                        if (xy.Length < 2) continue;
                        if (!double.TryParse(xy[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
                        if (!double.TryParse(xy[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
                        sketch.Points.Add(new[] { x, y });
                    }
                    if (sketch.Points.Count >= 3) data.Sketches.Add(sketch);
                }
            }
            return data;
        }

        public static SketchCutout RegionToSketch(RegionXY region)
        {
            var sketch = new SketchCutout();
            if (region == null || region.IsEmpty) return sketch;
            var e = region.RawEdges;
            if (e.Count < 8) return sketch;
            var x0 = e[0];
            var y0 = e[1];
            sketch.Points.Add(new[] { x0, y0 });
            var tol = FpfOptions.GeometryTolerance;
            for (var i = 4; i < e.Count; i += 4)
            {
                var x = e[i];
                var y = e[i + 1];
                var last = sketch.Points[sketch.Points.Count - 1];
                if (Math.Abs(last[0] - x) > tol || Math.Abs(last[1] - y) > tol)
                    sketch.Points.Add(new[] { x, y });
            }
            return sketch;
        }

        public static RegionXY SketchToRegion(SketchCutout sketch)
        {
            var region = new RegionXY();
            if (sketch?.Points == null || sketch.Points.Count < 3) return region;
            region.AddPolyline(sketch.Points.Select(p => new[] { p[0], p[1] }).ToList(), close: true);
            return region;
        }
    }
}