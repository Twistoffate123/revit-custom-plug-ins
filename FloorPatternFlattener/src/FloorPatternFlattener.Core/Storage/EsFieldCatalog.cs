using System;
using System.Collections.Generic;

namespace FloorPatternFlattener.Storage
{
    /// <summary>
    /// Which <c>Entity.Set</c> / <c>Get</c> overload a schema field uses.
    /// Length is stored in Revit internal feet and must be passed with <c>UnitTypeId.Feet</c>.
    /// Number, and every non-numeric field, uses the overload that takes no unit.
    /// Millimetres are never passed as a unit: a length field rejects them.
    /// </summary>
    public enum EsSetOverload
    {
        /// <summary>Do not call Set/Get. The live spec is not one we can write without throwing.</summary>
        Skip = 0,
        /// <summary>Set/Get without a <c>ForgeTypeId</c>.</summary>
        NoUnit = 1,
        /// <summary>Set/Get of a length, value in feet, unit <c>UnitTypeId.Feet</c>.</summary>
        LengthFeet = 2
    }

    public sealed class EsField
    {
        public EsField(string schemaName, string schemaGuid, string fieldName, string builtAs)
        {
            SchemaName = schemaName;
            SchemaGuid = schemaGuid;
            FieldName = fieldName;
            BuiltAs = builtAs;
        }

        public string SchemaName { get; }
        public string SchemaGuid { get; }
        public string FieldName { get; }
        /// <summary>Spec the builder uses when this GUID is first finished: Int, Bool, String, StringList, Number, Length.</summary>
        public string BuiltAs { get; }
        public EsSetOverload Overload => EsFieldCatalog.OverloadFor(BuiltAs);
    }

    /// <summary>Every extensible-storage field this add-in finishes, and the Set overload it requires.</summary>
    public static class EsFieldCatalog
    {
        public const double MillimetresPerFoot = 304.8;

        public static readonly IReadOnlyList<EsField> Fields = new EsField[]
        {
            F("FPF_FloorLink", "6F0B8E1A-3C57-4D8E-9A41-2B7C5D90E1F3", "Version", "Int"),
            F("FPF_FloorLink", "6F0B8E1A-3C57-4D8E-9A41-2B7C5D90E1F3", "Flattened", "Bool"),
            F("FPF_FloorLink", "6F0B8E1A-3C57-4D8E-9A41-2B7C5D90E1F3", "DirectShapeUniqueIds", "StringList"),
            F("FPF_FloorLink", "6F0B8E1A-3C57-4D8E-9A41-2B7C5D90E1F3", "Signature", "String"),
            F("FPF_FloorLink", "6F0B8E1A-3C57-4D8E-9A41-2B7C5D90E1F3", "DependencyUniqueIds", "StringList"),

            F("FPF_FlatPatternShape", "C4A2D9E7-81B3-4F6A-B05C-9E3D7A21F468", "Version", "Int"),
            F("FPF_FlatPatternShape", "C4A2D9E7-81B3-4F6A-B05C-9E3D7A21F468", "SourceFloorUniqueId", "String"),
            F("FPF_FlatPatternShape", "C4A2D9E7-81B3-4F6A-B05C-9E3D7A21F468", "ChunkIndex", "Int"),

            F("FPF_FloorStyle", "A7D35E92-4B1C-4E6F-8D20-5F9C3B71E0A4", "Version", "Int"),
            F("FPF_FloorStyle", "A7D35E92-4B1C-4E6F-8D20-5F9C3B71E0A4", "Style", "String"),

            F("FPF_FloorPlacement", "D2B6F1C8-5E3A-4A97-9C04-71E8B3A6D5F2", "Version", "Int"),
            F("FPF_FloorPlacement", "D2B6F1C8-5E3A-4A97-9C04-71E8B3A6D5F2", "Placement", "String"),

            F("FPF_FloorLift", "B3E8C5A1-7F2D-4E96-8C10-5A9D3E7F1B42", "Version", "Int"),
            F("FPF_FloorLift", "B3E8C5A1-7F2D-4E96-8C10-5A9D3E7F1B42", "LiftMm", "Number"),

            F("FPF_ShapeExtraLift", "C5D9E8F2-A4B6-4C7D-9E0F-1A2B3C4D5E6F", "Version", "Int"),
            F("FPF_ShapeExtraLift", "C5D9E8F2-A4B6-4C7D-9E0F-1A2B3C4D5E6F", "ExtraLiftMm", "Number"),

            F("FPF_ShapeExtraLiftNumber", "D6E0F1A3-B5C7-4D8E-8F10-2B3C4D5E6F70", "Version", "Int"),
            F("FPF_ShapeExtraLiftNumber", "D6E0F1A3-B5C7-4D8E-8F10-2B3C4D5E6F70", "ExtraLiftMm", "Number"),

            F("FPF_FloorLiftNumber", "C4F9D6B2-803E-4FA7-9D21-6BAE4F802C53", "Version", "Int"),
            F("FPF_FloorLiftNumber", "C4F9D6B2-803E-4FA7-9D21-6BAE4F802C53", "LiftMm", "Number"),
        };

        /// <summary>
        /// Live spec from <c>Field.GetSpecTypeId</c>, named the way the builder calls it:
        /// Length, Number, Int, Bool, String, StringList, ElementId, Guid.
        /// Empty or unrecognised measurable specs return <see cref="EsSetOverload.Skip"/>.
        /// </summary>
        public static EsSetOverload OverloadFor(string specToken)
        {
            if (string.IsNullOrEmpty(specToken)) return EsSetOverload.Skip;
            if (string.Equals(specToken, "Length", StringComparison.OrdinalIgnoreCase))
                return EsSetOverload.LengthFeet;
            if (string.Equals(specToken, "Number", StringComparison.OrdinalIgnoreCase)
                || string.Equals(specToken, "Int", StringComparison.OrdinalIgnoreCase)
                || string.Equals(specToken, "Bool", StringComparison.OrdinalIgnoreCase)
                || string.Equals(specToken, "String", StringComparison.OrdinalIgnoreCase)
                || string.Equals(specToken, "StringList", StringComparison.OrdinalIgnoreCase)
                || string.Equals(specToken, "ElementId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(specToken, "Guid", StringComparison.OrdinalIgnoreCase))
                return EsSetOverload.NoUnit;
            return EsSetOverload.Skip;
        }

        /// <summary>Value passed to Set. Length fields store internal feet. Number fields store the millimetre count.</summary>
        public static double ToStoredUnits(EsSetOverload overload, double millimetres)
        {
            if (overload == EsSetOverload.LengthFeet) return millimetres / MillimetresPerFoot;
            return millimetres;
        }

        public static double FromStoredUnits(EsSetOverload overload, double stored)
        {
            if (overload == EsSetOverload.LengthFeet) return stored * MillimetresPerFoot;
            return stored;
        }

        private static EsField F(string schema, string guid, string field, string builtAs) =>
            new EsField(schema, guid, field, builtAs);
    }
}
