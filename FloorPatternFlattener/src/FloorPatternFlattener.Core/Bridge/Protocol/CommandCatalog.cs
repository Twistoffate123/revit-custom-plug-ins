using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace FloorPatternFlattener.Bridge
{
    internal sealed class CommandSpec
    {
        public string Name;
        public string McpName;
        public string Summary;
        public bool Mutates;
        public JsonValue InputSchema;
    }

    internal sealed class ForbiddenSpec
    {
        public List<string> Names = new List<string>();
        public string Message;
    }

    internal static class CommandCatalog
    {
        private static readonly object Gate = new object();
        private static bool _loaded;
        private static List<CommandSpec> _commands;
        private static List<ForbiddenSpec> _forbidden;
        private static Dictionary<string, CommandSpec> _byName;
        private static Dictionary<string, string> _forbiddenByName;

        public static IList<CommandSpec> Commands
        {
            get { Ensure(); return _commands; }
        }

        public static IList<ForbiddenSpec> Forbidden
        {
            get { Ensure(); return _forbidden; }
        }

        public static bool TryGet(string name, out CommandSpec spec)
        {
            Ensure();
            if (name == null)
            {
                spec = null;
                return false;
            }
            return _byName.TryGetValue(name, out spec);
        }

        public static bool TryForbidden(string name, out string message)
        {
            Ensure();
            message = null;
            if (name == null) return false;
            return _forbiddenByName.TryGetValue(name, out message);
        }

        public static void Ensure()
        {
            if (_loaded) return;
            lock (Gate)
            {
                if (_loaded) return;
                Load();
                _loaded = true;
            }
        }

        private static void Load()
        {
            var asm = typeof(CommandCatalog).Assembly;
            using (var stream = asm.GetManifestResourceStream(BridgeInfo.ResourceName))
            {
                if (stream == null)
                {
                    var names = string.Join(", ", asm.GetManifestResourceNames());
                    throw new InvalidOperationException(
                        "The command list is missing from this build (expected " + BridgeInfo.ResourceName + "). Found: " + names);
                }
                string text;
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                var root = MiniJson.Parse(text);
                _commands = new List<CommandSpec>();
                _forbidden = new List<ForbiddenSpec>();
                _byName = new Dictionary<string, CommandSpec>(StringComparer.Ordinal);
                _forbiddenByName = new Dictionary<string, string>(StringComparer.Ordinal);

                JsonValue forbidden;
                if (root.TryGet("forbidden", out forbidden) && forbidden.Kind == JsonKind.Array)
                {
                    foreach (var item in forbidden.Array)
                    {
                        var spec = new ForbiddenSpec
                        {
                            Message = StringProp(item, "message")
                        };
                        JsonValue names;
                        if (!item.TryGet("names", out names) || names.Kind != JsonKind.Array)
                            throw new InvalidOperationException("A forbidden command is missing names.");
                        foreach (var n in names.Array)
                        {
                            if (n.Kind != JsonKind.String || string.IsNullOrEmpty(n.StringValue))
                                throw new InvalidOperationException("Forbidden command names must be strings.");
                            spec.Names.Add(n.StringValue);
                            _forbiddenByName[n.StringValue] = spec.Message;
                        }
                        _forbidden.Add(spec);
                    }
                }

                JsonValue commands;
                if (!root.TryGet("commands", out commands) || commands.Kind != JsonKind.Array)
                    throw new InvalidOperationException("commands.json has no commands array.");
                foreach (var item in commands.Array)
                {
                    var spec = new CommandSpec
                    {
                        Name = StringProp(item, "name"),
                        McpName = StringProp(item, "mcpName"),
                        Summary = StringProp(item, "summary"),
                        Mutates = BoolProp(item, "mutates"),
                    };
                    JsonValue schema;
                    if (!item.TryGet("inputSchema", out schema) || schema.Kind != JsonKind.Object)
                        throw new InvalidOperationException("Command '" + spec.Name + "' has no inputSchema.");
                    spec.InputSchema = schema;
                    if (string.IsNullOrEmpty(spec.Name))
                        throw new InvalidOperationException("A command is missing its name.");
                    if (_byName.ContainsKey(spec.Name))
                        throw new InvalidOperationException("Duplicate command '" + spec.Name + "'.");
                    if (_forbiddenByName.ContainsKey(spec.Name))
                        throw new InvalidOperationException("Command '" + spec.Name + "' is also forbidden.");
                    _commands.Add(spec);
                    _byName.Add(spec.Name, spec);
                }
            }
        }

        private static string StringProp(JsonValue obj, string key)
        {
            JsonValue value;
            if (!obj.TryGet(key, out value) || value.Kind != JsonKind.String)
                return "";
            return value.StringValue ?? "";
        }

        private static bool BoolProp(JsonValue obj, string key)
        {
            JsonValue value;
            if (!obj.TryGet(key, out value) || value.Kind != JsonKind.Bool) return false;
            return value.BoolValue;
        }
    }

    internal static class CommandValidator
    {
        public static string Validate(JsonValue root, out ValidatedCommand command)
        {
            command = null;
            if (root == null || root.Kind != JsonKind.Object)
                return "The request must be a JSON object with a command name and args.";

            if (root.Object != null)
            {
                for (var i = 0; i < root.Object.Count; i++)
                {
                    var key = root.Object[i].Key;
                    if (key != "command" && key != "args" && key != "id")
                        return "Unknown field '" + key + "'. Use command, args, and an optional id.";
                }
            }

            JsonValue commandValue;
            if (!root.TryGet("command", out commandValue) || commandValue.Kind != JsonKind.String || string.IsNullOrWhiteSpace(commandValue.StringValue))
                return "The request needs a command name, for example get_model_info.";

            var name = commandValue.StringValue.Trim().ToLowerInvariant();
            string id = null;
            JsonValue idValue;
            if (root.TryGet("id", out idValue))
            {
                if (idValue.Kind != JsonKind.String)
                    return "id must be a string.";
                id = idValue.StringValue ?? "";
                if (id.Length > 80) return "id is too long.";
            }

            string forbidden;
            if (CommandCatalog.TryForbidden(name, out forbidden))
                return forbidden;

            CommandSpec spec;
            if (!CommandCatalog.TryGet(name, out spec))
                return "Unknown command '" + name + "'. Use one of the commands the helper lists, such as get_model_info or query_elements.";

            JsonValue args;
            if (!root.TryGet("args", out args))
                args = JsonValue.FromObject(new List<KeyValuePair<string, JsonValue>>());
            if (args.Kind == JsonKind.Null)
                args = JsonValue.FromObject(new List<KeyValuePair<string, JsonValue>>());
            if (args.Kind != JsonKind.Object)
                return "args must be a JSON object.";

            JsonValue normalized;
            var schemaError = SchemaCheck.Apply(spec.InputSchema, args, out normalized);
            if (schemaError != null) return schemaError;

            var cross = CrossField(name, normalized);
            if (cross != null) return cross;

            command = new ValidatedCommand
            {
                Id = id,
                Name = name,
                Args = normalized
            };
            return null;
        }

        private static string CrossField(string name, JsonValue args)
        {
            if (name == "query_elements")
            {
                var hasParam = args.TryGet("parameter", out _);
                var hasEquals = args.TryGet("equals", out _);
                if (hasParam != hasEquals)
                    return "To filter by a parameter, pass both parameter and equals.";
            }
            if (name == "create_wall")
            {
                JsonValue height;
                args.TryGet("height", out height);
                if (height.AsDouble() <= 0)
                    return "Height must be greater than zero.";
                if (PointsTooClose(args, "start", "end"))
                    return "The wall start and end are the same point. Pick two different points.";
            }
            if (name == "create_grid" && PointsTooClose(args, "start", "end"))
                return "The grid start and end are the same point. Pick two different points.";
            if (name == "flatten_patterns")
            {
                var action = StringArg(args, "action");
                var hasIds = HasNonEmptyIds(args);
                var useSelection = BoolArg(args, "useSelection");
                var all = BoolArg(args, "all");
                if (action == "flatten")
                {
                    if (all) return "For flatten, pass elementIds or useSelection. all is only for clear or refresh.";
                    if (!hasIds && !useSelection)
                        return "For flatten, pass elementIds of the floors, or useSelection true to use what is selected in Revit.";
                }
                else if (action == "clear")
                {
                    if (all && (hasIds || useSelection))
                        return "For clear, pass either all true, or elementIds / useSelection, not both.";
                    if (!all && !hasIds && !useSelection)
                        return "For clear, pass elementIds, useSelection true, or all true. all removes every flat pattern in the model.";
                }
                else if (action == "refresh")
                {
                    if (all && (hasIds || useSelection))
                        return "For refresh, pass either all (or nothing) to refresh every flattened floor, or elementIds / useSelection for specific floors.";
                }
            }
            return null;
        }

        private static bool PointsTooClose(JsonValue args, string a, string b)
        {
            JsonValue av, bv;
            if (!args.TryGet(a, out av) || !args.TryGet(b, out bv)) return false;
            if (av.Array == null || bv.Array == null) return false;
            var n = Math.Min(av.Array.Count, bv.Array.Count);
            if (n < 2) return false;
            for (var i = 0; i < n; i++)
            {
                if (Math.Abs(av.Array[i].AsDouble() - bv.Array[i].AsDouble()) > 1e-9) return false;
            }
            return true;
        }

        private static bool HasNonEmptyIds(JsonValue args)
        {
            JsonValue ids;
            return args.TryGet("elementIds", out ids) && ids.Kind == JsonKind.Array && ids.Array != null && ids.Array.Count > 0;
        }

        private static bool BoolArg(JsonValue args, string key)
        {
            JsonValue value;
            return args.TryGet(key, out value) && value.Kind == JsonKind.Bool && value.BoolValue;
        }

        private static string StringArg(JsonValue args, string key)
        {
            JsonValue value;
            if (!args.TryGet(key, out value) || value.Kind != JsonKind.String) return "";
            return value.StringValue ?? "";
        }
    }

    internal static class SchemaCheck
    {
        public static string Apply(JsonValue schema, JsonValue instance, out JsonValue normalized)
        {
            return Check(schema, instance, "$", out normalized);
        }

        private static string Check(JsonValue schema, JsonValue instance, string path, out JsonValue normalized)
        {
            normalized = instance;
            JsonValue typeValue;
            schema.TryGet("type", out typeValue);

            if (typeValue != null && typeValue.Kind == JsonKind.Array)
            {
                foreach (var option in typeValue.Array)
                {
                    var one = JsonValue.FromObject(CopyProps(schema, option));
                    JsonValue candidate;
                    var err = Check(one, instance, path, out candidate);
                    if (err == null)
                    {
                        normalized = candidate;
                        return null;
                    }
                }
                return path + " has a value of the wrong type.";
            }

            var type = typeValue != null && typeValue.Kind == JsonKind.String ? typeValue.StringValue : "object";
            switch (type)
            {
                case "object":
                    return CheckObject(schema, instance, path, out normalized);
                case "array":
                    return CheckArray(schema, instance, path, out normalized);
                case "string":
                    return CheckString(schema, instance, path, out normalized);
                case "boolean":
                    if (instance.Kind != JsonKind.Bool)
                        return path + " must be true or false.";
                    normalized = instance;
                    return null;
                case "integer":
                    if (!instance.IsInteger)
                        return path + " must be a whole number.";
                    return CheckRange(schema, instance.AsDouble(), path, out normalized, instance);
                case "number":
                    if (instance.Kind != JsonKind.Number)
                        return path + " must be a number.";
                    return CheckRange(schema, instance.AsDouble(), path, out normalized, instance);
                default:
                    return "Unsupported schema type '" + type + "'.";
            }
        }

        private static List<KeyValuePair<string, JsonValue>> CopyProps(JsonValue schema, JsonValue typeOverride)
        {
            var props = new List<KeyValuePair<string, JsonValue>>();
            if (schema.Object != null)
            {
                for (var i = 0; i < schema.Object.Count; i++)
                {
                    if (schema.Object[i].Key == "type") continue;
                    props.Add(schema.Object[i]);
                }
            }
            props.Add(new KeyValuePair<string, JsonValue>("type", typeOverride));
            return props;
        }

        private static string CheckObject(JsonValue schema, JsonValue instance, string path, out JsonValue normalized)
        {
            normalized = null;
            if (instance.Kind != JsonKind.Object)
                return path + " must be an object.";

            JsonValue properties;
            schema.TryGet("properties", out properties);
            var additional = true;
            JsonValue add;
            if (schema.TryGet("additionalProperties", out add) && add.Kind == JsonKind.Bool)
                additional = add.BoolValue;

            if (!additional && instance.Object != null && properties != null)
            {
                for (var i = 0; i < instance.Object.Count; i++)
                {
                    JsonValue ignored;
                    if (!properties.TryGet(instance.Object[i].Key, out ignored))
                        return "Unknown field '" + instance.Object[i].Key + "'.";
                }
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            JsonValue req;
            if (schema.TryGet("required", out req) && req.Kind == JsonKind.Array)
            {
                foreach (var item in req.Array)
                {
                    if (item.Kind == JsonKind.String) required.Add(item.StringValue);
                }
            }

            var output = new List<KeyValuePair<string, JsonValue>>();
            if (properties != null && properties.Object != null)
            {
                for (var i = 0; i < properties.Object.Count; i++)
                {
                    var key = properties.Object[i].Key;
                    var propSchema = properties.Object[i].Value;
                    var childPath = path == "$" ? key : path + "." + key;
                    JsonValue present;
                    if (instance.TryGet(key, out present))
                    {
                        JsonValue child;
                        var err = Check(propSchema, present, childPath, out child);
                        if (err != null) return err;
                        output.Add(new KeyValuePair<string, JsonValue>(key, child));
                    }
                    else
                    {
                        JsonValue def;
                        if (propSchema.TryGet("default", out def))
                        {
                            JsonValue child;
                            var err = Check(propSchema, def, childPath, out child);
                            if (err != null) return "Default for " + childPath + " is invalid: " + err;
                            output.Add(new KeyValuePair<string, JsonValue>(key, child));
                        }
                        else if (required.Contains(key))
                        {
                            return "Missing " + childPath + ".";
                        }
                    }
                }
            }

            if (additional && instance.Object != null)
            {
                for (var i = 0; i < instance.Object.Count; i++)
                {
                    var key = instance.Object[i].Key;
                    var known = false;
                    for (var j = 0; j < output.Count; j++)
                    {
                        if (output[j].Key == key) { known = true; break; }
                    }
                    if (!known) output.Add(instance.Object[i]);
                }
            }

            normalized = JsonValue.FromObject(output);
            return null;
        }

        private static string CheckArray(JsonValue schema, JsonValue instance, string path, out JsonValue normalized)
        {
            normalized = null;
            if (instance.Kind != JsonKind.Array || instance.Array == null)
                return path + " must be a list.";
            int minItems, maxItems;
            if (TryInt(schema, "minItems", out minItems) && instance.Array.Count < minItems)
                return path + " needs at least " + minItems + " item(s).";
            if (TryInt(schema, "maxItems", out maxItems) && instance.Array.Count > maxItems)
                return path + " has too many items (maximum " + maxItems + ").";

            JsonValue itemsSchema;
            var output = new List<JsonValue>();
            if (schema.TryGet("items", out itemsSchema))
            {
                for (var i = 0; i < instance.Array.Count; i++)
                {
                    JsonValue child;
                    var err = Check(itemsSchema, instance.Array[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", out child);
                    if (err != null) return err;
                    output.Add(child);
                }
            }
            else
            {
                output.AddRange(instance.Array);
            }
            normalized = JsonValue.FromArray(output);
            return null;
        }

        private static string CheckString(JsonValue schema, JsonValue instance, string path, out JsonValue normalized)
        {
            normalized = null;
            if (instance.Kind != JsonKind.String)
                return path + " must be text.";
            var text = instance.StringValue ?? "";
            int min, max;
            if (TryInt(schema, "minLength", out min) && text.Length < min)
                return path + " must not be empty.";
            if (TryInt(schema, "maxLength", out max) && text.Length > max)
                return path + " is too long.";

            JsonValue en;
            if (schema.TryGet("enum", out en) && en.Kind == JsonKind.Array)
            {
                string canonical = null;
                foreach (var option in en.Array)
                {
                    if (option.Kind == JsonKind.String && string.Equals(option.StringValue, text, StringComparison.OrdinalIgnoreCase))
                    {
                        canonical = option.StringValue;
                        break;
                    }
                }
                if (canonical == null)
                    return path + " must be one of: " + EnumList(en) + ".";
                normalized = JsonValue.FromString(canonical);
                return null;
            }
            normalized = instance;
            return null;
        }

        private static string CheckRange(JsonValue schema, double number, string path, out JsonValue normalized, JsonValue original)
        {
            normalized = original;
            double min, max;
            if (TryDouble(schema, "minimum", out min) && number < min)
                return path + " must be at least " + min.ToString(CultureInfo.InvariantCulture) + ".";
            if (TryDouble(schema, "maximum", out max) && number > max)
                return path + " must be at most " + max.ToString(CultureInfo.InvariantCulture) + ".";
            return null;
        }

        private static bool TryInt(JsonValue schema, string key, out int value)
        {
            value = 0;
            JsonValue v;
            if (!schema.TryGet(key, out v) || !v.IsInteger) return false;
            value = (int)v.AsLong();
            return true;
        }

        private static bool TryDouble(JsonValue schema, string key, out double value)
        {
            value = 0;
            JsonValue v;
            if (!schema.TryGet(key, out v) || v.Kind != JsonKind.Number) return false;
            value = v.AsDouble();
            return true;
        }

        private static string EnumList(JsonValue en)
        {
            var parts = new List<string>();
            foreach (var option in en.Array)
            {
                if (option.Kind == JsonKind.String) parts.Add(option.StringValue);
            }
            return string.Join(", ", parts);
        }
    }
}
