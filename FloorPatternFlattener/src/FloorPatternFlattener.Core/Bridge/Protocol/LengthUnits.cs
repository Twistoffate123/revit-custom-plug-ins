using System;

namespace FloorPatternFlattener.Bridge
{
    /// <summary>
    /// Length conversion that does not need the Revit API. Explicit units use the international foot
    /// (the same foot Revit uses internally). "project" is resolved by the add-in from the open model.
    /// </summary>
    internal static class LengthUnits
    {
        public static string Canonical(string units)
        {
            if (string.IsNullOrWhiteSpace(units)) return "project";
            switch (units.Trim().ToLowerInvariant())
            {
                case "project": return "project";
                case "mm":
                case "millimetre":
                case "millimetres":
                case "millimeter":
                case "millimeters":
                    return "mm";
                case "cm":
                case "centimetre":
                case "centimetres":
                case "centimeter":
                case "centimeters":
                    return "cm";
                case "m":
                case "metre":
                case "metres":
                case "meter":
                case "meters":
                    return "m";
                case "ft":
                case "foot":
                case "feet":
                    return "ft";
                case "in":
                case "inch":
                case "inches":
                    return "in";
                default:
                    return null;
            }
        }

        /// <summary>Feet per one display unit, for units other than "project".</summary>
        public static bool TryFeetPerUnit(string canonicalUnit, out double feetPerUnit)
        {
            switch (canonicalUnit)
            {
                case "mm": feetPerUnit = 1.0 / 304.8; return true;
                case "cm": feetPerUnit = 1.0 / 30.48; return true;
                case "m": feetPerUnit = 1.0 / 0.3048; return true;
                case "ft": feetPerUnit = 1.0; return true;
                case "in": feetPerUnit = 1.0 / 12.0; return true;
                default:
                    feetPerUnit = 0;
                    return false;
            }
        }

        public static bool TryToFeet(double value, string units, out double feet, out string error)
        {
            var canonical = Canonical(units);
            if (canonical == null)
            {
                feet = 0;
                error = "Unknown units '" + units + "'. Use mm, cm, m, ft, in, or project.";
                return false;
            }
            if (canonical == "project")
            {
                feet = 0;
                error = "Project units are resolved from the open model.";
                return false;
            }
            double factor;
            if (!TryFeetPerUnit(canonical, out factor))
            {
                feet = 0;
                error = "Cannot convert '" + units + "'.";
                return false;
            }
            feet = value * factor;
            error = null;
            return true;
        }
    }
}
