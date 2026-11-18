using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using FloorPatternFlattener.Services;

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

        private static readonly HashSet<string> LoggedSchemaKeys = new HashSet<string>();

        /// <summary>Called when a Flatten/Refresh session starts so the next write logs the live schema again.</summary>
        public static void ResetSchemaLog()
        {
            lock (Gate) LoggedSchemaKeys.Clear();
        }

        private static Field FindField(Schema schema, string fieldName)
        {
            if (schema == null || string.IsNullOrEmpty(fieldName)) return null;
            try { return schema.GetField(fieldName); }
            catch { return null; }
        }

        private static void LogSchemaOnce(Schema schema)
        {
            if (schema == null || !schema.IsValidObject) return;
            var key = FpfRefreshLog.SessionSerial.ToString() + "|" + schema.GUID;
            lock (Gate)
            {
                if (!LoggedSchemaKeys.Add(key)) return;
            }

            var sb = new StringBuilder();
            sb.Append("guid=").Append(schema.GUID.ToString());
            try { sb.Append(" name=").Append(schema.SchemaName); } catch { /* ignore */ }
            IList<Field> fields = null;
            try { fields = schema.ListFields(); } catch { fields = null; }
            if (fields != null)
            {
                foreach (var field in fields)
                {
                    if (field == null) continue;
                    sb.Append(" | field=").Append(field.FieldName);
                    try { sb.Append(" type=").Append(field.ValueType != null ? field.ValueType.FullName : "?"); }
                    catch { sb.Append(" type=?"); }
                    try
                    {
                        var spec = field.GetSpecTypeId();
                        var id = spec == null ? null : spec.TypeId;
                        sb.Append(" spec=").Append(string.IsNullOrEmpty(id) ? "(empty)" : id);
                    }
                    catch (Exception ex)
                    {
                        sb.Append(" spec-error=").Append(OneLine(ex));
                    }
                    try
                    {
                        var choice = Classify(field, out var unit);
                        sb.Append(" choice=").Append(choice.ToString());
                        var unitId = "";
                        try { unitId = unit == null ? "" : unit.TypeId; } catch { unitId = ""; }
                        sb.Append(" unit=").Append(string.IsNullOrEmpty(unitId) ? "(none)" : unitId);
                    }
                    catch (Exception ex)
                    {
                        sb.Append(" choice-error=").Append(OneLine(ex));
                    }
                }
            }
            FpfRefreshLog.LogPhaseAlways("link-schema", sb.ToString());
        }

        private static void LogFieldFailure(Schema schema, string fieldName, Exception ex)
        {
            var key = FpfRefreshLog.SessionSerial.ToString() + "|err|" +
                      (schema != null ? schema.GUID.ToString() : "?") + "|" + (fieldName ?? "?");
            lock (Gate)
            {
                if (!LoggedSchemaKeys.Add(key)) return;
            }
            FpfRefreshLog.LogPhaseAlways("link-schema",
                "guid=" + (schema != null ? schema.GUID.ToString() : "?") +
                " field=" + (fieldName ?? "?") +
                " error=" + OneLine(ex));
        }

        private static string OneLine(Exception ex)
        {
            if (ex == null) return "";
            return ex.ToString().Replace("\r", " ").Replace("\n", " | ");
        }

        /// <summary>
        /// Inspects the field that is already stored for this schema GUID.
        /// Empty spec or a non-double uses no unit. Length uses feet. Number uses general.
        /// Any other spec uses the first unit <c>GetValidUnits</c> reports, and only if
        /// <c>Field.CompatibleUnit</c> accepts it.
        /// </summary>
        private static EsUnitChoice Classify(Field field, out ForgeTypeId unit)
        {
            unit = null;
            if (field == null) return EsUnitChoice.NoUnit;
            var isDouble = false;
            try { isDouble = field.ValueType == typeof(double); } catch { /* ignore */ }

            ForgeTypeId spec = null;
            try { spec = field.GetSpecTypeId(); } catch { spec = null; }
            var typeId = "";
            try { typeId = spec == null ? "" : (spec.TypeId ?? ""); } catch { typeId = ""; }
            var empty = spec == null || string.IsNullOrEmpty(typeId);
            var token = "";
            if (!empty)
            {
                try
                {
                    if (spec.Equals(SpecTypeId.Length)) token = "Length";
                    else if (spec.Equals(SpecTypeId.Number)) token = "Number";
                }
                catch { /* compare the type id text below */ }
                if (token.Length == 0)
                {
                    if (typeId.IndexOf(":length", StringComparison.OrdinalIgnoreCase) >= 0)
                        token = "Length";
                    else if (typeId.IndexOf(":number", StringComparison.OrdinalIgnoreCase) >= 0)
                        token = "Number";
                    else
                        token = "Other";
                }
            }

            var choice = EsFieldCatalog.Choose(isDouble, empty, token);
            if (choice == EsUnitChoice.NoUnit) return choice;

            if (choice == EsUnitChoice.Feet) unit = UnitTypeId.Feet;
            else if (choice == EsUnitChoice.General) unit = UnitTypeId.General;
            else unit = FirstValidUnit(spec);

            if (unit != null && !Compatible(field, unit))
            {
                var fallback = FirstValidUnit(spec);
                if (fallback != null && Compatible(field, fallback))
                {
                    unit = fallback;
                    choice = EsUnitChoice.FirstValid;
                }
                else
                {
                    unit = null;
                    choice = EsUnitChoice.NoUnit;
                }
            }
            return choice;
        }

        private static bool Compatible(Field field, ForgeTypeId unit)
        {
            if (field == null || unit == null) return false;
            try { return field.CompatibleUnit(unit); }
            catch { return false; }
        }

        private static ForgeTypeId FirstValidUnit(ForgeTypeId spec)
        {
            if (spec == null) return null;
            try
            {
                var units = UnitUtils.GetValidUnits(spec);
                if (units == null) return null;
                foreach (var unit in units)
                    if (unit != null) return unit;
            }
            catch { /* spec is not measurable */ }
            return null;
        }

        private static double MillimetresToUnit(double millimetres, EsUnitChoice choice, ForgeTypeId unit)
        {
            if (choice == EsUnitChoice.Feet || (unit != null && unit.Equals(UnitTypeId.Feet)))
                return EsFieldCatalog.ToStoredUnits(EsUnitChoice.Feet, millimetres);
            if (choice == EsUnitChoice.NoUnit || choice == EsUnitChoice.General || unit == null
                || unit.Equals(UnitTypeId.General))
                return millimetres;
            try { return UnitUtils.Convert(millimetres, UnitTypeId.Millimeters, unit); }
            catch { return millimetres; }
        }

        private static double UnitToMillimetres(double stored, EsUnitChoice choice, ForgeTypeId unit)
        {
            if (choice == EsUnitChoice.Feet || (unit != null && unit.Equals(UnitTypeId.Feet)))
                return EsFieldCatalog.FromStoredUnits(EsUnitChoice.Feet, stored);
            if (choice == EsUnitChoice.NoUnit || choice == EsUnitChoice.General || unit == null
                || unit.Equals(UnitTypeId.General))
                return stored;
            try { return UnitUtils.Convert(stored, unit, UnitTypeId.Millimeters); }
            catch { return stored; }
        }

        private static void SetValue(Entity entity, string fieldName, object value)
        {
            if (entity == null || !entity.IsValid()) return;
            LogSchemaOnce(entity.Schema);
            var field = FindField(entity.Schema, fieldName);
            if (field == null) return;
            try
            {
                var type = field.ValueType;
                if (type == typeof(double))
                {
                    var number = value is double d ? d : Convert.ToDouble(value);
                    WriteDouble(entity, field, number, asMillimetres: false);
                    return;
                }
                if (type == typeof(int))
                {
                    entity.Set(field, Convert.ToInt32(value));
                    return;
                }
                if (type == typeof(bool))
                {
                    entity.Set(field, Convert.ToBoolean(value));
                    return;
                }
                if (type == typeof(string))
                {
                    entity.Set(field, value as string ?? string.Empty);
                    return;
                }
                if (value is IList<string> strings)
                {
                    entity.Set<IList<string>>(field, strings);
                    return;
                }
                LogFieldFailure(entity.Schema, fieldName,
                    new InvalidOperationException("skipped unsupported type " + (type == null ? "?" : type.FullName)));
            }
            catch (Exception ex)
            {
                LogFieldFailure(entity.Schema, field.FieldName, ex);
            }
        }

        private static bool WriteDouble(Entity entity, Field field, double amount, bool asMillimetres)
        {
            var choice = Classify(field, out var unit);
            var stored = asMillimetres ? MillimetresToUnit(amount, choice, unit) : amount;
            try
            {
                if (choice == EsUnitChoice.NoUnit || unit == null)
                    entity.Set(field, stored);
                else
                    entity.Set(field, stored, unit);
                return true;
            }
            catch (Exception ex)
            {
                LogFieldFailure(entity.Schema, field.FieldName, ex);
                return false;
            }
        }

        private static bool TryReadMillimetres(Entity entity, string fieldName, out double millimetres)
        {
            millimetres = 0;
            var field = FindField(entity.Schema, fieldName);
            if (field == null) return false;
            try
            {
                if (field.ValueType != typeof(double)) return false;
                var choice = Classify(field, out var unit);
                double stored;
                if (choice == EsUnitChoice.NoUnit || unit == null)
                    stored = entity.Get<double>(field);
                else
                    stored = entity.Get<double>(field, unit);
                millimetres = UnitToMillimetres(stored, choice, unit);
                return true;
            }
            catch (Exception ex)
            {
                LogFieldFailure(entity.Schema, fieldName, ex);
                return false;
            }
        }

        private static bool TryWriteMillimetres(Entity entity, string fieldName, double millimetres)
        {
            LogSchemaOnce(entity.Schema);
            var field = FindField(entity.Schema, fieldName);
            if (field == null) return false;
            return WriteDouble(entity, field, millimetres, asMillimetres: true);
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
            WriteMillimetreEntity(floor, FloorLiftNumberSchema, FLiftMm, mm);
            WriteMillimetreEntity(floor, FloorLiftSchema, FLiftMm, mm);
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
            if (ds == null || data == null) return;
            try
            {
                var e = new Entity(DirectShapeSchema);
                SetValue(e, FVersion, FpfOptions.SchemaDataVersion);
                SetValue(e, FSource, data.SourceFloorUniqueId ?? string.Empty);
                SetValue(e, FChunk, data.ChunkIndex);
                ds.SetEntity(e);
            }
            catch (Exception ex)
            {
                LogFieldFailure(DirectShapeSchema, FSource, ex);
            }
            WriteMillimetreEntity(ds, DirectShapeExtraNumberSchema, FExtraLiftMm, data.ExtraLiftMm);
            WriteMillimetreEntity(ds, DirectShapeExtraSchema, FExtraLiftMm, data.ExtraLiftMm);
        }

        /// <summary>
        /// Writes one millimetre field onto a schema that may already exist with a different spec.
        /// A missing field is skipped. A unit mismatch is logged once and does not fail the chunk.
        /// </summary>
        private static void WriteMillimetreEntity(Element element, Schema schema, string fieldName, double millimetres)
        {
            if (element == null || schema == null || !schema.IsValidObject) return;
            try
            {
                var entity = new Entity(schema);
                SetValue(entity, FVersion, FpfOptions.SchemaDataVersion);
                if (!TryWriteMillimetres(entity, fieldName, millimetres)) return;
                element.SetEntity(entity);
            }
            catch (Exception ex)
            {
                LogFieldFailure(schema, fieldName, ex);
            }
        }

        private static double? ReadMillimetresFrom(Element element, Guid schemaGuid, string fieldName)
        {
            try
            {
                var schema = Schema.Lookup(schemaGuid);
                if (schema == null) return null;
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                if (!TryReadMillimetres(entity, fieldName, out var mm)) return null;
                return mm;
            }
            catch
            {
                return null;
            }
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
