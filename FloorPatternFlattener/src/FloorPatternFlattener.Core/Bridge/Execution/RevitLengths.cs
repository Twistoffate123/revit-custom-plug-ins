using System;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Bridge
{
    internal static class RevitLengths
    {
        public static bool TryToFeet(Document doc, double value, string units, out double feet, out string error)
        {
            var canonical = LengthUnits.Canonical(units);
            if (canonical == null)
            {
                feet = 0;
                error = "Unknown units '" + units + "'. Use mm, cm, m, ft, in, or project.";
                return false;
            }
            if (canonical == "project")
            {
                try
                {
                    var unitId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                    feet = UnitUtils.ConvertToInternalUnits(value, unitId);
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    feet = 0;
                    error = "Could not read the model's length units. " + ex.Message;
                    return false;
                }
            }
            return LengthUnits.TryToFeet(value, canonical, out feet, out error);
        }

        public static string ProjectUnitLabel(Document doc)
        {
            try
            {
                var unitId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                return Label(unitId);
            }
            catch
            {
                return "project";
            }
        }

        public static string Label(ForgeTypeId unitId)
        {
            if (unitId == null) return "";
            if (unitId == UnitTypeId.Millimeters) return "mm";
            if (unitId == UnitTypeId.Centimeters) return "cm";
            if (unitId == UnitTypeId.Meters) return "m";
            if (unitId == UnitTypeId.Feet) return "ft";
            if (unitId == UnitTypeId.Inches) return "in";
            if (unitId == UnitTypeId.FeetFractionalInches) return "ft";
            try { return unitId.TypeId; }
            catch { return ""; }
        }

        public static bool TryParameterToInternal(Document doc, Parameter param, double value, string units, out double internalValue, out string error)
        {
            internalValue = 0;
            error = null;
            var canonical = LengthUnits.Canonical(units);
            if (canonical == null)
            {
                error = "Unknown units '" + units + "'. Use mm, cm, m, ft, in, or project.";
                return false;
            }
            ForgeTypeId spec = null;
            try { spec = param.Definition.GetDataType(); }
            catch { spec = null; }
            var measurable = false;
            try { measurable = spec != null && UnitUtils.IsMeasurableSpec(spec); }
            catch { measurable = false; }

            if (!measurable)
            {
                if (canonical != "project")
                {
                    error = "That parameter is not a length, so units cannot be applied. Pass the number Revit stores, or pass the value as text.";
                    return false;
                }
                internalValue = value;
                return true;
            }

            try
            {
                if (canonical == "project")
                {
                    var unitId = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                    internalValue = UnitUtils.ConvertToInternalUnits(value, unitId);
                    return true;
                }
                if (spec != SpecTypeId.Length)
                {
                    error = "That parameter is not a length. Leave units as project, or pass the value as text the way it looks in Revit.";
                    return false;
                }
                double feet;
                if (!LengthUnits.TryToFeet(value, canonical, out feet, out error)) return false;
                internalValue = feet;
                return true;
            }
            catch (Exception ex)
            {
                error = "Revit could not convert that number. " + ex.Message;
                return false;
            }
        }
    }
}
