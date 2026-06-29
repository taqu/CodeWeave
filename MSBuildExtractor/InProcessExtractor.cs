using MessagePack.Formatters;
using Microsoft.Build.Execution;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MSBuild.CompileCommands.Extractor
{
    public class InProcessExtractor : ICompileCommandsExtractor
    {
        private readonly string _projectPath;
        private readonly string _projectName;
        private readonly bool _enableLogger;
        private readonly string _configuration;
        private readonly string _platform;
        private readonly Dictionary<string, string> _globalProperties;
        private readonly string? _vcToolsInstallDir;
        private readonly bool _emitDefaults;
        private readonly bool _mergeDefaults;
        private string[] _externalIncludePaths = [];
        private string[] _includePaths = [];
        // System include paths extracted from VC_IncludePath and WindowsSDK_IncludePath.
        // Injected as /imsvc (clang-cl system-include flag) to suppress warnings from MSVC/SDK headers.
        private string[] _systemIncludePaths = [];
        private string? _fixupPropsPath;

        // Resolve unevaluated MSBuild expressions like $([MSBuild]::NormalizePath('base', 'rel'))
        // that appear in IncludePath when GetPropertyValue doesn't fully evaluate intrinsics.
        private static string? TryResolveMsBuildExpression(string expr)
        {
            Match match = Regex.Match(expr, @"\$\(\[MSBuild\]::NormalizePath\('([^']+)'(?:.*?)(?:\)\))?(.*)$");
            if (match.Success)
            {
                string basePath = match.Groups[1].Value;
                string suffix = match.Groups[2].Value.TrimStart('\\', '\'', ',', ' ');
                try
                {
                    var resolved = Path.GetFullPath(Path.Combine(basePath, suffix));
                    if (Directory.Exists(resolved))
                        return resolved;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> ResolveIncludePathEntries(IEnumerable<string> entries)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (!entry.StartsWith("$("))
                {
                    string resolved;
                    try { resolved = Uri.UnescapeDataString(entry.Trim()); } catch { resolved = entry.Trim(); }
                    if (!string.IsNullOrEmpty(resolved))
                        yield return resolved;
                }
                else
                {
                    string resolved = TryResolveMsBuildExpression(entry);
                    if (resolved != null)
                        yield return resolved;
                }
            }
        }

        // Derive VS auxiliary include paths from VCToolsInstallDir.
        // VCToolsInstallDir is like ...\VC\Tools\MSVC\14.50.35717
        // VS auxiliary paths are ...\VC\Auxiliary\VS\include and ...\VC\Auxiliary\VS\UnitTest\include
        private static IEnumerable<string> GetVsAuxiliaryIncludePaths(string? vcToolsInstallDir)
        {
            if (string.IsNullOrEmpty(vcToolsInstallDir)) return [];
            try
            {
                var vcDir = Path.GetFullPath(Path.Combine(vcToolsInstallDir, "..", "..", ".."));
                var results = new List<string>();
                var auxInclude = Path.Combine(vcDir, "Auxiliary", "VS", "include");
                if (Directory.Exists(auxInclude))
                    results.Add(auxInclude);
                var auxUnitTest = Path.Combine(vcDir, "Auxiliary", "VS", "UnitTest", "include");
                if (Directory.Exists(auxUnitTest))
                    results.Add(auxUnitTest);
                return results;
            }
            catch { return []; }
        }

        /// <summary>
        /// Detects the NETFXKitsDir by enumerating installed versions under
        /// %ProgramFiles(x86)%\Windows Kits\NETFXSDK and picking the highest.
        /// Returns the path with a trailing backslash, or null if not found.
        /// </summary>
        internal static string? FindNETFXKitsDir()
        {
            try
            {
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var netfxSdkRoot = Path.Combine(programFilesX86, "Windows Kits", "NETFXSDK");
                if (!Directory.Exists(netfxSdkRoot)) return null;

                var best = Directory.GetDirectories(netfxSdkRoot)
                    .Select(d => new { Path = d, Name = Path.GetFileName(d) })
                    .Where(d => Version.TryParse(d.Name, out _))
                    .OrderByDescending(d => Version.Parse(d.Name))
                    .FirstOrDefault();

                if (best == null) return null;
                return best.Path.EndsWith("\\") ? best.Path : best.Path + "\\";
            }
            catch { return null; }
        }

        /// <summary>
        /// Replaces unresolved MSBuild fallback values (e.g., "VCInstallDir_170_is_not_defined")
        /// in compiler arguments with the correct resolved paths. Removes include path
        /// arguments that still contain unresolved placeholders after replacement.
        /// </summary>
        internal static string[] SanitizeFallbackPaths(string[] args, string? vcToolsInstallDir)
        {
            // Build replacement map
            var replacements = new List<(string Placeholder, string Replacement)>();

            if (vcToolsInstallDir != null)
            {
                try
                {
                    var vcDir = Path.GetFullPath(Path.Combine(vcToolsInstallDir, "..", "..", ".."));
                    if (Directory.Exists(vcDir))
                    {
                        var vcDirSlash = vcDir.EndsWith("\\") ? vcDir : vcDir + "\\";
                        // Handle any version suffix: VCInstallDir_170, VCInstallDir_180, etc.
                        // The fallback format is: <PropertyName>_is_not_defined or <PropertyName>_is not_defined
                        replacements.Add(("VCInstallDir_170_is_not_defined", vcDirSlash));
                        replacements.Add(("VCInstallDir_180_is_not_defined", vcDirSlash));
                        replacements.Add(("VCInstallDir_is_not_defined", vcDirSlash));
                        replacements.Add(("VCInstallDir_170_is not_defined", vcDirSlash));
                        replacements.Add(("VCInstallDir_180_is not_defined", vcDirSlash));
                    }

                    string vtNorm = vcToolsInstallDir.EndsWith("\\") ? vcToolsInstallDir : vcToolsInstallDir + "\\";
                    replacements.Add(("VCToolsInstallDir_170_is_not_defined", vtNorm));
                    replacements.Add(("VCToolsInstallDir_180_is_not_defined", vtNorm));
                    replacements.Add(("VCToolsInstallDir_is_not_defined", vtNorm));
                    replacements.Add(("VCToolsInstallDir_170_is not_defined", vtNorm));
                    replacements.Add(("VCToolsInstallDir_180_is not_defined", vtNorm));
                }
                catch { }
            }

            string netfxDir = FindNETFXKitsDir();
            if (netfxDir != null)
            {
                replacements.Add(("NETFXKitsDir_is not_defined", netfxDir));
                replacements.Add(("NETFXKitsDir_is_not_defined", netfxDir));
            }

            if (replacements.Count == 0) return args;

            var result = new List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                // Apply known replacements
                foreach (var (placeholder, replacement) in replacements)
                {
                    int index = arg.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase);
                    if (0<=index) {
                        string str = arg.Substring(index, placeholder.Length);
                        arg = arg.Replace(str, replacement);
                    }
                }

                // Check for any remaining unresolved placeholders
                if (0<=arg.IndexOf("_is_not_defined", StringComparison.OrdinalIgnoreCase) ||
                    0<=arg.IndexOf("_is not_defined", StringComparison.OrdinalIgnoreCase))
                {
                    // For /I or /external:I with a separate path arg, skip both
                    if ((arg.Equals("/I", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("/external:I", StringComparison.OrdinalIgnoreCase)) &&
                        i + 1 < args.Length)
                    {
                        i++; // skip path arg too
                    }
                    continue; // drop this arg
                }

                result.Add(arg);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Merges flags from the project-wide default entry into a per-file argument
        /// list. Only adds flags that are not already present, so per-file overrides
        /// are preserved. Handles /D, /I, /std:, /W, /EH, /GR, /MD, /MT and other
        /// switch families by checking the prefix.
        /// </summary>
        internal static void MergeDefaultFlags(List<string> fileArgs, string[] defaultArgs)
        {
            // Build a set of flag prefixes already present in the per-file args.
            // For /D and /I we track the full value; for others we track the prefix.
            var existingDefines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in fileArgs)
            {
                if (a.StartsWith("/D", StringComparison.OrdinalIgnoreCase) && a.Length > 2)
                    existingDefines.Add(a);
                else if (a.StartsWith("/I", StringComparison.OrdinalIgnoreCase) && a.Length > 2)
                    existingIncludes.Add(a);
                else if (a.StartsWith("/std:", StringComparison.OrdinalIgnoreCase))
                    existingPrefixes.Add("/std:");
                else if (a.StartsWith("/EH", StringComparison.OrdinalIgnoreCase))
                    existingPrefixes.Add("/EH");
                else if (a.StartsWith("/GR", StringComparison.OrdinalIgnoreCase))
                    existingPrefixes.Add("/GR");
                else if (a.StartsWith("/MD", StringComparison.OrdinalIgnoreCase) ||
                         a.StartsWith("/MT", StringComparison.OrdinalIgnoreCase))
                    existingPrefixes.Add("/M");
                else if (a.StartsWith("/W", StringComparison.OrdinalIgnoreCase) && a.Length > 2)
                    existingPrefixes.Add("/W");
                else if (a.StartsWith("/Zc:", StringComparison.OrdinalIgnoreCase))
                {
                    // Track each /Zc: sub-option separately (e.g. /Zc:wchar_t, /Zc:forScope)
                    int colonEnd = a.IndexOf('-');
                    if (colonEnd < 0) colonEnd = a.Length;
                    existingPrefixes.Add(a.Substring(0, colonEnd));
                }
            }

            // Insert new flags from defaults just before the source file (last element).
            int insertPos = fileArgs.Count - 1;

            foreach (string a in defaultArgs)
            {
                // Skip cl.exe path, --target, -ferror-limit (already in baseArgs)
                if (a.StartsWith("--target=", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("-ferror-limit", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip build-output flags
                if (a.StartsWith("/Fo", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("/Fd", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (a.StartsWith("/D", StringComparison.OrdinalIgnoreCase) && a.Length > 2)
                {
                    if (!existingDefines.Contains(a))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingDefines.Add(a);
                    }
                }
                else if (a.StartsWith("/I", StringComparison.OrdinalIgnoreCase) && a.Length > 2)
                {
                    if (!existingIncludes.Contains(a))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingIncludes.Add(a);
                    }
                }
                else if (a.StartsWith("/std:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!existingPrefixes.Contains("/std:"))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingPrefixes.Add("/std:");
                    }
                }
                else if (a.StartsWith("/EH", StringComparison.OrdinalIgnoreCase))
                {
                    if (!existingPrefixes.Contains("/EH"))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingPrefixes.Add("/EH");
                    }
                }
                else if (a.StartsWith("/GR", StringComparison.OrdinalIgnoreCase))
                {
                    if (!existingPrefixes.Contains("/GR"))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingPrefixes.Add("/GR");
                    }
                }
                else if (a.StartsWith("/MD", StringComparison.OrdinalIgnoreCase) ||
                         a.StartsWith("/MT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!existingPrefixes.Contains("/M"))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingPrefixes.Add("/M");
                    }
                }
                else if (a.StartsWith("/W", StringComparison.OrdinalIgnoreCase) && a.Length > 2 &&
                         !a.StartsWith("/we", StringComparison.OrdinalIgnoreCase) &&
                         !a.StartsWith("/wd", StringComparison.OrdinalIgnoreCase))
                {
                    if (!existingPrefixes.Contains("/W"))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingPrefixes.Add("/W");
                    }
                }
                else if (a.StartsWith("/Zc:", StringComparison.OrdinalIgnoreCase))
                {
                    int colonEnd = a.IndexOf('-');
                    if (colonEnd < 0) colonEnd = a.Length;
                    string key = a.Substring(0, colonEnd);
                    if (!existingPrefixes.Contains(key))
                    {
                        fileArgs.Insert(insertPos++, a);
                        existingPrefixes.Add(key);
                    }
                }
            }
        }

        public InProcessExtractor(
            string projectPath,
            string configuration = "Debug",
            string platform = "x64",
            bool enableLogger = false,
            string? solutionDir = null,
            string? vcToolsInstallDir = null,
            bool emitDefaults = false,
            bool mergeDefaults = false)
        {
            _projectPath = Path.GetFullPath(projectPath);

            if (!File.Exists(_projectPath))
                throw new FileNotFoundException($"Project file not found: {_projectPath}");

            _projectName = Path.GetFileNameWithoutExtension(_projectPath);
            _enableLogger = enableLogger;
            _configuration = configuration;
            _platform = platform;
            _emitDefaults = emitDefaults;
            _mergeDefaults = mergeDefaults;
            _vcToolsInstallDir = vcToolsInstallDir?.TrimEnd('\\');
            if (_vcToolsInstallDir != null && !_vcToolsInstallDir.StartsWith(@"\\"))
                _vcToolsInstallDir = _vcToolsInstallDir.Replace(@"\\", @"\");

            _globalProperties = new Dictionary<string, string>
            {
                { "Configuration", configuration },
                { "Platform", platform },
                { "DesignTimeBuild", "true" },
                { "BuildingInsideVisualStudio", "true" },
                // Design-time builds should not build referenced projects.
                { "BuildProjectReferences", "false" },
                // Stable locale for MSBuild error strings.
                // MSBuild error strings and task messages.
                { "LangID", "1033" },
                // VC++ projects use OutDir, not OutputPath. When the .NET SDK's
                // _CheckForInvalidConfigurationAndPlatform target runs, it fails
                // if OutputPath is empty. SkipInvalidConfigurations bypasses this.
                { "SkipInvalidConfigurations", "true" }
            };

            // Create a temporary .props file that defines default ClCompile metadata.
            // FixupCLCompileOptions in Microsoft.CppCommon.Targets uses unqualified
            // %(PrecompiledHeaderFile) for batching, causing MSB4096 when some items
            // have the metadata and others don't. ForceImportBeforeCppTargets injects
            // the defaults at the XML import level before targets execute.
            _fixupPropsPath = CreateFixupPropsFile();
            if (_fixupPropsPath != null)
            {
                _globalProperties["ForceImportBeforeCppTargets"] = _fixupPropsPath;
            }

            if (solutionDir is not null)
            {
                _globalProperties["SolutionDir"] = solutionDir.EndsWith("\\")
                    ? solutionDir : solutionDir + "\\";
            }

            if (vcToolsInstallDir is not null)
            {
                string normalized = vcToolsInstallDir.StartsWith(@"\\")
                    ? vcToolsInstallDir
                    : vcToolsInstallDir.Replace(@"\\", @"\");
                _globalProperties["VCToolsInstallDir"] = normalized.EndsWith("\\")
                    ? normalized : normalized + "\\";

                // Derive VCInstallDir from VCToolsInstallDir.
                // VCToolsInstallDir is like ...\VC\Tools\MSVC\14.x; go up 3 levels to get ...\VC\.
                // Setting VCInstallDir as a global property prevents repo-specific props files
                // (e.g., Microsoft.Cpp.VCTools.props) from overriding it with placeholder values
                // like "VCInstallDir_170_is_not_defined" when VCInstallDir_170 isn't set.
                try
                {
                    var vcInstallDir = Path.GetFullPath(Path.Combine(normalized, "..", "..", ".."));
                    if (Directory.Exists(vcInstallDir))
                    {
                        var vcInstallDirNorm = vcInstallDir.EndsWith("\\") ? vcInstallDir : vcInstallDir + "\\";
                        _globalProperties["VCInstallDir"] = vcInstallDirNorm;
                    }
                }
                catch { /* best-effort */ }
            }

            // Detect and set NETFXKitsDir to prevent unresolved fallback values.
            // Enumerate installed versions and pick the highest.
            string netfxKitsDir = FindNETFXKitsDir();
            if (netfxKitsDir != null)
                _globalProperties["NETFXKitsDir"] = netfxKitsDir;

            // WindowsTargetPlatformVersion is resolved after project evaluation
            // so that projects with explicit full versions aren't overridden.
        }

        public List<CompileCommand> ExtractCompileCommands()
        {
            try
            {
                List<ClCommandLineEntry> entries;
                try
                {
                    entries = GetClCommandLines();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Info: Design-time build failed for {_projectPath}: {ex.Message}");
                    entries = new List<ClCommandLineEntry>();
                }

                var commands = entries.Count > 0 ? ToCompileCommands(entries) : new List<CompileCommand>();
                if (commands.Count > 0)
                    return commands;

                // Fallback for GN-generated projects (Chromium, Crashpad, WebRTC):
                // These have IntelliSense settings in ItemDefinitionGroup/ClCompile
                // but source files as CustomBuild items that delegate to ninja.
                var fallback = TryExtractFromItemDefinitionGroup();
                if (fallback.Count > 0)
                {
                    Console.Error.WriteLine($"Info: Used ItemDefinitionGroup fallback for {_projectPath} ({fallback.Count} entries)");
                    return fallback;
                }

                Console.Error.WriteLine($"Warning: No compile commands found in {_projectPath}");
                return new List<CompileCommand>();
            }
            finally
            {
                CleanupFixupPropsFile();
            }
        }

        public void WriteCompileCommandsJson(string? outputPath = null)
        {
            var commands = ExtractCompileCommands();
            outputPath ??= Path.Combine(Path.GetDirectoryName(_projectPath)!, "compile_commands.json");

            var json = JsonSerializer.Serialize(commands, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            File.WriteAllText(outputPath, json);
        }

        /// <summary>
        /// Splits <paramref name="vcIncludePath"/> and <paramref name="windowsSdkIncludePath"/>
        /// (semicolon-separated) into absolute paths that exist on disk.
        /// Skips unresolved MSBuild expressions and non-existent directories.
        /// </summary>
        private static string[] ResolveSystemIncludePaths(string vcIncludePath, string windowsSdkIncludePath)
        {
            var result = new List<string>();
            foreach (string raw in new[] { vcIncludePath, windowsSdkIncludePath })
            {
                if (string.IsNullOrEmpty(raw)) continue;
                foreach (string entry in raw.Split(new char[]{';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = entry.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    if (trimmed.StartsWith("$(") || trimmed.StartsWith("%(")) continue;
                    try { if (Directory.Exists(trimmed)) result.Add(trimmed); }
                    catch { }
                }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return result.Where(p => seen.Add(p)).ToArray();
        }

        private List<ClCommandLineEntry> GetClCommandLines()
        {
            var projectInstance = new ProjectInstance(_projectPath, _globalProperties, null);

            // Resolve ambiguous WindowsTargetPlatformVersion after evaluation
            // so projects with explicit full versions (e.g. 10.0.19041.0) aren't overridden.
            var wtp = projectInstance.GetPropertyValue("WindowsTargetPlatformVersion");
            if (string.IsNullOrEmpty(wtp) || wtp == "10" || wtp == "10.0")
            {
                string resolved = VsWhereHelper.FindLatestWindowsSdkVersion();
                if (resolved != null)
                    projectInstance.SetProperty("WindowsTargetPlatformVersion", resolved);
            }

            // Extract MSVC toolset and Windows SDK system include paths.
            // VC_IncludePath depends on VCToolsInstallDir (set in global properties) so it
            // evaluates correctly. WindowsSDK_IncludePath depends on WindowsTargetPlatformVersion;
            // ResolveSystemIncludePaths filters out any paths that don't exist on disk, which
            // handles the vague "10.0" placeholder case gracefully.
            var vcIncludePath = projectInstance.GetPropertyValue("VC_IncludePath");
            var windowsSdkIncludePath = projectInstance.GetPropertyValue("WindowsSDK_IncludePath");
            _systemIncludePaths = ResolveSystemIncludePaths(vcIncludePath, windowsSdkIncludePath);

            // Fix MSB4096: FixupCLCompileOptions in Microsoft.CppCommon.Targets uses
            // unqualified %(PrecompiledHeaderFile) for batching. When some ClCompile items
            // have this metadata (from ItemDefinitionGroup) and others don't, MSBuild
            // throws MSB4096. Ensure all items have the metadata defined (even if empty).
            EnsureClCompileMetadataConsistency(projectInstance);

            // IncludePath / ExternalIncludePath are read from the GetProjectDirectories
            // target output below (authoritative source that handles UseEnv=true and Makefile
            // project resolution that raw GetPropertyValue misses).

            var buildParams = new BuildParameters();
            if (_enableLogger)
            {
                buildParams.Loggers = new[] { new Microsoft.Build.Logging.ConsoleLogger(Microsoft.Build.Framework.LoggerVerbosity.Detailed) };
            }

            // Batch design-time targets into a single BuildRequestData:
            //  - GetProjectDirectories provides authoritative IncludePath and ExternalIncludePath values,
            //    including UseEnv and Makefile resolution that raw GetPropertyValue misses.
            //  - ComputeReferenceCLInput resolves PublicIncludeDirectories from project references
            //    and adds them to ClCompile.AdditionalIncludeDirectories before GetClCommandLines runs.
            //  - ReplaceExistingProjectInstance prevents stale cached ProjectInstances
            //    across --all-configurations iterations.
            var buildRequestData = new BuildRequestData(
                projectInstance,
                new[] { "ComputeReferenceCLInput", "GetProjectDirectories", "GetClCommandLines" },
                hostServices: null,
                BuildRequestDataFlags.ReplaceExistingProjectInstance);
            var buildResult = BuildManager.DefaultBuildManager.Build(buildParams, buildRequestData);

            // Harvest GetProjectDirectories even on failure. It's a light target
            // and often succeeds even when GetClCommandLines doesn't.
            if (buildResult.ResultsByTarget.TryGetValue("GetProjectDirectories", out var projDirsResult)
                && projDirsResult.Items.Length > 0)
            {
                var directoriesItem = projDirsResult.Items[0];
                var externalIncludePath = directoriesItem.GetMetadata("ExternalIncludePath");
                var includePath = directoriesItem.GetMetadata("IncludePath");

                if (!string.IsNullOrEmpty(externalIncludePath))
                {
                    _externalIncludePaths = ResolveIncludePathEntries(
                        externalIncludePath.Split(new []{';'}, StringSplitOptions.RemoveEmptyEntries)).ToArray();
                }
                if (!string.IsNullOrEmpty(includePath))
                {
                    var externalSet = new HashSet<string>(_externalIncludePaths, StringComparer.OrdinalIgnoreCase);
                    _includePaths = ResolveIncludePathEntries(
                        includePath.Split(new []{';'}, StringSplitOptions.RemoveEmptyEntries))
                        .Where(p => !externalSet.Contains(p))
                        .ToArray();
                }
            }

            // Add VS auxiliary include paths from VCToolsInstallDir. The IncludePath
            // property contains $([MSBuild]::NormalizePath(...)) calls that can't
            // be fully evaluated, so these paths are appended explicitly here.
            var vcToolsDir = _vcToolsInstallDir ?? projectInstance.GetPropertyValue("VCToolsInstallDir")?.TrimEnd('\\');
            var allIncludes = new HashSet<string>(_externalIncludePaths.Concat(_includePaths), StringComparer.OrdinalIgnoreCase);
            var auxPaths = GetVsAuxiliaryIncludePaths(vcToolsDir)
                .Where(p => !allIncludes.Contains(p))
                .ToArray();
            if (auxPaths.Length > 0)
                _includePaths = _includePaths.Concat(auxPaths).ToArray();

            if (buildResult.OverallResult != BuildResultCode.Success
                && !buildResult.ResultsByTarget.ContainsKey("GetClCommandLines"))
            {
                // GN-generated projects may fail because the configuration doesn't match.
                // Return empty list to trigger the ItemDefinitionGroup fallback.
                if (_enableLogger)
                    Console.Error.WriteLine($"Design-time build failed for {_projectPath} (will try fallback)");
                return new List<ClCommandLineEntry>();
            }

            var entries = new List<ClCommandLineEntry>();
            if (!buildResult.ResultsByTarget.TryGetValue("GetClCommandLines", out var targetResult))
                return entries;

            foreach (var item in targetResult.Items)
            {
                var files = item.GetMetadata("Files")
                    .Split(new []{';'}, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToArray();

                bool isConfigDefault = string.Equals(
                    item.GetMetadata("ConfigurationOptions"), "true",
                    StringComparison.OrdinalIgnoreCase);

                // FxCompile (HLSL) heuristic: the CLCommandLine task runs once for @(ClCompile)
                // and once for @(FxCompile), and both outputs land in the same @(ClCommandLines)
                // item group with no discriminator metadata.
                // Detect by file extensions: if every file in the Files list is an HLSL source,
                // treat the entry as FxCompile.
                var isFxCompile = files.Length > 0 && files.All(f =>
                {
                    var ext = Path.GetExtension(f);
                    return string.Equals(ext, ".hlsl", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ext, ".fx", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ext, ".hlsli", StringComparison.OrdinalIgnoreCase);
                });

                entries.Add(new ClCommandLineEntry(
                    CommandLine: item.ItemSpec,
                    WorkingDirectory: item.GetMetadata("WorkingDirectory"),
                    ToolPath: item.GetMetadata("ToolPath"),
                    Files: files,
                    IsConfigurationDefault: isConfigDefault,
                    IsFxCompile: isFxCompile
                ));
            }

            return entries;
        }

        /// <summary>
        /// Ensures metadata consistency across ClCompile items to prevent MSB4096 errors.
        /// The VC++ targets use unqualified %(PrecompiledHeaderFile) for batching, which
        /// fails when some items define it and others don't. Projects like Windows Calculator
        /// set PrecompiledHeader=Use in ItemDefinitionGroup but omit PrecompiledHeaderFile,
        /// while the PCH source file has PrecompiledHeader=Create set per-item.
        /// </summary>
        private static void EnsureClCompileMetadataConsistency(ProjectInstance projectInstance)
        {
            var clCompileItems = projectInstance.GetItems("ClCompile").ToList();
            if (clCompileItems.Count == 0) return;

            // Metadata keys that FixupCLCompileOptions batches on without qualification
            string[] metadataKeys = new[] { "PrecompiledHeaderFile", "PrecompiledHeaderOutputFile" };

            foreach (string key in metadataKeys)
            {
                bool anyHasValue = clCompileItems.Any(i => !string.IsNullOrEmpty(i.GetMetadataValue(key)));
                if (!anyHasValue) continue;

                // Some items have it, some don't. Set empty string on items missing it
                foreach (var item in clCompileItems)
                {
                    if (string.IsNullOrEmpty(item.GetMetadataValue(key)))
                    {
                        item.SetMetadata(key, "");
                    }
                }
            }
        }

        private static readonly HashSet<string> CppExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".cpp", ".cc", ".cxx", ".c", ".c++", ".cp" };

        // Extensions accepted by cl.exe/clang-cl as compilable source units
        private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".cpp", ".cc", ".cxx", ".c", ".c++", ".cp", ".ixx", ".cppm", ".mpp" };

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="path"/> has a file extension
        /// that a C/C++ compiler can compile. Used to filter stray entries (build directories,
        /// object files, PDB paths) from the <c>Files</c> metadata of <c>ClCommandLines</c>.
        /// </summary>
        private static bool IsCompilableSource(string path)
        {
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && SourceExtensions.Contains(ext);
        }

        /// <summary>
        /// Strips a single layer of surrounding double-quotes from a string.
        /// </summary>
        private static string StripOuterQuotes(string s)
        {
            return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"'
                ? s.Substring(1, s.Length - 2)
                : s;
        }

        /// <summary>
        /// Resolves any relative paths inside <c>/I</c> and <c>/external:I</c> arguments
        /// to absolute paths anchored at <paramref name="workingDir"/>.
        /// Paths that are already absolute are returned unchanged.
        /// Both the single-token form (<c>/Irelpath</c>) and the two-token form
        /// (<c>/I</c> + <c>relpath</c>) are handled.
        /// </summary>
        private static List<string> ResolveIncludePaths(List<string> args, string workingDir)
        {
            var result = new List<string>(args.Count);
            for (int i = 0; i < args.Count; i++)
            {
                var arg = args[i];

                // Single-token /Ipath form
                if (arg.StartsWith("/I", StringComparison.OrdinalIgnoreCase) && arg.Length > 2)
                {
                    result.Add(MakeAbsoluteInclude("/I", arg.Substring(2), workingDir));
                    continue;
                }

                // Two-token /I <path> form (uncommon but emitted by some CL tasks)
                if (arg.Equals("/I", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                {
                    string path = StripOuterQuotes(args[i + 1]);
                    result.Add(MakeAbsoluteInclude("/I", path, workingDir));
                    i++; // consume the path token
                    continue;
                }

                // Single-token /external:Ipath form (already merged upstream)
                if (arg.StartsWith("/external:I", StringComparison.OrdinalIgnoreCase) && arg.Length > 11)
                {
                    result.Add(MakeAbsoluteInclude("/external:I", arg.Substring(11), workingDir));
                    continue;
                }

                result.Add(arg);
            }
            return result;
        }

        private static string MakeAbsoluteInclude(string prefix, string rawPath, string workingDir)
        {
            string path = StripOuterQuotes(rawPath);
            if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path))
            {
                try { path = Path.GetFullPath(Path.Combine(workingDir, path)); }
                catch { /* leave as-is if resolution fails */ }
            }
            return prefix + path;
        }

        /// <summary>
        /// Strips build-output flags (<c>/Fo</c>, <c>/Fd</c>, <c>/errorReport</c>) from
        /// <paramref name="args"/> and also discards the immediately following token when
        /// the flag was emitted in two-token form (<c>/Fo</c> + <c>D:\obj\</c>).
        /// The <see cref="List{T}.RemoveAll"/> approach only removes the flag token,
        /// leaving stray path tokens that Clang then misinterprets as source files.
        /// </summary>
        private static List<string> StripOutputFlags(List<string> args)
        {
            var result = new List<string>(args.Count);
            for (int i = 0; i < args.Count; i++)
            {
                var a = args[i];

                bool isBareOutputFlag =
                    a.Equals("/Fo", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/Fd", StringComparison.OrdinalIgnoreCase);

                bool isCombinedOutputFlag =
                    (!isBareOutputFlag && a.StartsWith("/Fo", StringComparison.OrdinalIgnoreCase)) ||
                    (!isBareOutputFlag && a.StartsWith("/Fd", StringComparison.OrdinalIgnoreCase)) ||
                    a.StartsWith("/errorReport", StringComparison.OrdinalIgnoreCase);

                if (isCombinedOutputFlag)
                    continue; // path is embedded in the token itself — just skip

                if (isBareOutputFlag)
                {
                    // Skip the next token too when it is the path argument
                    // (doesn't start with a flag character, so it can't be a compiler switch)
                    if (i + 1 < args.Count &&
                        args[i + 1] is { Length: > 0 } next &&
                        next[0] != '/' && next[0] != '-' && next[0] != '@')
                    {
                        i++; // consume the stray path token
                    }
                    continue;
                }

                result.Add(a);
            }
            return result;
        }

        /// <summary>
        /// Fallback extraction for GN-generated projects (Chromium, Crashpad, WebRTC).
        /// These projects store IntelliSense settings in ItemDefinitionGroup/ClCompile
        /// and list source files as CustomBuild items that delegate compilation to ninja.
        /// Parses the vcxproj XML directly to avoid MSBuild evaluation issues with
        /// non-standard configurations (e.g., "GN" instead of "Debug").
        /// </summary>
        private List<CompileCommand> TryExtractFromItemDefinitionGroup()
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(_projectPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load project XML for fallback: {ex.Message}");
                return new List<CompileCommand>();
            }

            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var projectDir = Path.GetDirectoryName(_projectPath)!;

            // Find ClCompile settings in ItemDefinitionGroup
            var clCompileDefElement = doc.Root?
                .Elements(ns + "ItemDefinitionGroup")
                .Elements(ns + "ClCompile")
                .FirstOrDefault();

            if (clCompileDefElement == null)
                return new List<CompileCommand>();

            string GetElementValue(string name) =>
                clCompileDefElement.Element(ns + name)?.Value ?? "";

            string additionalIncludes = GetElementValue("AdditionalIncludeDirectories");
            string preprocessorDefs = GetElementValue("PreprocessorDefinitions");
            string additionalOptions = GetElementValue("AdditionalOptions");
            string languageStandard = GetElementValue("LanguageStandard");
            string disabledWarnings = GetElementValue("DisableSpecificWarnings");

            // Collect source files from CustomBuild items (GN pattern) or ClCompile items
            var sourceFiles = doc.Root?
                .Elements(ns + "ItemGroup")
                .Elements(ns + "CustomBuild")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null && CppExtensions.Contains(Path.GetExtension(v)))
                .Cast<string>()
                .ToList() ?? new List<string>();

            if (sourceFiles.Count == 0)
            {
                sourceFiles = doc.Root?
                    .Elements(ns + "ItemGroup")
                    .Elements(ns + "ClCompile")
                    .Select(e => e.Attribute("Include")?.Value)
                    .Where(v => v != null)
                    .Cast<string>()
                    .ToList() ?? new List<string>();
            }

            // Fallback: scan disk for source files when none found in project XML.
            // GN-generated projects sometimes have empty ItemGroups with source
            // files present on disk nearby (e.g., wrapper executables, header-only
            // libs with associated .cc files).
            if (sourceFiles.Count == 0)
            {
                sourceFiles = FindSourceFilesOnDisk(projectDir, doc, ns);
            }

            if (sourceFiles.Count == 0)
                return new List<CompileCommand>();

            // Build the compiler arguments from ItemDefinitionGroup metadata
            string toolPath = ResolveToolPath(VsWhereHelper.ResolveClExePath(_vcToolsInstallDir, _platform)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CL.exe"));

            var baseArgs = new List<string> { toolPath };

            string target = GetClangTargetTriple();
            if (target != null)
                baseArgs.Add($"--target={target}");
            baseArgs.Add("-ferror-limit=0");

            // Add /I from ExternalIncludePath and IncludePath properties
            foreach (string p in _externalIncludePaths)
                baseArgs.Add($"/I{p}");
            foreach (string p in _includePaths)
                baseArgs.Add($"/I{p}");

            // Add /I from AdditionalIncludeDirectories
            foreach (string inc in additionalIncludes.Split(new[]{';'}, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = inc.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("%(") || trimmed.StartsWith("$(")) continue;
                var resolved = Path.IsPathRooted(trimmed)
                    ? trimmed
                    : Path.GetFullPath(Path.Combine(projectDir, trimmed));
                baseArgs.Add($"/I{resolved}");
            }

            // Add /D from PreprocessorDefinitions
            foreach (string def in preprocessorDefs.Split(new[]{';'}, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = def.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("%(")) continue;
                baseArgs.Add($"/D{trimmed}");
            }

            // Add disabled warnings
            foreach (string w in disabledWarnings.Split(new[]{';'}, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = w.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("%(")) continue;
                baseArgs.Add($"/wd{trimmed}");
            }

            // Add language standard
            if (!string.IsNullOrEmpty(languageStandard) && !languageStandard.StartsWith("%("))
            {
                string stdFlag = languageStandard.ToLowerInvariant() switch
                {
                    "stdcpp14" => "/std:c++14",
                    "stdcpp17" => "/std:c++17",
                    "stdcpp20" => "/std:c++20",
                    "stdcpplatest" => "/std:c++latest",
                    "stdc11" => "/std:c11",
                    "stdc17" => "/std:c17",
                    _ => null
                };
                if (stdFlag != null)
                    baseArgs.Add(stdFlag);
            }

            // Add AdditionalOptions (raw flags, may contain /D, /std:, warnings, etc.)
            if (!string.IsNullOrEmpty(additionalOptions))
            {
                System.Collections.Generic.List<string> tokens = CommandLineTokenizer.TokenizeWithResponseFiles(
                    additionalOptions, projectDir);
                // Filter out %(AdditionalOptions) MSBuild inheritance marker
                baseArgs.AddRange(tokens.Where(t => !t.StartsWith("%(")));
            }

            // Merge two-arg /external:I flags into single token
            var mergedArgs2 = new List<string>();
            for (int i = 0; i < baseArgs.Count; i++)
            {
                if (i + 1 < baseArgs.Count &&
                    baseArgs[i].Equals("/external:I", StringComparison.OrdinalIgnoreCase))
                {
                    mergedArgs2.Add($"/external:I{baseArgs[i + 1]}");
                    i++;
                }
                else
                {
                    mergedArgs2.Add(baseArgs[i]);
                }
            }
            baseArgs = mergedArgs2;

            // Strip build-output flags (two-token-aware; see StripOutputFlags)
            baseArgs = StripOutputFlags(baseArgs);

            // Build compile commands for each source file
            var commands = new List<CompileCommand>();
            foreach (var file in sourceFiles)
            {
                var fullPath = Path.IsPathRooted(file)
                    ? file
                    : Path.GetFullPath(Path.Combine(projectDir, file));

                var fileArgs = new List<string>(baseArgs) { fullPath };
                string[] commandLine = MsvcSanitizer.Sanitize(
                    SanitizeFallbackPaths(fileArgs.ToArray(), _vcToolsInstallDir));

                commands.Add(new CompileCommand(
                    File: fullPath,
                    Arguments: commandLine,
                    Directory: projectDir,
                    ProjectPath: _projectPath,
                    ProjectName: _projectName,
                    Configuration: _configuration,
                    Platform: _platform
                ));
            }

            return commands;
        }

        /// <summary>
        /// Scans the disk for C/C++ source files when the project XML contains none.
        /// Tries: ClInclude/None item directories first, then the project directory itself.
        /// </summary>
        private static List<string> FindSourceFilesOnDisk(string projectDir, XDocument doc, XNamespace ns)
        {
            var files = new List<string>();

            // Collect directories from ClInclude and None items (header references)
            var headerPaths = doc.Root?
                .Elements(ns + "ItemGroup")
                .SelectMany(ig => ig.Elements(ns + "ClInclude").Concat(ig.Elements(ns + "None")))
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Cast<string>()
                .Select(v => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectDir, v))))
                .Where(d => d != null && Directory.Exists(d))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (headerPaths.Count > 0)
            {
                // Scan directories where headers were found for matching source files
                foreach (var dir in headerPaths)
                {
                    try
                    {
                        files.AddRange(Directory.EnumerateFiles(dir!, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(f => CppExtensions.Contains(Path.GetExtension(f))));
                    }
                    catch (Exception) { /* permission denied, etc. */ }
                }
            }
            else
            {
                // No header hints; scan the project directory (non-recursive to avoid
                // pulling in huge trees for build-output directories)
                try
                {
                    files.AddRange(Directory.EnumerateFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => CppExtensions.Contains(Path.GetExtension(f))));
                }
                catch (Exception) { /* permission denied, etc. */ }
            }

            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Creates a temporary .props file that defines default ItemDefinitionGroup metadata
        /// for ClCompile items, preventing MSB4096 errors from unqualified metadata batching
        /// in FixupCLCompileOptions. This works at the MSBuild XML import level.
        /// </summary>
        private static string? CreateFixupPropsFile()
        {
            try
            {
                var propsPath = Path.Combine(Path.GetTempPath(), $"msbuild_extractor_fixup_{Guid.NewGuid():N}.props");
                string propsContent = """
                    <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                      <ItemDefinitionGroup>
                        <ClCompile>
                          <PrecompiledHeaderFile Condition="'%(ClCompile.PrecompiledHeaderFile)' == ''">pch.h</PrecompiledHeaderFile>
                          <PrecompiledHeaderOutputFile Condition="'%(ClCompile.PrecompiledHeaderOutputFile)' == ''">$(IntDir)%(Filename).pch</PrecompiledHeaderOutputFile>
                        </ClCompile>
                      </ItemDefinitionGroup>
                    </Project>
                    """;
                File.WriteAllText(propsPath, propsContent);
                return propsPath;
            }
            catch
            {
                return null;
            }
        }

        private void CleanupFixupPropsFile()
        {
            if (_fixupPropsPath != null)
            {
                try { File.Delete(_fixupPropsPath); } catch { }
                _fixupPropsPath = null;
            }
        }

        private List<CompileCommand> ToCompileCommands(List<ClCommandLineEntry> entries)
        {
            var commands = new List<CompileCommand>();
            string[]? defaultArgs = null;

            foreach (var entry in entries)
            {
                // Drop FxCompile (HLSL) entries from standard compile_commands.json
                // output. They carry fxc.exe flags, not cl.exe flags, and clangd
                // cannot consume them. See ClCommandLineEntry.IsFxCompile.
                if (entry.IsFxCompile)
                    continue;

                string toolPath = ResolveToolPath(entry.ToolPath);
                var baseArgs = new List<string> { toolPath };

                // Inject --target so clangd uses the correct architecture triple.
                // cl.exe path encodes target arch (e.g. Hostx64\x86) but clangd
                // defaults to the host triple (x86_64) with --driver-mode=cl.
                string target = GetClangTargetTriple();
                if (target != null)
                    baseArgs.Add($"--target={target}");

                // Disable clang's error limit to prevent cascading failures.
                // MSVC STL headers can trigger errors that clang recovers from,
                // but the default limit (20) causes fatal_too_many_errors which
                // stops parsing and cascades into hundreds of false diagnostics.
                baseArgs.Add("-ferror-limit=0");

                // Add the GetClCommandLines output FIRST. It contains project-local
                // include paths that must come before system/external includes to match
                // the real build's resolution order.
                baseArgs.AddRange(CommandLineTokenizer.TokenizeWithResponseFiles(entry.CommandLine, entry.WorkingDirectory));

                // Then append IncludePath/ExternalIncludePath entries. These are
                // system-level paths (SDK, toolchain) that GetClCommandLines doesn't
                // emit but the compiler uses via the INCLUDE env var in real builds.
                foreach (string includePath in _externalIncludePaths)
                {
                    baseArgs.Add($"/I{includePath}");
                }

                foreach (string includePath in _includePaths)
                {
                    baseArgs.Add($"/I{includePath}");
                }

                // Merge two-arg /external:I flags into single token.
                // MSBuild emits "/external:I" "<path>" as separate tokens but
                // cl.exe also accepts the concatenated form "/external:I<path>".
                var mergedArgs = new List<string>();
                for (int i = 0; i < baseArgs.Count; i++)
                {
                    if (i + 1 < baseArgs.Count &&
                        baseArgs[i].Equals("/external:I", StringComparison.OrdinalIgnoreCase))
                    {
                        mergedArgs.Add($"/external:I{baseArgs[i + 1]}");
                        i++; // skip next
                    }
                    else
                    {
                        mergedArgs.Add(baseArgs[i]);
                    }
                }
                baseArgs = mergedArgs;

                // Strip build-output flags. Uses a sequential scan rather than RemoveAll so
                // that the path token following a bare /Fo or /Fd is also discarded (see
                // StripOutputFlags). Leaving that stray path in baseArgs causes Clang to
                // treat it as an extra source-file argument.
                baseArgs = StripOutputFlags(baseArgs);

                // Resolve relative /I and /external:I paths to absolute.
                // GetClCommandLines emits paths relative to the project working directory;
                // Clang cannot resolve them without the correct base when it parses
                // compile_commands.json from an arbitrary directory.
                baseArgs = ResolveIncludePaths(baseArgs, entry.WorkingDirectory);

                // Inject VC_IncludePath / WindowsSDK_IncludePath via /imsvc so clang-cl
                // treats MSVC and Windows SDK headers as system headers (suppresses warnings).
                // /imsvc is the clang-cl equivalent of GCC -isystem; -isystem is silently
                // ignored in --driver-mode=cl and must not be used.
                foreach (string sysPath in _systemIncludePaths)
                {
                    baseArgs.Add("/imsvc");
                    baseArgs.Add(sysPath);
                }

                foreach (var file in entry.Files)
                {
                    // Filter entries that are not compilable source files (e.g. build output
                    // directories, object files, or paths with no extension that MSBuild
                    // sometimes leaks into the Files metadata of ClCommandLines items).
                    // __temporary.cpp has a valid .cpp extension and passes this check.
                    if (!IsCompilableSource(file))
                    {
                        Console.Error.WriteLine($"Info: Skipping non-source entry in ClCommandLines.Files: {file}");
                        continue;
                    }

                    bool isTemporaryCpp = string.Equals(Path.GetFileName(file), "__temporary.cpp", StringComparison.OrdinalIgnoreCase);

                    if (isTemporaryCpp)
                    {
                        if (_emitDefaults)
                        {
                            // Emit with a synthetic filename so consumers can inspect
                            // the project-wide baseline.
                            var defaultsFile = Path.Combine(entry.WorkingDirectory, "__project_defaults.cpp");
                            var defaultsArgs = new List<string>(baseArgs) { defaultsFile };
                            string[] sanitized = MsvcSanitizer.Sanitize(
                                SanitizeFallbackPaths(defaultsArgs.ToArray(), _vcToolsInstallDir));
                            commands.Add(new CompileCommand(
                                File: defaultsFile,
                                Arguments: sanitized,
                                Directory: entry.WorkingDirectory,
                                ProjectPath: _projectPath,
                                ProjectName: _projectName,
                                Configuration: _configuration,
                                Platform: _platform
                            ));
                        }
                        if (_mergeDefaults)
                        {
                            // Capture the default args (without the source file) for
                            // merging into per-file entries below.
                            defaultArgs = baseArgs.ToArray();
                        }
                        continue;
                    }

                    var fileArgs = new List<string>(baseArgs) { file };

                    // Backfill flags from the project-wide default command line
                    // that this per-file entry doesn't already carry.
                    // Per-file entries omit anything inherited unchanged from ItemDefinitionGroup,
                    // typically defines, language standard, and warning level.
                    // Without this merge, clangd would see a partial command
                    // and miss most of the project's compilation context.
                    // The defaults come from the ConfigurationOptions=true entry,
                    // captured above when --merge-defaults is active.
                    if (_mergeDefaults && defaultArgs != null)
                        MergeDefaultFlags(fileArgs, defaultArgs);

                    // Resolve fallback paths then sanitize for Clang libtooling compatibility
                    string[] commandLine = MsvcSanitizer.Sanitize(
                        SanitizeFallbackPaths(fileArgs.ToArray(), _vcToolsInstallDir));

                    commands.Add(new CompileCommand(
                        File: file,
                        Arguments: commandLine,
                        Directory: entry.WorkingDirectory,
                        ProjectPath: _projectPath,
                        ProjectName: _projectName,
                        Configuration: _configuration,
                        Platform: _platform
                    ));
                }
            }

            return commands;
        }

        /// <summary>
        /// Maps the MSBuild Platform to a clang target triple so that clangd
        /// compiles with the correct pointer size, predefined macros (_WIN64,
        /// _M_X64, etc.), and ABI. Without this, Win32 projects that define
        /// _USE_32BIT_TIME_T break because clangd defaults to x86_64.
        /// </summary>
        private string? GetClangTargetTriple()
        {
            return _platform.ToLowerInvariant() switch
            {
                "win32" or "x86" => "i686-pc-windows-msvc",
                "x64" or "x86_64" or "amd64" => "x86_64-pc-windows-msvc",
                "arm" => "thumbv7-pc-windows-msvc",
                "arm64" or "aarch64" => "aarch64-pc-windows-msvc",
                _ => null // Let clangd infer from cl.exe path
            };
        }

        private string ResolveToolPath(string toolPath)
        {
            if (0<=toolPath.IndexOf("system32", StringComparison.OrdinalIgnoreCase) &&
                toolPath.EndsWith("CL.exe", StringComparison.OrdinalIgnoreCase))
            {
                string resolved = VsWhereHelper.ResolveClExePath(_vcToolsInstallDir, _platform);
                if (resolved != null)
                    return resolved;
            }
            return toolPath;
        }

        public static List<CompileCommand> ExtractCompileCommandsFromSolution(
            string solutionPath, string configuration = "Debug", string platform = "x64",
            bool enableLogger = false, string? vcToolsInstallDir = null,
            bool emitDefaults = false, bool mergeDefaults = false,
            ExtractorCache? cache = null)
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
            var projects = ProjectDiscovery.GetVcProjectsFromSolution(solutionPath);
            var allCommands = new List<CompileCommand>();

            foreach (var project in projects)
            {
                try
                {
                    // Return cached result when the .vcxproj file is unchanged
                    if (cache != null &&
                        cache.TryGetCachedCommands(project.Path, configuration, platform, out List<CompileCommand>? cached))
                    {
                        if (enableLogger)
                            Console.Error.WriteLine($"Info: Cache hit for {Path.GetFileName(project.Path)}");
                        allCommands.AddRange(cached!);
                        continue;
                    }

                    InProcessExtractor extractor = new InProcessExtractor(
                        project.Path, configuration, platform, enableLogger, solutionDir, vcToolsInstallDir,
                        emitDefaults, mergeDefaults);
                    var commands = extractor.ExtractCompileCommands();
                    cache?.CacheCommands(project.Path, configuration, platform, commands);
                    allCommands.AddRange(commands);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to extract compile commands from {project.Path}: {ex.Message}");
                }
            }

            return allCommands;
        }

        public static void WriteCompileCommandsJsonFromSolution(
            string solutionPath, string configuration = "Debug", string platform = "x64",
            bool enableLogger = false, string? outputPath = null, string? vcToolsInstallDir = null)
        {
            var commands = ExtractCompileCommandsFromSolution(solutionPath, configuration, platform, enableLogger, vcToolsInstallDir);
            outputPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, "compile_commands.json");

            var json = JsonSerializer.Serialize(commands, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            File.WriteAllText(outputPath, json);
        }
    }
}
