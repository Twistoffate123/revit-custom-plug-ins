using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using FloorPatternFlattener.Geometry;
using FloorPatternFlattener.Session;

namespace FloorPatternFlattener.Overlay
{
    /// <summary>
    /// Draws plan-flat hatch lines for floors registered in <see cref="FlattenSession"/>
    /// into 3D views via DirectContext3D.
    /// </summary>
    public sealed class FloorPatternOverlayServer : IDirectContext3DServer
    {
        public static readonly Guid ServerIdValue =
            new Guid("A7C3E912-4B8F-4D21-9E6A-1F0C8D47B2E5");

        private static FloorPatternOverlayServer _instance;
        private static HatchCache _cache;

        public static FloorPatternOverlayServer Instance =>
            _instance ?? (_instance = new FloorPatternOverlayServer());

        private FloorPatternOverlayServer() { }

        public Guid GetServerId() => ServerIdValue;

        public string GetVendorId() => "FPF";

        public string GetName() => "Floor Pattern Flattener Overlay";

        public string GetDescription() =>
            "Draws horizontal hatch overlays for floors so finish patterns appear plan-flat in 3D.";

        public ExternalServiceId GetServiceId() =>
            ExternalServices.BuiltInExternalServices.DirectContext3DService;

        public string GetApplicationId() => "FloorPatternFlattener";

        public string GetSourceId() => string.Empty;

        public bool CanExecute(View view)
        {
            return view is View3D v3d && !v3d.IsTemplate;
        }

        public bool UseInTransparentPass(View view) => false;

        public Outline GetBoundingBox(View view)
        {
            if (!(view is View3D) || view.Document == null)
                return null;

            var doc = view.Document;
            FlattenSession.RemoveInvalidIds(doc);
            var ids = FlattenSession.GetFloors(doc);
            if (ids.Count == 0) return null;

            XYZ min = null;
            XYZ max = null;
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el == null) continue;
                var bb = el.get_BoundingBox(view) ?? el.get_BoundingBox(null);
                if (bb == null) continue;
                min = min == null ? bb.Min : new XYZ(
                    Math.Min(min.X, bb.Min.X),
                    Math.Min(min.Y, bb.Min.Y),
                    Math.Min(min.Z, bb.Min.Z));
                max = max == null ? bb.Max : new XYZ(
                    Math.Max(max.X, bb.Max.X),
                    Math.Max(max.Y, bb.Max.Y),
                    Math.Max(max.Z, bb.Max.Z));
            }

            if (min == null || max == null) return null;
            return new Outline(
                new XYZ(min.X, min.Y, min.Z - 0.1),
                new XYZ(max.X, max.Y, max.Z + 0.1));
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            if (!CanExecute(view) || DrawContext.IsInterrupted())
                return;

            var doc = view.Document;
            if (doc == null) return;

            FlattenSession.RemoveInvalidIds(doc);
            var ids = FlattenSession.GetFloors(doc);
            if (ids.Count == 0) return;

            var cache = GetOrBuildCache(doc, ids);
            if (cache == null || cache.Segments.Count == 0) return;

