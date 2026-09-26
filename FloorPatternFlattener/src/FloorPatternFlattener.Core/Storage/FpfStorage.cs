using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace FloorPatternFlattener.Storage
{
    /// <summary>Data stored on a flattened floor.</summary>
    public sealed class FloorLinkData
    {
        public int Version = FpfOptions.SchemaDataVersion;
        public bool Flattened;
        public List<string> DirectShapeUniqueIds = new List<string>();
        public string Signature = string.Empty;
        public List<string> DependencyUniqueIds = new List<string>();
    }

    /// <summary>Data stored on a generated DirectShape.</summary>
    public sealed class DirectShapeLinkData
    {
        public int Version = FpfOptions.SchemaDataVersion;
        public string SourceFloorUniqueId = string.Empty;
        public int ChunkIndex;
    }

    /// <summary>
    /// Extensible storage schemas. GUIDs are fixed forever; read/write access is Public so files open cleanly
    /// (and data can be cleaned up) on machines without the add-in. VendorId matches the .addin manifest.
    /// Never change fields of an existing schema: add a new schema GUID instead and migrate.
    /// </summary>
    public static class FpfStorage
    {
        public static readonly Guid FloorSchemaGuid = new Guid("6F0B8E1A-3C57-4D8E-9A41-2B7C5D90E1F3");
        public static readonly Guid DirectShapeSchemaGuid = new Guid("C4A2D9E7-81B3-4F6A-B05C-9E3D7A21F468");

        private const string FVersion = "Version";
        private const string FFlattened = "Flattened";
        private const string FDsIds = "DirectShapeUniqueIds";
        private const string FSignature = "Signature";
        private const string FDeps = "DependencyUniqueIds";
        private const string FSource = "SourceFloorUniqueId";
        private const string FChunk = "ChunkIndex";

        private static readonly object Gate = new object();

        public static Schema FloorSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(FloorSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(FloorSchemaGuid, "FPF_FloorLink",
                        "Floor Pattern Flattener: floor is flattened; links to generated DirectShapes.");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FFlattened, typeof(bool));
                    b.AddArrayField(FDsIds, typeof(string));
                    b.AddSimpleField(FSignature, typeof(string));
                    b.AddArrayField(FDeps, typeof(string));
                    return b.Finish();
                }
            }
        }

        public static Schema DirectShapeSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(DirectShapeSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(DirectShapeSchemaGuid, "FPF_FlatPatternShape",
                        "Floor Pattern Flattener: generated flat pattern lines; source floor link.");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FSource, typeof(string));
                    b.AddSimpleField(FChunk, typeof(int));
                    return b.Finish();
                }
            }
        }

        private static SchemaBuilder NewBuilder(Guid guid, string name, string doc)
        {
            var b = new SchemaBuilder(guid);
            b.SetSchemaName(name);
            b.SetDocumentation(doc);
            b.SetVendorId(FpfOptions.VendorId);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            return b;
        }

        // ------------------------------------------------------------ floor

        public static FloorLinkData ReadFloor(Element floor)
        {
            if (floor == null) return null;
            var schema = Schema.Lookup(FloorSchemaGuid);
            if (schema == null) return null;
            Entity e;
            try { e = floor.GetEntity(schema); } catch { return null; }
            if (e == null || !e.IsValid()) return null;

            var d = new FloorLinkData();
            try { d.Version = e.Get<int>(FVersion); } catch { /* ignore */ }
            try { d.Flattened = e.Get<bool>(FFlattened); } catch { /* ignore */ }
            try { d.Signature = e.Get<string>(FSignature) ?? string.Empty; } catch { /* ignore */ }
            try
            {
                var ids = e.Get<IList<string>>(FDsIds);
                if (ids != null) d.DirectShapeUniqueIds = new List<string>(ids);
            }
            catch { /* ignore */ }
            try
            {
                var deps = e.Get<IList<string>>(FDeps);
                if (deps != null) d.DependencyUniqueIds = new List<string>(deps);
            }
            catch { /* ignore */ }
            return d;
        }

        public static bool IsFlattened(Element floor)
        {
            var d = ReadFloor(floor);
            return d != null && d.Flattened;
        }

        public static void WriteFloor(Element floor, FloorLinkData data)
        {
            var schema = FloorSchema;
            var e = new Entity(schema);
            e.Set(FVersion, FpfOptions.SchemaDataVersion);
            e.Set(FFlattened, data.Flattened);
            e.Set<IList<string>>(FDsIds, new List<string>(data.DirectShapeUniqueIds ?? new List<string>()));
            e.Set(FSignature, data.Signature ?? string.Empty);
            e.Set<IList<string>>(FDeps, new List<string>(data.DependencyUniqueIds ?? new List<string>()));
            floor.SetEntity(e);
        }

        public static void DeleteFloor(Element floor)
        {
            var schema = Schema.Lookup(FloorSchemaGuid);
            if (schema == null) return;
            try
            {
                var e = floor.GetEntity(schema);
                if (e != null && e.IsValid()) floor.DeleteEntity(schema);
            }
            catch
            {
                // ignore
            }
        }

        // ------------------------------------------------------------ direct shape

        public static DirectShapeLinkData ReadDirectShape(Element ds)
        {
            if (ds == null) return null;
            var schema = Schema.Lookup(DirectShapeSchemaGuid);
            if (schema == null) return null;
            Entity e;
            try { e = ds.GetEntity(schema); } catch { return null; }
            if (e == null || !e.IsValid()) return null;
            var d = new DirectShapeLinkData();
            try { d.Version = e.Get<int>(FVersion); } catch { /* ignore */ }
            try { d.SourceFloorUniqueId = e.Get<string>(FSource) ?? string.Empty; } catch { /* ignore */ }
            try { d.ChunkIndex = e.Get<int>(FChunk); } catch { /* ignore */ }
            return d;
        }

        public static void WriteDirectShape(Element ds, DirectShapeLinkData data)
        {
            var e = new Entity(DirectShapeSchema);
            e.Set(FVersion, FpfOptions.SchemaDataVersion);
            e.Set(FSource, data.SourceFloorUniqueId ?? string.Empty);
            e.Set(FChunk, data.ChunkIndex);
            ds.SetEntity(e);
        }

        // ------------------------------------------------------------ queries

        /// <summary>Floors carrying our entity with Flattened == true.</summary>
        public static List<Floor> GetFlattenedFloors(Document doc)
        {
            var list = new List<Floor>();
            if (Schema.Lookup(FloorSchemaGuid) == null) return list;
            var col = new FilteredElementCollector(doc)
                .OfClass(typeof(Floor))
                .WherePasses(new ExtensibleStorageFilter(FloorSchemaGuid));
            foreach (var el in col)
                if (el is Floor f && IsFlattened(f)) list.Add(f);
            return list;
        }

        /// <summary>All DirectShapes generated by this add-in.</summary>
        public static List<DirectShape> GetGeneratedDirectShapes(Document doc)
        {
            var list = new List<DirectShape>();
            if (Schema.Lookup(DirectShapeSchemaGuid) == null) return list;
            var col = new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .WherePasses(new ExtensibleStorageFilter(DirectShapeSchemaGuid));
            foreach (var el in col)
                if (el is DirectShape ds) list.Add(ds);
            return list;
        }
    }
}
