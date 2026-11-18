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
        /// <summary>Manual world-Z tweak for this chunk (mm); kept on Refresh.</summary>
        public double ExtraLiftMm;
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

        /// <summary>
        /// Separate schema for the flatten style (Lines / Surface), so the original floor schema stays unchanged and
        /// floors flattened before this option existed read as Lines.
        /// </summary>
        public static readonly Guid FloorStyleSchemaGuid = new Guid("A7D35E92-4B1C-4E6F-8D20-5F9C3B71E0A4");
        private const string FStyle = "Style";

        /// <summary>Per-floor rotation / anchor (separate schema; absent = identity).</summary>
        public static readonly Guid FloorPlacementSchemaGuid = new Guid("D2B6F1C8-5E3A-4A97-9C04-71E8B3A6D5F2");
        private const string FPlacement = "Placement";

        public static readonly Guid FloorLiftSchemaGuid = new Guid("B3E8C5A1-7F2D-4E96-8C10-5A9D3E7F1B42");
        private const string FLiftMm = "LiftMm";

        private const string FVersion = "Version";
        private const string FFlattened = "Flattened";
        private const string FDsIds = "DirectShapeUniqueIds";
        private const string FSignature = "Signature";
        private const string FDeps = "DependencyUniqueIds";
        private const string FSource = "SourceFloorUniqueId";
        private const string FChunk = "ChunkIndex";
        private const string FExtraLiftMm = "ExtraLiftMm";

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

        public static Schema FloorStyleSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(FloorStyleSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(FloorStyleSchemaGuid, "FPF_FloorStyle",
                        "Floor Pattern Flattener: flatten style of the floor (Lines or Surface).");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FStyle, typeof(string));
                    return b.Finish();
                }
            }
        }

        public static Schema FloorPlacementSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(FloorPlacementSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(FloorPlacementSchemaGuid, "FPF_FloorPlacement",
                        "Floor Pattern Flattener: pattern rotation and edge anchor of the floor (plan, internal feet).");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FPlacement, typeof(string));
                    return b.Finish();
                }
            }
        }

        public static Schema FloorLiftSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(FloorLiftSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(FloorLiftSchemaGuid, "FPF_FloorLift",
                        "Floor Pattern Flattener: lift above draped top (millimetres).");
                    b.AddSimpleField(FVersion, typeof(int));
                    // Millimetres stored as a plain number. A length spec forces every
                    // Set/Get to pass a UnitTypeId, and the unitless overload then throws.
                    b.AddSimpleField(FLiftMm, typeof(double)).SetSpec(SpecTypeId.Number);
                    return b.Finish();
                }
            }
        }

        public static readonly Guid DirectShapeExtraSchemaGuid = new Guid("C5D9E8F2-A4B6-4C7D-9E0F-1A2B3C4D5E6F");

        /// <summary>
        /// Unitless replacement for <see cref="DirectShapeExtraSchemaGuid"/>. Models that already
        /// finished the old schema keep it; new writes go here so Set does not require units.
        /// </summary>
        public static readonly Guid DirectShapeExtraNumberSchemaGuid = new Guid("D6E0F1A3-B5C7-4D8E-8F10-2B3C4D5E6F70");

        /// <summary>Unitless replacement for <see cref="FloorLiftSchemaGuid"/>.</summary>
        public static readonly Guid FloorLiftNumberSchemaGuid = new Guid("C4F9D6B2-803E-4FA7-9D21-6BAE4F802C53");

        public static Schema DirectShapeExtraSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(DirectShapeExtraSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(DirectShapeExtraSchemaGuid, "FPF_ShapeExtraLift",
                        "Floor Pattern Flattener: extra manual world-Z lift for one output (millimetres).");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FExtraLiftMm, typeof(double)).SetSpec(SpecTypeId.Number);
                    return b.Finish();
                }
            }
        }

        public static Schema DirectShapeExtraNumberSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(DirectShapeExtraNumberSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(DirectShapeExtraNumberSchemaGuid, "FPF_ShapeExtraLiftNumber",
                        "Floor Pattern Flattener: extra manual lift in millimetres, stored as a unitless number.");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FExtraLiftMm, typeof(double)).SetSpec(SpecTypeId.Number);
                    return b.Finish();
                }
            }
        }

        public static Schema FloorLiftNumberSchema
        {
            get
            {
                lock (Gate)
                {
                    var s = Schema.Lookup(FloorLiftNumberSchemaGuid);
                    if (s != null) return s;
                    var b = NewBuilder(FloorLiftNumberSchemaGuid, "FPF_FloorLiftNumber",
                        "Floor Pattern Flattener: floor lift in millimetres, stored as a unitless number.");
                    b.AddSimpleField(FVersion, typeof(int));
                    b.AddSimpleField(FLiftMm, typeof(double)).SetSpec(SpecTypeId.Number);
                    return b.Finish();
                }
            }
        }

        /// <summary>
        /// True when the field was finished with a measurable spec (Length and similar).
        /// Those fields reject <c>Set/Get</c> unless a <see cref="ForgeTypeId"/> unit is passed.
        /// <see cref="SpecTypeId.Number"/> is unitless and uses the plain overload.
        /// </summary>
        private static bool FieldNeedsUnits(Field field)
        {
            if (field == null) return false;
            ForgeTypeId spec;
            try { spec = field.GetSpecTypeId(); }
            catch { return false; }
            if (spec == null) return false;
            string typeId;
            try { typeId = spec.TypeId; }
            catch { return false; }
            if (string.IsNullOrEmpty(typeId)) return false;
            if (spec.Equals(SpecTypeId.Number)) return false;
            try { return UnitUtils.IsMeasurableSpec(spec); }
            catch { return false; }
        }

        private static double ReadMillimetres(Entity entity, string fieldName)
        {
            var field = entity.Schema.GetField(fieldName);
            if (field == null) return 0;
            if (FieldNeedsUnits(field))
                return entity.Get<double>(field, UnitTypeId.Millimeters);
            return entity.Get<double>(field);
        }

        private static void WriteMillimetres(Entity entity, string fieldName, double millimetres)
        {
            var field = entity.Schema.GetField(fieldName);
            if (field == null) return;
            if (FieldNeedsUnits(field))
                entity.Set(field, millimetres, UnitTypeId.Millimeters);
            else
                entity.Set(field, millimetres);
        }

        private static SchemaBuilder NewBuilder(Guid guid, string name, string doc)
        {
            var b = new SchemaBuilder(guid);
            b.SetSchemaName(name);
            b.SetDocumentation(doc);
            if (!SchemaBuilder.VendorIdIsValid(FpfOptions.VendorId))
                throw new InvalidOperationException("Invalid extensible storage vendor id '" + FpfOptions.VendorId +
                                                    "' (needs 4-253 letters/digits).");
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

        // ------------------------------------------------------------ floor style

        /// <summary>The floor's flatten style; Lines when none is stored.</summary>
        public static FlattenStyle ReadStyle(Element floor)
        {
            if (floor == null) return FlattenStyle.Lines;
            var schema = Schema.Lookup(FloorStyleSchemaGuid);
            if (schema == null) return FlattenStyle.Lines;
            try
            {
                var e = floor.GetEntity(schema);
                if (e == null || !e.IsValid()) return FlattenStyle.Lines;
                var text = e.Get<string>(FStyle);
                if (!string.IsNullOrEmpty(text) && Enum.TryParse(text, true, out FlattenStyle st) &&
                    Enum.IsDefined(typeof(FlattenStyle), st))
                    return st;
            }
            catch
            {
                // ignore
            }
            return FlattenStyle.Lines;
        }

        // ------------------------------------------------------------ hide-live mark (instance Comments)

        /// <summary>True if the floor's Comments carry <see cref="FpfOptions.HideLiveMark"/>.</summary>
        public static bool ReadHideLive(Element floor)
        {
            try
            {
                var p = floor?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                var t = p?.AsString();
                return t != null && t.IndexOf(FpfOptions.HideLiveMark, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Adds or removes the mark, keeping the user's own Comments text. Returns true if the parameter changed.
        /// </summary>
        public static bool WriteHideLive(Element floor, bool on)
        {
            var p = floor?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p == null || p.IsReadOnly) return false;
            var t = p.AsString() ?? string.Empty;
            var has = t.IndexOf(FpfOptions.HideLiveMark, StringComparison.OrdinalIgnoreCase) >= 0;
            if (has == on) return false;
            string n;
            if (on)
            {
                n = string.IsNullOrWhiteSpace(t) ? FpfOptions.HideLiveMark : t.TrimEnd() + " " + FpfOptions.HideLiveMark;
            }
            else
            {
                n = t;
                int i;
                while ((i = n.IndexOf(FpfOptions.HideLiveMark, StringComparison.OrdinalIgnoreCase)) >= 0)
                    n = n.Remove(i, FpfOptions.HideLiveMark.Length);
                n = n.Replace("  ", " ").Trim();
            }
            return p.Set(n);
        }

        /// <summary>
        /// True if the floor was flattened with the removed "Surface" style (stored text "Surface" or "1").
        /// Such floors are rebuilt as Lines and their old surface shape is deleted.
        /// </summary>
        public static bool HasLegacySurfaceStyle(Element floor)
        {
            if (floor == null) return false;
            var schema = Schema.Lookup(FloorStyleSchemaGuid);
            if (schema == null) return false;
            try
            {
                var e = floor.GetEntity(schema);
                if (e == null || !e.IsValid()) return false;
                var text = (e.Get<string>(FStyle) ?? string.Empty).Trim();
                return string.Equals(text, "Surface", StringComparison.OrdinalIgnoreCase) || text == "1";
            }
            catch
            {
                return false;
            }
        }

        public static void WriteStyle(Element floor, FlattenStyle style)
        {
            var e = new Entity(FloorStyleSchema);
            e.Set(FVersion, FpfOptions.SchemaDataVersion);
            e.Set(FStyle, style.ToString());
            floor.SetEntity(e);
        }

        public static void DeleteStyle(Element floor)
        {
            var schema = Schema.Lookup(FloorStyleSchemaGuid);
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

        // ------------------------------------------------------------ floor placement

        /// <summary>The floor's pattern rotation / anchor; identity when none is stored.</summary>
        public static Geometry.PatternPlacement ReadPlacement(Element floor)
        {
            if (floor == null) return Geometry.PatternPlacement.Identity;
            var schema = Schema.Lookup(FloorPlacementSchemaGuid);
            if (schema == null) return Geometry.PatternPlacement.Identity;
            try
            {
                var e = floor.GetEntity(schema);
                if (e == null || !e.IsValid()) return Geometry.PatternPlacement.Identity;
                return Geometry.PatternPlacement.FromStorage(e.Get<string>(FPlacement));
            }
            catch
            {
                return Geometry.PatternPlacement.Identity;
            }
        }

        /// <summary>Stores the placement; identity removes the entity (same as never set).</summary>
        public static void WritePlacement(Element floor, Geometry.PatternPlacement placement)
        {
            if (placement == null || placement.IsIdentity)
            {
                DeletePlacement(floor);
                return;
            }
            var e = new Entity(FloorPlacementSchema);
            e.Set(FVersion, FpfOptions.SchemaDataVersion);
            e.Set(FPlacement, placement.ToStorage());
            floor.SetEntity(e);
        }

        public static void DeletePlacement(Element floor)
        {
            var schema = Schema.Lookup(FloorPlacementSchemaGuid);
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

        // ------------------------------------------------------------ floor lift

        public static double? ReadFloorLiftMm(Element floor)
        {
            if (floor == null) return null;
            var number = ReadMillimetresFrom(floor, FloorLiftNumberSchemaGuid, FLiftMm);
            if (number.HasValue) return number;
            return ReadMillimetresFrom(floor, FloorLiftSchemaGuid, FLiftMm);
        }

        public static void WriteFloorLiftMm(Element floor, double mm)
        {
            if (floor == null) return;
            // Always the unitless schema. The older LiftMm field may be a length spec;
            // writing that one without a unit aborts the transaction.
            var e = new Entity(FloorLiftNumberSchema);
            e.Set(FVersion, FpfOptions.SchemaDataVersion);
            WriteMillimetres(e, FLiftMm, mm);
            floor.SetEntity(e);
            TryWriteLegacyMillimetres(floor, FloorLiftSchema, FLiftMm, mm);
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
            var extra = ReadMillimetresFrom(ds, DirectShapeExtraNumberSchemaGuid, FExtraLiftMm)
                        ?? ReadMillimetresFrom(ds, DirectShapeExtraSchemaGuid, FExtraLiftMm);
            if (extra.HasValue) d.ExtraLiftMm = extra.Value;
            return d;
        }

        public static void WriteDirectShape(Element ds, DirectShapeLinkData data)
        {
            var e = new Entity(DirectShapeSchema);
            e.Set(FVersion, FpfOptions.SchemaDataVersion);
            e.Set(FSource, data.SourceFloorUniqueId ?? string.Empty);
            e.Set(FChunk, data.ChunkIndex);
            ds.SetEntity(e);
            var ex = new Entity(DirectShapeExtraNumberSchema);
            ex.Set(FVersion, FpfOptions.SchemaDataVersion);
            WriteMillimetres(ex, FExtraLiftMm, data.ExtraLiftMm);
            ds.SetEntity(ex);
            TryWriteLegacyMillimetres(ds, DirectShapeExtraSchema, FExtraLiftMm, data.ExtraLiftMm);
        }

        private static double? ReadMillimetresFrom(Element element, Guid schemaGuid, string fieldName)
        {
            try
            {
                var schema = Schema.Lookup(schemaGuid);
                if (schema == null) return null;
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                var field = entity.Schema.GetField(fieldName);
                if (field == null || !CanReadDouble(field)) return null;
                return ReadMillimetres(entity, fieldName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes the legacy schema only when its double field has an explicit spec, so the
        /// call uses the matching overload and cannot throw "Units are required".
        /// </summary>
        private static void TryWriteLegacyMillimetres(Element element, Schema schema, string fieldName, double millimetres)
        {
            if (element == null || schema == null || !schema.IsValidObject) return;
            var field = schema.GetField(fieldName);
            if (!CanReadDouble(field)) return;
            var entity = new Entity(schema);
            entity.Set(FVersion, FpfOptions.SchemaDataVersion);
            WriteMillimetres(entity, fieldName, millimetres);
            element.SetEntity(entity);
        }

        private static bool CanReadDouble(Field field)
        {
            if (field == null) return false;
            if (FieldNeedsUnits(field)) return true;
            ForgeTypeId spec;
            try { spec = field.GetSpecTypeId(); }
            catch { return false; }
            return spec != null && spec.Equals(SpecTypeId.Number);
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
