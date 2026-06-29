using System.Collections.Generic;
using System.Linq;

namespace MSBuild.CompileCommands.Extractor
{
    /// <summary>
    /// Sanitizes MSVC compiler arguments for strict <c>clang-cl</c> compliance.
    /// <list type="bullet">
    ///   <item>Prepends <c>--driver-mode=cl</c> and <c>-fms-compatibility</c> (clang-cl header pair).</item>
    ///   <item>Drops flags that clang-cl does not understand (<c>/MP</c>, <c>/Gm</c>, <c>/GL</c>, <c>/FS</c>, <c>/analyze-</c>).</item>
    ///   <item>Merges split <c>/D</c> <c>MACRO</c> tokens into the single-token form <c>/DMACRO</c> that clang-cl requires.</item>
    ///   <item>Keeps <c>/std:c++XX</c> in native MSVC syntax — clang-cl ignores GCC-style <c>-std=</c>.</item>
    /// </list>
    /// </summary>
    public static class MsvcSanitizer
    {
        public static string[] Sanitize(string[] args)
        {
            if (args.Length == 0) return args;

            var result = new List<string>(args.Length + 2);
            result.Add(args[0]); // compiler executable path — preserve as-is

            // Pre-scan to avoid duplicating flags that already exist
            bool hasDriverMode = false;
            bool hasFmsCompat = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--driver-mode=", StringComparison.OrdinalIgnoreCase))
                    hasDriverMode = true;
                if (args[i].Equals("-fms-compatibility", StringComparison.OrdinalIgnoreCase))
                    hasFmsCompat = true;
            }

            // Insert clang-cl compatibility flags immediately after the compiler path
            if (!hasDriverMode) result.Add("--driver-mode=cl");
            if (!hasFmsCompat) result.Add("-fms-compatibility");

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                // Skip any pre-existing instances of the flags we just inserted
                if (arg.StartsWith("--driver-mode=", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-fms-compatibility", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ShouldDrop(arg)) continue;

                // Merge split "/D" "MACRO" → "/DMACRO".
                // MSBuild's GetClCommandLines target can emit the define prefix and the
                // macro name as separate argv tokens. clang-cl (and cl.exe) require them
                // concatenated; a bare "/D" followed by a space is not valid.
                if (arg.Equals("/D", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    result.Add("/D" + args[i + 1]);
                    i++; // consume the macro-name token
                    continue;
                }

                // Keep /std:c++XX in native MSVC syntax.
                // clang-cl honours /std:c++17, /std:c++20, /std:c++latest etc. directly.
                // GCC-style -std=c++17 is silently ignored in --driver-mode=cl.
                // (No conversion needed — pass through unchanged.)

                result.Add(arg);
            }

            return result.ToArray();
        }

        private static bool ShouldDrop(string arg)
        {
            if (arg.Length < 3) return false;

            // /MP[n] — parallel build; n is an optional digit sequence
            if (arg.StartsWith("/MP", StringComparison.OrdinalIgnoreCase) &&
                arg.Substring(3).All(char.IsDigit)) {
                return true;
            }

            // /Gm or /Gm- — minimal rebuild (deprecated in VS 2019+)
            if (arg.StartsWith("/Gm", StringComparison.OrdinalIgnoreCase) &&
                (arg.Length == 3 || (arg.Length == 4 && arg[3] == '-')))
                return true;

            // /GL or /GL- — whole-program optimization / link-time code generation
            if (arg.StartsWith("/GL", StringComparison.OrdinalIgnoreCase) &&
                (arg.Length == 3 || (arg.Length == 4 && arg[3] == '-')))
                return true;

            // /FS — force synchronous PDB writes
            if (arg.Equals("/FS", StringComparison.OrdinalIgnoreCase))
                return true;

            // /analyze- — disable static analysis (the enablement form /analyze is kept)
            if (arg.Equals("/analyze-", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
