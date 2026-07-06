using System.IO;
using System.Linq;

namespace CodeWeave
{
    public static class Utility
    {
        public static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        }

        public static string CompileCommandsName(string projectName)
        {
            string safeName = Utility.SanitizeFileName(projectName);
            return $"compile_commands_{safeName}.json";
        }
    }
}
