using System;
using System.IO;

namespace FloorPatternFlattener
{
    /// <summary>How a floor is flattened. Stored per floor (absent = Lines, the original behaviour).</summary>
    public enum FlattenStyle
    {
        /// <summary>Hatch drawn as model lines at the live pattern's exact positions (plan-true on slopes).</summary>
        Lines = 0,

        /// <summary>Thin flat surface with a copy of the floor's material patterns; filters control the pattern.</summary>
        Surface = 1
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
}