            SubmitLineList(cache);
        }

        private static HatchCache GetOrBuildCache(Document doc, IReadOnlyCollection<ElementId> ids)
        {
            var gen = FlattenSession.Generation;
            if (_cache != null && _cache.Matches(doc, gen, ids))
                return _cache;

            _cache?.DisposeBuffers();

            var allSegments = new List<(XYZ A, XYZ B)>();
            Color lineColor = new Color(80, 80, 80);

            foreach (var id in ids)
            {
                if (DrawContext.IsInterrupted()) return _cache;
                var floor = doc.GetElement(id) as Floor;
                if (floor == null) continue;

                var hatch = FloorHatchBuilder.Build(floor, doc);
                if (hatch.Segments.Count == 0) continue;
                lineColor = hatch.Color ?? lineColor;
                allSegments.AddRange(hatch.Segments);
            }

            _cache = new HatchCache(doc.GetHashCode(), gen, ids, allSegments, lineColor);
            return _cache;
        }

        private static void SubmitLineList(HatchCache cache)
        {
            var segments = cache.Segments;
            var vertexCount = segments.Count * 2;
            if (vertexCount < 2) return;

            if (!cache.EnsureBuffers())
                return;

            DrawContext.FlushBuffer(
                cache.VertexBuffer,
                vertexCount,
                cache.IndexBuffer,
                cache.IndexCount,
                cache.VertexFormat,
                cache.Effect,
                PrimitiveType.LineList,
                0,
                segments.Count);
        }

        public static void Register()
        {
            var service = ExternalServiceRegistry.GetService(
                ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
            if (service == null) return;

            var server = Instance;
            if (!service.IsRegisteredServerId(server.GetServerId()))
                service.AddServer(server);

            var active = new List<Guid>(service.GetActiveServerIds());
            if (!active.Contains(server.GetServerId()))
            {
                active.Add(server.GetServerId());
                service.SetActiveServers(active);
            }
        }

        public static void Unregister()
        {
            try
            {
                var service = ExternalServiceRegistry.GetService(
                    ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
                if (service == null) return;

                var active = new List<Guid>(service.GetActiveServerIds());
                if (active.Remove(ServerIdValue))
                    service.SetActiveServers(active);

                if (service.IsRegisteredServerId(ServerIdValue))
                    service.RemoveServer(ServerIdValue);
            }
            catch
            {
                // Revit may already have torn the service down.
            }

            InvalidateCache();
        }

        public static void EnsureRegistered()
        {
            try { Register(); }
            catch { /* first command retries */ }
        }

        public static void InvalidateCache()
        {
            _cache?.DisposeBuffers();
            _cache = null;
        }

        public static void RefreshViews(Document doc, UIDocument uidoc = null)
        {
            if (doc == null) return;
            try
            {
                uidoc?.RefreshActiveView();
            }
            catch
            {
            }
        }

        private sealed class HatchCache
        {
            public int DocKey { get; }
            public int Generation { get; }
            public HashSet<ElementId> Ids { get; }
            public List<(XYZ A, XYZ B)> Segments { get; }
            public Color Color { get; }
            public VertexBuffer VertexBuffer { get; private set; }
            public IndexBuffer IndexBuffer { get; private set; }
            public VertexFormat VertexFormat { get; private set; }
            public EffectInstance Effect { get; private set; }
            public int IndexCount { get; private set; }

            public HatchCache(
                int docKey,
                int generation,
                IEnumerable<ElementId> ids,
                List<(XYZ A, XYZ B)> segments,
                Color color)
            {
                DocKey = docKey;
                Generation = generation;
                Ids = new HashSet<ElementId>(ids);
                Segments = segments ?? new List<(XYZ A, XYZ B)>();
                Color = color ?? new Color(80, 80, 80);
            }

            public bool Matches(Document doc, int generation, IReadOnlyCollection<ElementId> ids)
            {
                if (doc == null || generation != Generation || doc.GetHashCode() != DocKey)
                    return false;
                if (ids.Count != Ids.Count) return false;
                foreach (var id in ids)
                {
                    if (!Ids.Contains(id)) return false;
                }
                return VertexBuffer != null;
            }

            public bool EnsureBuffers()
            {
                if (VertexBuffer != null) return true;
                if (Segments.Count == 0) return false;

                var vertexCount = Segments.Count * 2;
                var vertexFloats = VertexPosition.GetSizeInFloats() * vertexCount;
                var vertexBuffer = new VertexBuffer(vertexFloats);
                vertexBuffer.Map(vertexFloats);
                var vstream = vertexBuffer.GetVertexStreamPosition();
                foreach (var (a, b) in Segments)
                {
                    vstream.AddVertex(new VertexPosition(a));
                    vstream.AddVertex(new VertexPosition(b));
                }
                vertexBuffer.Unmap();

                var indexCount = Segments.Count * 2;
                var indexBuffer = new IndexBuffer(indexCount);
                indexBuffer.Map(indexCount);
                var istream = indexBuffer.GetIndexStreamLine();
                for (var i = 0; i < Segments.Count; i++)
                    istream.AddLine(new IndexLine(i * 2, i * 2 + 1));
                indexBuffer.Unmap();

                var formatBits = VertexFormatBits.Position;
                var vertexFormat = new VertexFormat(formatBits);
                var effect = new EffectInstance(formatBits);
                effect.SetColor(Color);

                VertexBuffer = vertexBuffer;
                IndexBuffer = indexBuffer;
                VertexFormat = vertexFormat;
                Effect = effect;
                IndexCount = indexCount;
                return true;
            }

            public void DisposeBuffers()
            {
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
                VertexFormat?.Dispose();
                Effect?.Dispose();
                VertexBuffer = null;
                IndexBuffer = null;
                VertexFormat = null;
                Effect = null;
            }
        }
    }
}
