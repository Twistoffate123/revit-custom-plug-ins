using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace FloorPatternFlattener.Bridge
{
    internal sealed class BridgeServer : IDisposable
    {
        private readonly string _token;
        private readonly int _revitYear;
        private readonly Func<ValidatedCommand, BridgeResponse> _execute;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _stop;

        public BridgeServer(string token, int revitYear, Func<ValidatedCommand, BridgeResponse> execute)
        {
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("A connection code is required.", nameof(token));
            _token = token;
            _revitYear = revitYear;
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public int Port { get; private set; }

        public void Start(int preferredPort)
        {
            if (_listener != null) throw new InvalidOperationException("The bridge is already listening.");
            var start = preferredPort <= 0
                ? 20000 + (Environment.TickCount & 0x7FFF) % 20000
                : preferredPort;
            var attempts = preferredPort <= 0 ? 30 : BridgeInfo.PortAttempts;
            Exception last = null;
            for (var i = 0; i < attempts; i++)
            {
                var port = start + i;
                if (port < 1 || port > 65535) continue;
                HttpListener listener = null;
                try
                {
                    listener = new HttpListener();
                    // Loopback only. Do not add localhost, +, *, or 0.0.0.0.
                    listener.Prefixes.Add("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
                    listener.IgnoreWriteExceptions = true;
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    _stop = false;
                    _thread = new Thread(Loop)
                    {
                        IsBackground = true,
                        Name = "Revit Agent Bridge"
                    };
                    _thread.Start();
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (listener != null) listener.Close(); } catch { /* ignore */ }
                }
            }
            throw new InvalidOperationException(
                "Could not listen on 127.0.0.1. Another program may be using the port, or Windows blocked a local listener. " +
                (last == null ? "" : last.Message), last);
        }

        public void Dispose()
        {
            _stop = true;
            var listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try { listener.Stop(); } catch { /* ignore */ }
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        private void Loop()
        {
            while (!_stop)
            {
                HttpListenerContext context = null;
                try
                {
                    var listener = _listener;
                    if (listener == null || !listener.IsListening) break;
                    context = listener.GetContext();
                }
                catch
                {
                    if (_stop) break;
                    continue;
                }
                if (context != null)
                    ThreadPool.QueueUserWorkItem(Serve, context);
            }
        }

        private void Serve(object state)
        {
            var context = (HttpListenerContext)state;
            try
            {
                var remote = context.Request.RemoteEndPoint as IPEndPoint;
                if (remote == null || !remote.Address.Equals(IPAddress.Loopback))
                {
                    Write(context, 403, "{\"ok\":false,\"error\":{\"code\":\"forbidden\",\"message\":\"Only this computer can use the bridge.\"}}");
                    return;
                }

                var host = context.Request.Headers["Host"] ?? "";
                var expectedHost = "127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase))
                {
                    Write(context, 403, "{\"ok\":false,\"error\":{\"code\":\"forbidden\",\"message\":\"The request did not target 127.0.0.1.\"}}");
                    return;
                }

                var path = (context.Request.Url.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";
                var method = context.Request.HttpMethod ?? "";

                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/v1/health")
                {
                    var health = new JsonObj()
                        .Set("ok", true)
                        .Set("running", true)
                        .Set("product", BridgeInfo.Product)
                        .Set("revitYear", _revitYear)
                        .Set("port", Port)
                        .Set("pid", Process.GetCurrentProcess().Id)
                        .Build();
                    Write(context, 200, health.ToJson());
                    return;
                }

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/v1/command")
                {
                    string body;
                    var tooBig = !TryReadBody(context.Request, out body);
                    if (tooBig)
                    {
                        var big = BridgeResponse.Fail(null, null, "bad_json", "The request is too large.");
                        Write(context, 413, big.ToJson());
                        return;
                    }
                    var auth = context.Request.Headers["Authorization"];
                    var result = BridgeDispatch.Handle(body, auth, _token, _execute);
                    Write(context, result.StatusCode, result.Body);
                    return;
                }

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Write(context, 405, "{\"ok\":false,\"error\":{\"code\":\"bad_json\",\"message\":\"Use GET /v1/health or POST /v1/command.\"}}");
                    return;
                }

                Write(context, 404, "{\"ok\":false,\"error\":{\"code\":\"unknown_command\",\"message\":\"That address is not part of the bridge. Use /v1/command.\"}}");
            }
            catch (Exception ex)
            {
                try
                {
                    var failed = BridgeResponse.Fail(null, null, "failed", "The bridge hit an error: " + ex.Message);
                    Write(context, 500, failed.ToJson());
                }
                catch
                {
                    try { context.Response.Abort(); } catch { /* ignore */ }
                }
            }
        }

        private static bool TryReadBody(HttpListenerRequest request, out string body)
        {
            body = "";
            var stream = request.InputStream;
            if (stream == null) return true;
            var buffer = new byte[8192];
            var total = 0;
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > BridgeInfo.MaxBodyBytes) return false;
                    ms.Write(buffer, 0, read);
                }
                body = Encoding.UTF8.GetString(ms.ToArray());
            }
            return true;
        }

        private static void Write(HttpListenerContext context, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            var response = context.Response;
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.Headers["Cache-Control"] = "no-store";
            using (var output = response.OutputStream)
            {
                output.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
