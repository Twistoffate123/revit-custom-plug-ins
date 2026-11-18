using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace FloorPatternFlattener.Services
{
    /// <summary>Shared/project parameters FPF Lift (mm) and FPF Extra Lift (mm).</summary>
    internal static class FpfProjectParameters
    {
        public static readonly Guid LiftParamGuid = new Guid("8f3c1a2b-6d4e-5f70-9a81-b2c3d4e5f607");
        public static readonly Guid ExtraLiftParamGuid = new Guid("9a4d2b3c-7e5f-6071-ab92-c3d4e5f60718");

        public const string LiftName = "FPF Lift (mm)";
        public const string ExtraLiftName = "FPF Extra Lift (mm)";

        private static Definition _liftDef;
        private static Definition _extraLiftDef;
        private static bool _bindAttempted;

        /// <summary>Does not change Revit's shared-parameter file path (see <see cref="EnsureBound"/>).</summary>
        public static void ConfigureSharedParameterFile(ControlledApplication app)
        {
            // Intentionally empty: binding temporarily swaps Application.SharedParametersFilename in EnsureBound.
        }

        private static string FpfSharedParametersPath() =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "FpfSharedParameters.txt");

        public static void EnsureBound(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument) return;
            if (_bindAttempted) return;

            var fpfPath = FpfSharedParametersPath();
            if (!File.Exists(fpfPath))
            {
                _bindAttempted = true;
                return;
            }

            var app = doc.Application;
            var originalPath = app.SharedParametersFilename;
            var swapped = false;
            try
            {
                if (!string.Equals(originalPath ?? string.Empty, fpfPath, StringComparison.OrdinalIgnoreCase))
                {
                    app.SharedParametersFilename = fpfPath;
                    swapped = true;
                }

                var defFile = app.OpenSharedParameterFile();
                if (defFile == null) return;
                var grp = defFile.Groups.get_Item("FPF") ?? defFile.Groups.FirstOrDefault();
                if (grp == null) return;
                _liftDef = grp.Definitions.get_Item(LiftName);
                _extraLiftDef = grp.Definitions.get_Item(ExtraLiftName);
                if (_liftDef == null || _extraLiftDef == null) return;

                var map = doc.ParameterBindings;
                if (!map.Contains(_liftDef)) BindInstance(doc, _liftDef, BuiltInCategory.OST_GenericModel);
                if (!map.Contains(_extraLiftDef)) BindInstance(doc, _extraLiftDef, BuiltInCategory.OST_GenericModel);
                if (!map.Contains(_liftDef))
                {
                    BindInstance(doc, _liftDef, BuiltInCategory.OST_IOSModelGroups);
                    if (!map.Contains(_extraLiftDef))
                        BindInstance(doc, _extraLiftDef, BuiltInCategory.OST_IOSModelGroups);
                }
            }
            catch
            {
                // parameters remain in extensible storage only
            }
            finally
            {
                _bindAttempted = true;
                if (swapped)
                {
                    try { app.SharedParametersFilename = originalPath; }
                    catch { /* ignore */ }
                }
            }
        }

        private static void BindInstance(Document doc, Definition def, BuiltInCategory cat)
        {
            try
            {
                var catObj = Category.GetCategory(doc, cat);
                if (catObj == null) return;
                var set = doc.Application.Create.NewCategorySet();
                set.Insert(catObj);
                var binding = doc.Application.Create.NewInstanceBinding(set);
#if REVIT_2025 || REVIT_2026 || REVIT_2027
                doc.ParameterBindings.Insert(def, binding, GroupTypeId.Constraints);
#else
                doc.ParameterBindings.Insert(def, binding, BuiltInParameterGroup.PG_CONSTRAINTS);
#endif
            }
            catch
            {
                // already bound or not allowed
            }
        }

        public static double? ReadLiftMm(Element e)
        {
            var p = FindParam(e, _liftDef, LiftParamGuid, LiftName);
            return ReadMm(p);
        }

        public static double? ReadExtraLiftMm(Element e)
        {
            var p = FindParam(e, _extraLiftDef, ExtraLiftParamGuid, ExtraLiftName);
            return ReadMm(p);
        }

        public static bool WriteLiftMm(Element e, double mm)
        {
            var p = FindParam(e, _liftDef, LiftParamGuid, LiftName);
            return WriteMm(p, mm);
        }

        public static bool WriteExtraLiftMm(Element e, double mm)
        {
            var p = FindParam(e, _extraLiftDef, ExtraLiftParamGuid, ExtraLiftName);
            return WriteMm(p, mm);
        }

        private static Parameter FindParam(Element e, Definition def, Guid guid, string name)
        {
            if (e == null) return null;
            try
            {
                if (def != null)
                {
                    var p = e.get_Parameter(def);
                    if (p != null) return p;
                }
                var guidParam = e.get_Parameter(guid);
                if (guidParam != null) return guidParam;
                return e.LookupParameter(name);
            }
            catch
            {
                return null;
            }
        }

        private static double? ReadMm(Parameter p)
        {
            if (p == null || p.StorageType != StorageType.Double) return null;
            try
            {
                var feet = p.AsDouble();
                return feet * 304.8;
            }
            catch
            {
                return null;
            }
        }

        private static bool WriteMm(Parameter p, double mm)
        {
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return false;
            try
            {
                p.Set(mm / 304.8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static double MmToFeet(double mm) => mm / 304.8;
    }
}