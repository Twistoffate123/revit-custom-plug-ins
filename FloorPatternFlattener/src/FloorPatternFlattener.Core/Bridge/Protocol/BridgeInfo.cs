using System;
using System.Collections.Generic;

namespace FloorPatternFlattener.Bridge
{
    internal static class BridgeInfo
    {
        public const string Product = "Floor Pattern Flattener Agent Bridge";
        public const string ResourceName = "FloorPatternFlattener.Bridge.commands.json";
        public const int DefaultPort = 17823;
        public const int PortAttempts = 10;
        public const int MaxBodyBytes = 1000000;
        public const string SessionFolderName = ".revit-agent";
        public const string SessionFileName = "bridge.json";

        public static string SessionDirectory()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, SessionFolderName);
        }

        public static string SessionPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("REVIT_AGENT_SESSION");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            return System.IO.Path.Combine(SessionDirectory(), SessionFileName);
        }
    }

    internal sealed class SessionInfo
    {
        public string Host;
        public int Port;
        public string Token;
        public int Pid;
        public int RevitYear;
        public string StartedUtc;
        public string Product;
    }

    internal static class SessionFile
    {
        public static string Serialize(SessionInfo info)
        {
            var obj = new JsonObj()
                .Set("host", info.Host ?? "127.0.0.1")
                .Set("port", info.Port)
                .Set("token", info.Token ?? "")
                .Set("pid", info.Pid)
                .Set("revitYear", info.RevitYear)
                .Set("startedUtc", info.StartedUtc ?? "")
                .Set("product", info.Product ?? BridgeInfo.Product);
            return obj.Build().ToJson();
        }

        public static SessionInfo Parse(string json)
        {
            JsonValue root;
            try { root = MiniJson.Parse(json); }
            catch (FormatException ex)
            {
                throw new FormatException("The bridge connection file is not valid JSON. " + ex.Message);
            }
            if (root.Kind != JsonKind.Object)
                throw new FormatException("The bridge connection file must be a JSON object.");

            var info = new SessionInfo
            {
                Host = RequiredString(root, "host"),
                Port = (int)RequiredLong(root, "port"),
                Token = RequiredString(root, "token"),
                Pid = (int)RequiredLong(root, "pid"),
                RevitYear = (int)RequiredLong(root, "revitYear"),
                StartedUtc = RequiredString(root, "startedUtc"),
                Product = RequiredString(root, "product")
            };
            if (!string.Equals(info.Host, "127.0.0.1", StringComparison.Ordinal))
                throw new FormatException("The bridge connection file must point at 127.0.0.1.");
            if (info.Port < 1 || info.Port > 65535)
                throw new FormatException("The bridge port in the connection file is not valid.");
            if (string.IsNullOrEmpty(info.Token))
                throw new FormatException("The bridge connection file has no connection code.");
            return info;
        }

        private static string RequiredString(JsonValue obj, string key)
        {
            JsonValue value;
            if (!obj.TryGet(key, out value) || value.Kind != JsonKind.String)
                throw new FormatException("The bridge connection file is missing '" + key + "'.");
            return value.StringValue ?? "";
        }

        private static long RequiredLong(JsonValue obj, string key)
        {
            JsonValue value;
            if (!obj.TryGet(key, out value) || !value.IsInteger)
                throw new FormatException("The bridge connection file is missing a number '" + key + "'.");
            return value.AsLong();
        }
    }

    internal static class TokenCheck
    {
        public static bool Matches(string expected, string provided)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
            var a = Encoding.UTF8.GetBytes(expected);
            var b = Encoding.UTF8.GetBytes(provided);
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string FromAuthorizationHeader(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            const string prefix = "Bearer ";
            if (header.Length > prefix.Length && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return header.Substring(prefix.Length).Trim();
            return null;
        }
    }

    internal sealed class BridgeResponse
    {
        public bool Ok;
        public string Id;
        public string Command;
        public JsonValue Result;
        public string ErrorCode;
        public string ErrorMessage;
        public JsonValue ErrorDetails;

        public static BridgeResponse Success(string id, string command, JsonValue result)
        {
            return new BridgeResponse
            {
                Ok = true,
                Id = id,
                Command = command,
                Result = result ?? JsonValue.FromObject(new List<KeyValuePair<string, JsonValue>>())
            };
        }

        public static BridgeResponse Fail(string id, string command, string code, string message, JsonValue details)
        {
            return new BridgeResponse
            {
                Ok = false,
                Id = id,
                Command = command,
                ErrorCode = code,
                ErrorMessage = message,
                ErrorDetails = details
            };
        }

        public string ToJson()
        {
            var props = new List<KeyValuePair<string, JsonValue>>
            {
                new KeyValuePair<string, JsonValue>("ok", JsonValue.FromBool(Ok))
            };
            if (!string.IsNullOrEmpty(Id))
                props.Add(new KeyValuePair<string, JsonValue>("id", JsonValue.FromString(Id)));
            if (!string.IsNullOrEmpty(Command))
                props.Add(new KeyValuePair<string, JsonValue>("command", JsonValue.FromString(Command)));
            if (Ok)
            {
                props.Add(new KeyValuePair<string, JsonValue>("result", Result ?? JsonValue.FromObject(new List<KeyValuePair<string, JsonValue>>())));
            }
            else
            {
                var errProps = new List<KeyValuePair<string, JsonValue>>
                {
                    new KeyValuePair<string, JsonValue>("code", JsonValue.FromString(ErrorCode ?? "failed")),
                    new KeyValuePair<string, JsonValue>("message", JsonValue.FromString(ErrorMessage ?? "The command failed."))
                };
                if (ErrorDetails != null && ErrorDetails.Kind == JsonKind.Object && ErrorDetails.Object != null)
                {
                    for (var i = 0; i < ErrorDetails.Object.Count; i++)
                    {
                        var key = ErrorDetails.Object[i].Key;
                        if (key == "code" || key == "message") continue;
                        errProps.Add(ErrorDetails.Object[i]);
                    }
                }
                props.Add(new KeyValuePair<string, JsonValue>("error", JsonValue.FromObject(errProps)));
            }
            return JsonValue.FromObject(props).ToJson();
        }
    }

    internal sealed class DispatchResult
    {
        public int StatusCode;
        public string Body;
    }

    internal sealed class ValidatedCommand
    {
        public string Id;
        public string Name;
        public JsonValue Args;
    }
}
