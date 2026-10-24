using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace FloorPatternFlattener.Bridge
{
    internal static partial class CommandExecutor
    {
        public static BridgeResponse Execute(UIApplication app, ValidatedCommand cmd)
        {
            switch (cmd.Name)
            {
                case "ping": return Ping(app, cmd);
                case "get_model_info": return ModelInfo(app, cmd);
                case "query_elements": return Query(app, cmd);
                case "list_types": return ListTypes(app, cmd);
                case "get_parameters": return GetParameters(app, cmd);
                case "set_parameter": return SetParameter(app, cmd);
                case "select_elements": return Select(app, cmd);
                case "create_wall": return CreateWall(app, cmd);
                case "create_door": return CreateDoor(app, cmd);
                case "create_level": return CreateLevel(app, cmd);
                case "create_grid": return CreateGrid(app, cmd);
                case "create_room": return CreateRoom(app, cmd);
                case "create_room_tag": return CreateRoomTag(app, cmd);
                case "delete_elements": return Delete(app, cmd);
                case "flatten_patterns": return Flatten(app, cmd);
                default:
                    return RevitSupport.Fail(cmd, "unknown_command", "Unknown command '" + cmd.Name + "'.", null);
            }
        }

        private static BridgeResponse Ping(UIApplication app, ValidatedCommand cmd)
        {
            var doc = app == null || app.ActiveUIDocument == null ? null : app.ActiveUIDocument.Document;
            var message = doc == null
                ? "Revit is answering. No project model is open."
                : "Revit is answering. Open model: " + doc.Title + ".";
            var result = new JsonObj()
                .Set("message", message)
                .Set("revitYear", RevitYearInfo.Year)
                .Set("hasDocument", doc != null)
                .Build();
            return RevitSupport.Ok(cmd, result);
        }

        private static BridgeResponse ModelInfo(UIApplication app, ValidatedCommand cmd)
        {
            UIDocument uidoc;
            Document doc;
            BridgeResponse error;
            if (!RevitSupport.TryGetDocument(app, cmd, false, out uidoc, out doc, out error)) return error;

            var isCloud = false;
            try { isCloud = doc.IsModelInCloud; } catch { isCloud = false; }

            var userPath = "";
            var projectGuid = "";
            var modelGuid = "";
            var region = "";
            var urn = "";
            var cloudWarning = "";
            try
            {
                if (isCloud)
                {
                    var mp = doc.GetCloudModelPath();
                    if (mp != null)
                    {
                        userPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) ?? "";
                        projectGuid = mp.GetProjectGUID().ToString();
                        modelGuid = mp.GetModelGUID().ToString();
                        region = mp.Region ?? "";
                    }
                }
                else if (doc.IsWorkshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null)
                        userPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central) ?? "";
                }
                else userPath = doc.PathName ?? "";
            }
            catch (Exception ex) { cloudWarning = ex.Message; }

            if (isCloud)
            {
                try { urn = doc.GetCloudModelUrn() ?? ""; }
                catch (Exception ex)
                {
                    if (cloudWarning.Length == 0) cloudWarning = ex.Message;
                }
            }

            var view = uidoc.ActiveView;
            var viewObj = new JsonObj()
                .Set("id", view == null ? -1 : ElementIdUtil.GetValue(view.Id))
                .Set("name", view == null ? "" : view.Name)
                .Set("viewType", view == null ? "" : view.ViewType.ToString());
            if (view is ViewPlan plan && plan.GenLevel != null)
                viewObj.Set("level", plan.GenLevel.Name);

            var levels = new JsonArr();
            var levelCount = 0;
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                var l = (Level)level;
                levelCount++;
                if (levelCount > 200) continue;
                double projectElevation = l.Elevation;
                try
                {
                    var unitId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                    projectElevation = UnitUtils.ConvertFromInternalUnits(l.Elevation, unitId);
                }
                catch { /* keep internal feet */ }
                levels.Add(new JsonObj()
                    .Set("id", ElementIdUtil.GetValue(l.Id))
                    .Set("name", l.Name)
                    .Set("elevation", projectElevation)
                    .Build());
            }

            var selected = new JsonArr();
            var selectedCount = 0;
            var sel = uidoc.Selection == null ? null : uidoc.Selection.GetElementIds();
            if (sel != null)
            {
                foreach (var id in sel)
                {
                    selectedCount++;
                    if (selectedCount <= 100) selected.Add(JsonValue.FromLong(ElementIdUtil.GetValue(id)));
                }
            }

            var unit = RevitLengths.ProjectUnitLabel(doc);
            var kind = !doc.IsWorkshared
                ? "a local model (not workshared)"
                : isCloud ? "a cloud workshared model (ACC / BIM 360)" : "a workshared model";
            var message = "Open model: " + doc.Title + ". It is " + kind +
                ". Active view: " + (view == null ? "(none)" : view.Name) +
                ". Levels: " + levelCount +
                ". Selected elements: " + selectedCount +
                ". Length unit: " + unit +
                ". Sync and save stay manual.";
            if (cloudWarning.Length > 0)
                message += " Cloud details could not all be read: " + cloudWarning;

            var result = new JsonObj()
                .Set("message", message)
                .Set("title", doc.Title ?? "")
                .Set("path", doc.PathName ?? "")
                .Set("userVisiblePath", userPath)
                .Set("isWorkshared", doc.IsWorkshared)
                .Set("isCloud", isCloud)
                .Set("isDetached", SafeDetached(doc))
                .Set("isModified", doc.IsModified)
                .Set("isReadOnly", doc.IsReadOnly)
                .Set("projectGuid", projectGuid)
                .Set("modelGuid", modelGuid)
                .Set("region", region)
                .Set("cloudUrn", urn)
                .Set("cloudWarning", cloudWarning)
                .Set("revitUsername", app.Application == null ? "" : (app.Application.Username ?? ""))
                .Set("revitYear", RevitYearInfo.Year)
                .Set("lengthUnit", unit)
                .Set("activeView", viewObj.Build())
                .Set("levels", levels.Build())
                .Set("levelsTruncated", levelCount > 200)
                .Set("selectedCount", selectedCount)
                .Set("selectedIds", selected.Build())
                .Build();
            return RevitSupport.Ok(cmd, result);
        }

        private static bool SafeDetached(Document doc)
        {
            try { return doc.IsDetached; }
            catch { return false; }
        }

        private static BridgeResponse Query(UIApplication app, ValidatedCommand cmd)
        {
            UIDocument uidoc;
            Document doc;
            BridgeResponse error;
            if (!RevitSupport.TryGetDocument(app, cmd, false, out uidoc, out doc, out error)) return error;

            BuiltInCategory bic;
            string catError;
            if (!TryCategory(doc, Arg.Str(cmd.Args, "category"), out bic, out catError))
                return RevitSupport.Fail(cmd, "invalid_args", catError, null);

            Level level = null;
            var levelName = Arg.Str(cmd.Args, "level");
            if (!string.IsNullOrEmpty(levelName))
            {
                level = FindLevel(doc, levelName);
                if (level == null)
                    return RevitSupport.Fail(cmd, "not_found", "There is no level named '" + levelName + "'.", null);
            }

            var limit = (int)Arg.Long(cmd.Args, "limit");
            JsonValue equals = null;
            var filterParam = Arg.Str(cmd.Args, "parameter");
            if (!string.IsNullOrEmpty(filterParam)) cmd.Args.TryGet("equals", out equals);

            var items = new JsonArr();
            var matched = 0;
            var truncated = false;
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                if (level != null && !OnLevel(e, level.Id)) continue;
                if (equals != null && !ParameterEquals(e, filterParam, equals)) continue;
                matched++;
                if (matched > limit) { truncated = true; break; }
                items.Add(ElementSummary(doc, e));
            }

            var returned = truncated ? limit : matched;
            var message = "Found " + returned + (truncated ? " (more exist, the list was cut off)" : "") +
                " " + Arg.Str(cmd.Args, "category") +
                (level == null ? "" : " on " + level.Name) +
                ". Editable tells you if worksharing will let you change that element. Nothing was changed.";
            var result = new JsonObj()
                .Set("message", message)
                .Set("count", returned)
                .Set("truncated", truncated)
                .Set("elements", items.Build())
                .Build();
            return RevitSupport.Ok(cmd, result);
        }

        private static BridgeResponse ListTypes(UIApplication app, ValidatedCommand cmd)
        {
            Document doc;
            BridgeResponse error;
            UIDocument uidoc;
            if (!RevitSupport.TryGetDocument(app, cmd, false, out uidoc, out doc, out error)) return error;

            BuiltInCategory bic;
            string catError;
            if (!TryCategory(doc, Arg.Str(cmd.Args, "category"), out bic, out catError))
                return RevitSupport.Fail(cmd, "invalid_args", catError, null);

            var limit = (int)Arg.Long(cmd.Args, "limit");
            var items = new JsonArr();
            var count = 0;
            var truncated = false;
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType())
            {
                var t = e as ElementType;
                if (t == null) continue;
                count++;
                if (count > limit) { truncated = true; break; }
                items.Add(new JsonObj()
                    .Set("id", ElementIdUtil.GetValue(t.Id))
                    .Set("name", t.Name ?? "")
                    .Set("family", t.FamilyName ?? "")
                    .Set("category", t.Category != null ? t.Category.Name : "")
                    .Build());
            }
            var returned = truncated ? limit : count;
            var result = new JsonObj()
                .Set("message", "Found " + returned + " type(s) for " + Arg.Str(cmd.Args, "category") + ". Nothing was changed.")
                .Set("count", returned)
                .Set("truncated", truncated)
                .Set("types", items.Build())
                .Build();
            return RevitSupport.Ok(cmd, result);
        }

        private static BridgeResponse GetParameters(UIApplication app, ValidatedCommand cmd)
        {
            Document doc;
            BridgeResponse error;
            UIDocument uidoc;
            if (!RevitSupport.TryGetDocument(app, cmd, false, out uidoc, out doc, out error)) return error;

            Element element;
            if (!TryElement(doc, cmd, Arg.Long(cmd.Args, "elementId"), out element, out error)) return error;

            var wanted = Arg.Strings(cmd.Args, "names");
            var items = new JsonArr();
            if (wanted.Count == 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var count = 0;
                var truncated = false;
                foreach (Parameter p in element.Parameters)
                {
                    if (p == null || p.Definition == null || string.IsNullOrEmpty(p.Definition.Name)) continue;
                    if (!seen.Add(p.Definition.Name)) continue;
                    count++;
                    if (count > 200) { truncated = true; break; }
                    items.Add(ParameterJson(doc, p));
                }
                var resultAll = new JsonObj()
                    .Set("message", "Read parameters on " + RevitSupport.Describe(element) + ". Nothing was changed.")
                    .Set("elementId", ElementIdUtil.GetValue(element.Id))
                    .Set("truncated", truncated)
                    .Set("parameters", items.Build())
                    .Build();
                return RevitSupport.Ok(cmd, resultAll);
            }

            var missing = new JsonArr();
            var missingCount = 0;
            for (var i = 0; i < wanted.Count; i++)
            {
                string findError;
                var p = FindParameter(doc, element, wanted[i], out findError);
                if (p == null)
                {
                    missing.Add(JsonValue.FromString(wanted[i]));
                    missingCount++;
                }
                else items.Add(ParameterJson(doc, p));
            }
            var message = "Read " + (wanted.Count - missingCount) + " parameter(s) on " + RevitSupport.Describe(element) + ".";
            if (missingCount > 0) message += " Some names were not found.";
            message += " Nothing was changed.";
            var result = new JsonObj()
                .Set("message", message)
                .Set("elementId", ElementIdUtil.GetValue(element.Id))
                .Set("parameters", items.Build())
                .Set("missing", missing.Build())
                .Build();
            return RevitSupport.Ok(cmd, result);
        }

        private static BridgeResponse Select(UIApplication app, ValidatedCommand cmd)
        {
            UIDocument uidoc;
            Document doc;
            BridgeResponse error;
            if (!RevitSupport.TryGetDocument(app, cmd, false, out uidoc, out doc, out error)) return error;

            List<Element> elements;
            if (!TryElements(doc, cmd, Arg.Longs(cmd.Args, "elementIds"), out elements, out error)) return error;

            var ids = new List<ElementId>();
            for (var i = 0; i < elements.Count; i++) ids.Add(elements[i].Id);
            uidoc.Selection.SetElementIds(ids);
            var zoomNote = "";
            if (Arg.Bool(cmd.Args, "zoom"))
            {
                try { uidoc.ShowElements(ids); }
                catch (Exception ex)
                {
                    zoomNote = " Revit could not zoom to them (" + ex.Message + "). They may be hidden in this view.";
                }
            }
            var result = new JsonObj()
                .Set("message", "Selected " + ids.Count + " element(s)." + zoomNote + " Nothing in the model was changed.")
                .Set("count", ids.Count)
                .Set("elementIds", IdArray(elements))
                .Build();
            return RevitSupport.Ok(cmd, result);
        }

        private static JsonValue ParameterJson(Document doc, Parameter p)
        {
            var isType = p.Element is ElementType;
            var obj = new JsonObj()
                .Set("name", p.Definition.Name)
                .Set("storageType", p.StorageType.ToString())
                .Set("isReadOnly", p.IsReadOnly)
                .Set("isType", isType);
            try
            {
                var display = p.AsValueString();
                if (display != null) obj.Set("displayValue", display);
            }
            catch { /* ignore */ }
            switch (p.StorageType)
            {
                case StorageType.String:
                    obj.Set("value", p.AsString() ?? "");
                    break;
                case StorageType.Integer:
                    obj.Set("value", p.AsInteger());
                    break;
                case StorageType.Double:
                    obj.Set("internalValue", p.AsDouble());
                    try
                    {
                        var spec = p.Definition.GetDataType();
                        if (UnitUtils.IsMeasurableSpec(spec))
                        {
                            var unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                            obj.Set("projectValue", UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit));
                            obj.Set("projectUnit", RevitLengths.Label(unit));
                        }
                    }
                    catch { /* leave internal only */ }
                    break;
                case StorageType.ElementId:
                    var eid = p.AsElementId();
                    obj.Set("value", eid == null ? -1 : ElementIdUtil.GetValue(eid));
                    break;
            }
            return obj.Build();
        }

        private static bool ParameterEquals(Element e, string name, JsonValue equals)
        {
            var p = e.LookupParameter(name);
            if (p == null) return false;
            string needle;
            if (equals.Kind == JsonKind.Number) needle = equals.NumberText;
            else if (equals.Kind == JsonKind.Bool) needle = equals.BoolValue ? "Yes" : "No";
            else needle = equals.StringValue ?? "";
            try
            {
                if (string.Equals(p.AsValueString(), needle, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* ignore */ }
            if (p.StorageType == StorageType.String &&
                string.Equals(p.AsString(), needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (equals.Kind == JsonKind.Bool && p.StorageType == StorageType.Integer)
                return (p.AsInteger() != 0) == equals.BoolValue;
            if (equals.Kind == JsonKind.Number && equals.IsInteger && p.StorageType == StorageType.Integer)
                return p.AsInteger() == (int)equals.AsLong();
            if (equals.Kind == JsonKind.String && p.StorageType == StorageType.Integer)
            {
                if (string.Equals(needle, "yes", StringComparison.OrdinalIgnoreCase)) return p.AsInteger() != 0;
                if (string.Equals(needle, "no", StringComparison.OrdinalIgnoreCase)) return p.AsInteger() == 0;
            }
            return false;
        }

        private static JsonValue ElementSummary(Document doc, Element e)
        {
            var own = RevitSupport.Inspect(doc, e);
            var levelName = "";
            try
            {
                if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
                {
                    var level = doc.GetElement(e.LevelId) as Level;
                    if (level != null) levelName = level.Name;
                }
            }
            catch { /* some elements have no level */ }
            return new JsonObj()
                .Set("id", own.Id)
                .Set("uniqueId", e.UniqueId ?? "")
                .Set("name", own.Name ?? "")
                .Set("category", own.Category ?? "")
                .Set("level", levelName)
                .Set("editable", own.Editable)
                .Set("owner", own.Owner ?? "")
                .Set("reason", own.Reason ?? "")
                .Set("pinned", own.Pinned)
                .Set("inGroup", own.InGroup)
                .Build();
        }
    }

    internal static class Arg
    {
        public static string Str(JsonValue args, string key)
        {
            JsonValue value;
            if (!args.TryGet(key, out value) || value.Kind != JsonKind.String) return null;
            return value.StringValue;
        }

        public static bool Has(JsonValue args, string key)
        {
            JsonValue value;
            return args.TryGet(key, out value);
        }

        public static bool Bool(JsonValue args, string key)
        {
            JsonValue value;
            return args.TryGet(key, out value) && value.Kind == JsonKind.Bool && value.BoolValue;
        }

        public static long Long(JsonValue args, string key)
        {
            JsonValue value;
            if (!args.TryGet(key, out value)) return 0;
            return value.AsLong();
        }

        public static double Num(JsonValue args, string key)
        {
            JsonValue value;
            if (!args.TryGet(key, out value)) return 0;
            return value.AsDouble();
        }

        public static List<long> Longs(JsonValue args, string key)
        {
            var list = new List<long>();
            JsonValue value;
            if (!args.TryGet(key, out value) || value.Array == null) return list;
            for (var i = 0; i < value.Array.Count; i++) list.Add(value.Array[i].AsLong());
            return list;
        }

        public static List<string> Strings(JsonValue args, string key)
        {
            var list = new List<string>();
            JsonValue value;
            if (!args.TryGet(key, out value) || value.Array == null) return list;
            for (var i = 0; i < value.Array.Count; i++)
            {
                if (value.Array[i].Kind == JsonKind.String) list.Add(value.Array[i].StringValue);
            }
            return list;
        }

        public static bool Xy(JsonValue args, string key, out double x, out double y)
        {
            x = 0;
            y = 0;
            JsonValue value;
            if (!args.TryGet(key, out value) || value.Array == null || value.Array.Count < 2) return false;
            x = value.Array[0].AsDouble();
            y = value.Array[1].AsDouble();
            return true;
        }
    }
}
