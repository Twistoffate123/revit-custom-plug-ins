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
            if (!doc.IsModifiable)
            {
                FpfRefreshLog.LogUpdater("binding skipped reason=no-transaction");
                return;
            }

            var fpfPath = FpfSharedParametersPath();
            if (!File.Exists(fpfPath))
            {
                FpfRefreshLog.LogUpdater("binding skipped reason=missing-file path=" + fpfPath);
                return;
            }

            var fileProblem = SharedParameterFileCheck.Problem(File.ReadAllText(fpfPath));
            if (fileProblem != null)
            {
                FpfRefreshLog.LogUpdater("binding failed shared-parameter file: " + fileProblem + " path=" + fpfPath);
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
                if (defFile == null)
                {
                    FpfRefreshLog.LogUpdater("binding skipped reason=shared-file-unreadable");
                    return;
                }

                var grp = defFile.Groups.get_Item("FPF") ?? defFile.Groups.FirstOrDefault();
                if (grp == null)
                {
                    FpfRefreshLog.LogUpdater("binding skipped reason=no-FPF-group");
                    return;
                }

                _liftDef = grp.Definitions.get_Item(LiftName);
                _extraLiftDef = grp.Definitions.get_Item(ExtraLiftName);
                if (_liftDef == null || _extraLiftDef == null)
                {
                    FpfRefreshLog.LogUpdater("binding skipped reason=definition-missing");
                    return;
                }

                // The project already has FPF Lift bound to Generic Models only. Insert returns false.
                // ReInsert the existing definition with both categories.
                var liftDef = FindBoundDefinition(doc, LiftName) ?? _liftDef;
                var extraDef = FindBoundDefinition(doc, ExtraLiftName) ?? _extraLiftDef;
                var liftOk = Rebind(doc, liftDef, true,
                    BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_IOSModelGroups);
                var extraOk = Rebind(doc, extraDef, false, BuiltInCategory.OST_GenericModel);
                FpfRefreshLog.LogUpdater("binding lift=" + DescribeBinding(doc, LiftName) + " ok=" + liftOk +
                                         " extra=" + DescribeBinding(doc, ExtraLiftName) + " ok=" + extraOk);
            }
            catch (Exception ex)
            {
                FpfRefreshLog.LogUpdater("binding failed " + ex.Message);
            }
            finally
            {
                if (swapped)
                {
                    try { app.SharedParametersFilename = originalPath; }
                    catch { /* ignore */ }
                }
            }
        }

        private static Definition FindBoundDefinition(Document doc, string name)
        {
            try
            {
                var it = doc.ParameterBindings.ForwardIterator();
                it.Reset();
                while (it.MoveNext())
                {
                    if (it.Key is Definition d &&
                        string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string DescribeBinding(Document doc, string name)
        {
            try
            {
                var def = FindBoundDefinition(doc, name);
                if (def == null || !(doc.ParameterBindings.get_Item(def) is InstanceBinding b))
                    return name + ":unbound";
                var names = new System.Collections.Generic.List<string>();
                foreach (Category c in b.Categories)
                    if (c != null) names.Add(c.Name);
                return name + ":" + (names.Count == 0 ? "none" : string.Join("+", names));
            }
            catch (Exception ex)
            {
                return name + ":err " + ex.Message;
            }
        }

        /// <summary>Returns whether the binding call reported success.</summary>
        private static bool Rebind(Document doc, Definition def, bool allowReinsert, params BuiltInCategory[] cats)
        {
            var map = doc.ParameterBindings;
            var set = doc.Application.Create.NewCategorySet();
            var already = map.Contains(def);
            if (already && map.get_Item(def) is InstanceBinding existing)
            {
                foreach (Category c in existing.Categories)
                    if (c != null) set.Insert(c);
            }

            foreach (var cat in cats)
            {
                var catObj = Category.GetCategory(doc, cat);
                if (catObj != null) set.Insert(catObj);
            }

            if (set.Size == 0) return false;
            var binding = doc.Application.Create.NewInstanceBinding(set);
#if REVIT_2025 || REVIT_2026 || REVIT_2027
            var group = GroupTypeId.Constraints;
#else
            var group = BuiltInParameterGroup.PG_CONSTRAINTS;
#endif
            if (already)
            {
                if (!allowReinsert && CategoryCovers(set, cats)) return true;
                return map.ReInsert(def, binding, group);
            }

            if (map.Insert(def, binding, group)) return true;
            // Insert is false when a same-named parameter is already bound under another definition.
            var again = FindBoundDefinition(doc, def.Name);
            if (again == null) return false;
            return map.ReInsert(again, binding, group);
        }

        private static bool CategoryCovers(CategorySet set, BuiltInCategory[] cats)
        {
            foreach (var cat in cats)
            {
                var found = false;
                foreach (Category c in set)
                {
                    if (c != null && c.Id.IntegerValue == (int)cat) { found = true; break; }
                }
                if (!found) return false;
            }

            return true;
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