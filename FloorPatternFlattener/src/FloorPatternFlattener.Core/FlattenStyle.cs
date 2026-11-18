using System;
using System.Globalization;
using System.IO;

namespace FloorPatternFlattener
{
    /// <summary>How a floor is flattened. Stored per floor (absent = Lines, the original behaviour).</summary>
    public enum FlattenStyle
    {
        /// <summary>Hatch drawn as model lines at the live pattern's exact positions (plan-true on slopes).</summary>
        Lines = 0

        // 1 was "Surface" (removed). Floors still storing it read back as Lines and are rebuilt as Lines;
        // see FpfStorage.HasLegacySurfaceStyle.
    }

    /// <summary>Remembers the last style chosen in the Flatten dialog (per Windows user, all Revit years).</summary>
    internal static class LastStyleSetting
    {
        private static string FilePath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FloorPatternFlattener");
                return Path.Combine(dir, "last-flatten-style.txt");
            }
        }

        public static FlattenStyle Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path) && Enum.TryParse(File.ReadAllText(path).Trim(), true, out FlattenStyle s)
                    && Enum.IsDefined(typeof(FlattenStyle), s))
                    return s;
            }
            catch
            {
                // ignore - default below
            }
            return FlattenStyle.Lines;
        }

        public static void Save(FlattenStyle style)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, style.ToString());
            }
            catch
            {
                // not critical
            }
        }
    }

    /// <summary>Remembers default FPF Lift (mm) for new Flatten runs.</summary>
    internal static class LastFloorLiftSetting
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloorPatternFlattener", "floor-lift-mm.txt");

        public static double Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return FpfOptions.DefaultFloorLiftMm;
                if (double.TryParse(File.ReadAllText(FilePath).Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v)) return v;
            }
            catch { /* ignore */ }
            return FpfOptions.DefaultFloorLiftMm;
        }

        public static void Save(double mm)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, mm.ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch { /* not critical */ }
        }
    }


    /// <summary>Remembers the last "Hide live floor pattern and fold lines" choice for new Flatten runs.</summary>
    internal static class LastHideLiveSetting
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloorPatternFlattener", "hide-live.txt");

        public static bool Load()
        {
            try { return File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() == "on"; }
            catch { return false; }
        }

        public static void Save(bool on)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, on ? "on" : "off");
            }
            catch { /* not critical */ }
        }
    }

    /// <summary>"Auto: On / Off" ribbon switch (per Windows user, remembered between sessions). Default On.</summary>
    public static class AutoUpdateSetting
    {
        private static bool? _value;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloorPatternFlattener", "auto-update.txt");

        public static bool Enabled
        {
            get
            {
                if (_value == null)
                {
                    try
                    {
                        _value = !File.Exists(FilePath) ||
                                 !string.Equals(File.ReadAllText(FilePath).Trim(), "off", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        _value = true;
                    }
                }
                return _value.Value;
            }
            set
            {
                _value = value;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(FilePath, value ? "on" : "off");
                }
                catch
                {
                    // not critical
                }
            }
        }
    }
}
