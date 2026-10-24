using System;

namespace FloorPatternFlattener.Bridge
{
    /// <summary>
    /// Token check, JSON parse, and argument validation. No Revit types.
    /// The execute callback is only invoked for a command that passed every check.
    /// </summary>
    internal static class BridgeDispatch
    {
        public static DispatchResult Handle(string body, string authorizationHeader, string expectedToken, Func<ValidatedCommand, BridgeResponse> execute)
        {
            var presented = TokenCheck.FromAuthorizationHeader(authorizationHeader);
            if (!TokenCheck.Matches(expectedToken, presented))
            {
                var denied = BridgeResponse.Fail(null, null, "unauthorized",
                    "The connection code was rejected. In Revit, turn Agent Bridge off and on again, then retry. Nothing was changed.");
                return new DispatchResult { StatusCode = 401, Body = denied.ToJson() };
            }

            if (body != null && body.Length > BridgeInfo.MaxBodyBytes)
            {
                var big = BridgeResponse.Fail(null, null, "bad_json", "The request is too large.");
                return new DispatchResult { StatusCode = 413, Body = big.ToJson() };
            }

            JsonValue root;
            try
            {
                root = MiniJson.Parse(string.IsNullOrWhiteSpace(body) ? "" : body);
            }
            catch (FormatException ex)
            {
                var bad = BridgeResponse.Fail(null, null, "bad_json",
                    "The request is not valid JSON. " + ex.Message);
                return new DispatchResult { StatusCode = 400, Body = bad.ToJson() };
            }

            ValidatedCommand command;
            var error = CommandValidator.Validate(root, out command);
            if (error != null)
            {
                string id = null;
                string name = null;
                if (root != null && root.Kind == JsonKind.Object)
                {
                    JsonValue idValue;
                    if (root.TryGet("id", out idValue) && idValue.Kind == JsonKind.String) id = idValue.StringValue;
                    JsonValue nameValue;
                    if (root.TryGet("command", out nameValue) && nameValue.Kind == JsonKind.String)
                        name = (nameValue.StringValue ?? "").Trim().ToLowerInvariant();
                }
                var code = "invalid_args";
                string forbidden;
                if (!string.IsNullOrEmpty(name) && CommandCatalog.TryForbidden(name, out forbidden))
                    code = "forbidden";
                else if (!string.IsNullOrEmpty(name) && !CommandCatalog.TryGet(name, out _))
                    code = name == null ? "invalid_args" : (CommandCatalog.TryForbidden(name, out _) ? "forbidden" : "unknown_command");
                if (!string.IsNullOrEmpty(name) && !CommandCatalog.TryGet(name, out _) && code != "forbidden")
                    code = "unknown_command";
                var failed = BridgeResponse.Fail(id, string.IsNullOrEmpty(name) ? null : name, code, error);
                return new DispatchResult { StatusCode = 400, Body = failed.ToJson() };
            }

            BridgeResponse response;
            try
            {
                response = execute(command) ?? BridgeResponse.Fail(command.Id, command.Name, "failed", "The command returned nothing.");
            }
            catch (Exception ex)
            {
                response = BridgeResponse.Fail(command.Id, command.Name, "failed",
                    "The command failed: " + ex.Message);
            }
            if (string.IsNullOrEmpty(response.Command)) response.Command = command.Name;
            if (string.IsNullOrEmpty(response.Id)) response.Id = command.Id;
            return new DispatchResult { StatusCode = StatusFor(response), Body = response.ToJson() };
        }

        public static int StatusFor(BridgeResponse response)
        {
            if (response == null) return 500;
            if (response.Ok) return 200;
            switch (response.ErrorCode)
            {
                case "timeout": return 504;
                case "revit_busy": return 503;
                case "unauthorized": return 401;
                case "bad_json":
                case "invalid_args":
                case "unknown_command":
                case "forbidden":
                    return 400;
                default:
                    return 200;
            }
        }
    }
}
