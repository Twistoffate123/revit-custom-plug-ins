using System;

namespace FloorPatternFlattener.Services
{
    /// <summary>
    /// Revit rejects a shared-parameter file whose column headers are not starred
    /// (<c>Error in readParamDatabase</c>). Data rows end with USERMODIFIABLE and HIDEWHENNOVALUE.
    /// </summary>
    public static class SharedParameterFileCheck
    {
        public const string GroupHeader = "*GROUP\tID\tNAME";
        public const string ParamHeader =
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE";

        /// <summary>Null when the text is a file Revit will open. Otherwise a short reason.</summary>
        public static string Problem(string text)
        {
            if (string.IsNullOrEmpty(text)) return "empty";
            if (!text.EndsWith("\r\n", StringComparison.Ordinal)) return "missing final CRLF";
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var group = false;
            var param = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0) return "bare LF";
                if (i == lines.Length - 1)
                {
                    if (line.Length != 0) return "missing final CRLF";
                    continue;
                }
                if (line == GroupHeader) group = true;
                if (line == ParamHeader) param = true;
                if (line.StartsWith("PARAM\t", StringComparison.Ordinal) && !line.EndsWith("\t1\t0", StringComparison.Ordinal))
                    return "param row must end with tab 1 tab 0";
            }
            if (!group) return "missing *GROUP header";
            if (!param) return "missing *PARAM header";
            return null;
        }
    }
}
