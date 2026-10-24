using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FloorPatternFlattener.Bridge
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Small JSON DOM. Numbers keep their original text so element ids are not rounded.
    /// </summary>
    internal sealed class JsonValue
    {
        public JsonKind Kind;
        public bool BoolValue;
        public string NumberText;
        public string StringValue;
        public List<JsonValue> Array;
        public List<KeyValuePair<string, JsonValue>> Object;

        public static JsonValue Null()
        {
            return new JsonValue { Kind = JsonKind.Null };
        }

        public static JsonValue FromBool(bool value)
        {
            return new JsonValue { Kind = JsonKind.Bool, BoolValue = value };
        }

        public static JsonValue FromLong(long value)
        {
            return new JsonValue
            {
                Kind = JsonKind.Number,
                NumberText = value.ToString(CultureInfo.InvariantCulture)
            };
        }

        public static JsonValue FromDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("JSON numbers cannot be NaN or infinity.");
            string text;
            var rounded = Math.Round(value);
            if (Math.Abs(value - rounded) < 1e-9 && Math.Abs(value) < 1e15)
                text = ((long)rounded).ToString(CultureInfo.InvariantCulture);
            else
                text = value.ToString("0.########", CultureInfo.InvariantCulture);
            return new JsonValue { Kind = JsonKind.Number, NumberText = text };
        }

        public static JsonValue FromString(string value)
        {
            return new JsonValue { Kind = JsonKind.String, StringValue = value ?? "" };
        }

        public static JsonValue FromArray(List<JsonValue> items)
        {
            return new JsonValue { Kind = JsonKind.Array, Array = items ?? new List<JsonValue>() };
        }

        public static JsonValue FromObject(List<KeyValuePair<string, JsonValue>> props)
        {
            return new JsonValue { Kind = JsonKind.Object, Object = props ?? new List<KeyValuePair<string, JsonValue>>() };
        }

        public bool IsInteger
        {
            get
            {
                if (Kind != JsonKind.Number || string.IsNullOrEmpty(NumberText)) return false;
                long ignored;
                if (long.TryParse(NumberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ignored))
                    return true;
                double d;
                if (!double.TryParse(NumberText, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return false;
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                var rounded = Math.Round(d);
                return Math.Abs(d - rounded) <= 1e-9 && rounded <= long.MaxValue && rounded >= long.MinValue;
            }
        }

        public long AsLong()
        {
            long whole;
            if (long.TryParse(NumberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole))
                return whole;
            return (long)Math.Round(AsDouble());
        }

        public double AsDouble()
        {
            return double.Parse(NumberText, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public bool TryGet(string key, out JsonValue value)
        {
            value = null;
            if (Kind != JsonKind.Object || Object == null) return false;
            for (var i = 0; i < Object.Count; i++)
            {
                if (Object[i].Key == key)
                {
                    value = Object[i].Value;
                    return true;
                }
            }
            return false;
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        public void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(BoolValue ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(NumberText);
                    break;
                case JsonKind.String:
                    WriteString(sb, StringValue ?? "");
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    if (Array != null)
                    {
                        for (var i = 0; i < Array.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            Array[i].Write(sb);
                        }
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    if (Object != null)
                    {
                        for (var i = 0; i < Object.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            WriteString(sb, Object[i].Key);
                            sb.Append(':');
                            Object[i].Value.Write(sb);
                        }
                    }
                    sb.Append('}');
                    break;
                default:
                    sb.Append("null");
                    break;
            }
        }

        public static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    internal sealed class JsonObj
    {
        private readonly List<KeyValuePair<string, JsonValue>> _props = new List<KeyValuePair<string, JsonValue>>();

        public JsonObj Set(string key, JsonValue value)
        {
            _props.Add(new KeyValuePair<string, JsonValue>(key, value ?? JsonValue.Null()));
            return this;
        }

        public JsonObj Set(string key, string value) { return Set(key, JsonValue.FromString(value)); }
        public JsonObj Set(string key, bool value) { return Set(key, JsonValue.FromBool(value)); }
        public JsonObj Set(string key, long value) { return Set(key, JsonValue.FromLong(value)); }
        public JsonObj Set(string key, int value) { return Set(key, JsonValue.FromLong(value)); }
        public JsonObj Set(string key, double value) { return Set(key, JsonValue.FromDouble(value)); }

        public JsonValue Build()
        {
            return JsonValue.FromObject(_props);
        }
    }

    internal sealed class JsonArr
    {
        private readonly List<JsonValue> _items = new List<JsonValue>();

        public JsonArr Add(JsonValue value)
        {
            _items.Add(value ?? JsonValue.Null());
            return this;
        }

        public JsonValue Build()
        {
            return JsonValue.FromArray(_items);
        }
    }

    internal static class MiniJson
    {
        public const int MaxDepth = 40;
        public const int MaxNodes = 100000;

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is missing.");
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var p = new Parser(text);
            var value = p.ParseValue(0);
            p.SkipWs();
            if (!p.End) throw new FormatException("Unexpected characters after JSON at position " + p.Index + ".");
            return value;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private int _nodes;

            public Parser(string s) { _s = s; }
            public int Index => _i;
            public bool End => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    var c = _s[_i];
                    if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                    _i++;
                }
            }

            public JsonValue ParseValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("JSON is nested too deeply.");
                if (++_nodes > MaxNodes) throw new FormatException("JSON has too many values.");
                SkipWs();
                if (_i >= _s.Length) throw new FormatException("Unexpected end of JSON.");
                var c = _s[_i];
                if (c == '{') return ParseObject(depth);
                if (c == '[') return ParseArray(depth);
                if (c == '"') return JsonValue.FromString(ParseString());
                if (c == 't') return ExpectLiteral("true", JsonValue.FromBool(true));
                if (c == 'f') return ExpectLiteral("false", JsonValue.FromBool(false));
                if (c == 'n') return ExpectLiteral("null", JsonValue.Null());
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                throw new FormatException("Unexpected character '" + c + "' at position " + _i + ".");
            }

            private JsonValue ExpectLiteral(string word, JsonValue value)
            {
                if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw new FormatException("Invalid JSON literal at position " + _i + ".");
                _i += word.Length;
                return value;
            }

            private JsonValue ParseObject(int depth)
            {
                _i++; // {
                var props = new List<KeyValuePair<string, JsonValue>>();
                SkipWs();
                if (Peek('}')) { _i++; return JsonValue.FromObject(props); }
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != '"')
                        throw new FormatException("Expected a property name at position " + _i + ".");
                    var name = ParseString();
                    for (var k = 0; k < props.Count; k++)
                    {
                        if (props[k].Key == name)
                            throw new FormatException("Duplicate property '" + name + "'.");
                    }
                    SkipWs();
                    if (!Peek(':')) throw new FormatException("Expected ':' after '" + name + "'.");
                    _i++;
                    var value = ParseValue(depth + 1);
                    props.Add(new KeyValuePair<string, JsonValue>(name, value));
                    SkipWs();
                    if (Peek('}')) { _i++; break; }
                    if (!Peek(',')) throw new FormatException("Expected ',' or '}' in object at position " + _i + ".");
                    _i++;
                }
                return JsonValue.FromObject(props);
            }

            private JsonValue ParseArray(int depth)
            {
                _i++; // [
                var items = new List<JsonValue>();
                SkipWs();
                if (Peek(']')) { _i++; return JsonValue.FromArray(items); }
                while (true)
                {
                    items.Add(ParseValue(depth + 1));
                    SkipWs();
                    if (Peek(']')) { _i++; break; }
                    if (!Peek(',')) throw new FormatException("Expected ',' or ']' in array at position " + _i + ".");
                    _i++;
                }
                return JsonValue.FromArray(items);
            }

            private string ParseString()
            {
                if (_s[_i] != '"') throw new FormatException("Expected a string at position " + _i + ".");
                _i++;
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new FormatException("Unfinished escape in string.");
                        var e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw new FormatException("Short unicode escape.");
                                int code;
                                if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    throw new FormatException("Bad unicode escape.");
                                sb.Append((char)code);
                                _i += 4;
                                break;
                            default:
                                throw new FormatException("Unknown escape '\\" + e + "'.");
                        }
                    }
                    else if (c < 0x20)
                    {
                        throw new FormatException("Raw control character in string.");
                    }
                    else sb.Append(c);
                }
                throw new FormatException("Unclosed string.");
            }

            private JsonValue ParseNumber()
            {
                var start = _i;
                if (_s[_i] == '-') _i++;
                if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9')
                    throw new FormatException("Invalid number at position " + start + ".");
                if (_s[_i] == '0') _i++;
                else while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9')
                        throw new FormatException("Invalid number at position " + start + ".");
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    if (_i >= _s.Length || _s[_i] < '0' || _s[_i] > '9')
                        throw new FormatException("Invalid number at position " + start + ".");
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                var text = _s.Substring(start, _i - start);
                double ignored;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ignored))
                    throw new FormatException("Invalid number '" + text + "'.");
                return new JsonValue { Kind = JsonKind.Number, NumberText = text };
            }

            private bool Peek(char c)
            {
                return _i < _s.Length && _s[_i] == c;
            }
        }
    }
}
