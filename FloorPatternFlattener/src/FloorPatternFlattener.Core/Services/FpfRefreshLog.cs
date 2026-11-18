using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using FloorPatternFlattener.Storage;

namespace FloorPatternFlattener.Services
{
    /// <summary>Append-only phase log for Refresh / Flatten (survives Revit kill if each line is flushed).</summary>
    public sealed class FpfRefreshLog : IDisposable
    {
        private static readonly object FileLock = new object();

        public static FpfRefreshLog Current { get; private set; }

        /// <summary>Increments on every session so schema diagnostics log once per session, not once per chunk.</summary>
        public static int SessionSerial { get; private set; }

        private readonly StreamWriter _writer;
        private readonly Stopwatch _session = Stopwatch.StartNew();
        private string _floorLabel = "-";
        private int _updaterBlocked;

        private FpfRefreshLog(string sessionName)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FloorPatternFlattener");
            try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
            var path = Path.Combine(dir, "refresh-log.txt");
            StreamWriter w = null;
            try
            {
                w = new StreamWriter(path, append: true, encoding: new UTF8Encoding(false)) { AutoFlush = false };
            }
            catch
            {
                w = null;
            }

            _writer = w;
            Line("session-start name=" + sessionName);
        }

        public static IDisposable BeginSession(string name)
        {
            Current?.Dispose();
            SessionSerial++;
            FpfStorage.ResetSchemaLog();
            var log = new FpfRefreshLog(name ?? "Refresh");
            Current = log;
            return log;
        }

        /// <summary>Phase line inside a session, or a flushed updater line when Flatten has not opened one.</summary>
        public static void LogPhaseAlways(string phase, string detail)
        {
            if (Current != null)
            {
                Current.Phase(phase, 0, detail);
                return;
            }
            LogUpdater("phase=" + (phase ?? "log") + " " + (detail ?? ""));
        }

        public void BeginFloor(Floor floor)
        {
            _floorLabel = floor != null && floor.IsValidObject ? FpfReport.Describe(floor) : "?";
            Line("floor-start id=" + (floor?.Id.ToString() ?? "?"));
        }

        public void Phase(string phase, long elapsedMs, string detail)
        {
            var sb = new StringBuilder();
            sb.Append("phase=").Append(phase);
            sb.Append(" ms=").Append(elapsedMs.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(detail)) sb.Append(' ').Append(detail);
            Line(sb.ToString());
        }

        public void UpdaterBlocked()
        {
            _updaterBlocked++;
            Line("updater-blocked count=" + _updaterBlocked.ToString(CultureInfo.InvariantCulture));
        }

        public static void LogIfActive(string phase, long elapsedMs, string detail) =>
            Current?.Phase(phase, elapsedMs, detail);

        /// <summary>Always append (updater / diagnostics outside Refresh session).</summary>
        public static void LogUpdater(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FloorPatternFlattener");
            try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
            var path = Path.Combine(dir, "refresh-log.txt");
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = ts + " floor=- updater " + detail;
            lock (FileLock)
            {
                try
                {
                    using (var w = new StreamWriter(path, append: true, encoding: new UTF8Encoding(false)))
                    {
                        w.WriteLine(line);
                        w.Flush();
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        public void Dispose()
        {
            Line("session-end ms=" + _session.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                 " updaterBlocked=" + _updaterBlocked.ToString(CultureInfo.InvariantCulture));
            try { _writer?.Dispose(); } catch { /* ignore */ }
            if (Current == this) Current = null;
        }

        private void Line(string message)
        {
            if (_writer == null) return;
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = ts + " floor=" + _floorLabel + " " + message;
            lock (FileLock)
            {
                try
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}