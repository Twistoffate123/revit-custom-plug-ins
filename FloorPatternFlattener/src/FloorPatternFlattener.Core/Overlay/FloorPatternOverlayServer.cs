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

            var allSegments = new List<(XYZ A, XYZ B)>();
            Color lineColor = new Color(80, 80, 80);

            foreach (var id in ids)
            {
                if (DrawContext.IsInterrupted()) return;
                var floor = doc.GetElement(id) as Floor;
                if (floor == null) continue;

                var hatch = FloorHatchBuilder.Build(floor, doc);
                if (hatch.Segments.Count == 0) continue;
                lineColor = hatch.Color ?? lineColor;
                allSegments.AddRange(hatch.Segments);
            }

            if (allSegments.Count == 0) return;

            SubmitLineList(allSegments, lineColor);
        }

        private static void SubmitLineList(IList<(XYZ A, XYZ B)> segments, Color color)
        {
            var vertexCount = segments.Count * 2;
            if (vertexCount < 2) return;

            var vertexFloats = VertexPosition.GetSizeInFloats() * vertexCount;
            var vertexBuffer = new VertexBuffer(vertexFloats);
            vertexBuffer.Map(vertexFloats);
            var vstream = vertexBuffer.GetVertexStreamPosition();
            foreach (var (a, b) in segments)
            {
                vstream.AddVertex(new VertexPosition(a));
                vstream.AddVertex(new VertexPosition(b));
            }
            vertexBuffer.Unmap();

            var indexCount = segments.Count * 2;
            var indexBuffer = new IndexBuffer(indexCount);
            indexBuffer.Map(indexCount);
            var istream = indexBuffer.GetIndexStreamLine();
            for (var i = 0; i < segments.Count; i++)
            {
                istream.AddLine(new IndexLine(i * 2, i * 2 + 1));
            }
            indexBuffer.Unmap();

            var formatBits = VertexFormatBits.Position;
            var vertexFormat = new VertexFormat(formatBits);
            var effect = new EffectInstance(formatBits);

            try
            {
                var tEff = effect.GetType();
                var m = tEff.GetMethod("SetColor", new[] { typeof(Color) })
                        ?? tEff.GetMethod("SetDiffuseColor", new[] { typeof(Color) });
                m?.Invoke(effect, new object[] { color });
            }
            catch
            {
            }

            DrawContext.FlushBuffer(
                vertexBuffer,
                vertexCount,
                indexBuffer,
                indexCount,
                vertexFormat,
                effect,
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
            {
                service.AddServer(server);
            }

            var active = new List<Guid>(service.GetActiveServerIds());
            if (!active.Contains(server.GetServerId()))
            {
                active.Add(server.GetServerId());
                service.SetActiveServers(active);
            }
        }

        public static void EnsureRegistered()
        {
            try { Register(); }
            catch
            {
            }
        }

        public static void RefreshViews(Document doc, UIDocument uidoc = null)
        {
            if (doc == null) return;
            try
            {
                var view = uidoc?.ActiveView ?? doc.ActiveView;
                if (view != null)
                {
                    uidoc?.RefreshActiveView();
                }
            }
            catch
            {
            }
        }
    }
}
