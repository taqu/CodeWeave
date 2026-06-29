using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Markup;

namespace MSBuild.CompileCommands.Extractor
{
    /// <summary>
    /// Rich compile database format, a hierarchical alternative to compile_commands.json.
    /// Organizes compile commands by solution → project → configuration → file,
    /// with structured fields for includes, defines, and language standard.
    /// </summary>
    public static class RichDatabase
    {
        public const int SchemaVersion = 1;

        public record Root(
            int Version,
            string Generator,
            string GeneratedAt,
            FingerprintInfo Fingerprint,
            ToolchainInfo? Toolchain,
            List<SolutionEntry> Solutions,
            List<ProjectEntry> StandaloneProjects
        );

        public record FingerprintInfo(
            string Tool,
            string ToolVersion,
            int SchemaVersion,
            string Checksum
        );

        public record ToolchainInfo(
            string? Compiler,
            string? CompilerVersion,
            string? VcToolsInstallDir,
            string? WindowsSdkVersion
        );

        public record SolutionEntry(
            string Path,
            List<ProjectEntry> Projects
        );

        public record ProjectEntry(
            string Path,
            string Name,
            List<ConfigurationEntry> Configurations
        );

        public record ConfigurationEntry(
            string Configuration,
            string Platform,
            List<RichCompileCommand> CompileCommands
        );

        public record RichCompileCommand(
            string File,
            string Directory,
            List<string> Includes,
            List<string> SystemIncludes,
            List<string> Defines,
            string? StandardVersion,
            List<string> AdditionalFlags,
            string[] Arguments
        );

        /// <summary>
        /// Build a rich database from a list of CompileCommand entries and metadata.
        /// </summary>
        public static Root Build(
            List<CompileCommand> commands,
            CommandLineOptions options)
        {
            ToolchainInfo toolchain = BuildToolchainInfo(commands, options);

            // Group commands: first by solution path (if known), then by project, then by config|platform
            Dictionary<string, List<CompileCommand>> solutionGroups = new Dictionary<string, List<CompileCommand>>(StringComparer.OrdinalIgnoreCase);
            List<CompileCommand> standaloneCommands = new List<CompileCommand>();

            // Determine which solution each command belongs to
            Dictionary<string, string> projectToSolution = MapProjectsToSolutions(options);

            foreach (CompileCommand cmd in commands)
            {
                string? solutionPath = null;
                if (cmd.ProjectPath != null)
                    projectToSolution.TryGetValue(NormalizePath(cmd.ProjectPath), out solutionPath);

                if (solutionPath != null)
                {
                    if (!solutionGroups.ContainsKey(solutionPath))
                        solutionGroups[solutionPath] = [];
                    solutionGroups[solutionPath].Add(cmd);
                }
                else
                {
                    standaloneCommands.Add(cmd);
                }
            }

            List<SolutionEntry> solutions = solutionGroups.Select(kvp =>
                new SolutionEntry(kvp.Key, BuildProjectEntries(kvp.Value))).ToList();

            List<ProjectEntry> standaloneProjects = BuildProjectEntries(standaloneCommands);

            FingerprintInfo fingerprint = BuildFingerprint(commands);

            return new Root(
                Version: SchemaVersion,
                Generator: "msbuild-extractor",
                GeneratedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Fingerprint: fingerprint,
                Toolchain: toolchain,
                Solutions: solutions,
                StandaloneProjects: standaloneProjects
            );
        }

        private static Dictionary<string, string> MapProjectsToSolutions(CommandLineOptions options)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sln in options.Solutions)
            {
                try
                {
                    var projects = ProjectDiscovery.GetVcProjectsFromSolution(sln);
                    foreach (var proj in projects)
                        map[NormalizePath(proj.Path)] = System.IO.Path.GetFullPath(sln);
                }
                catch { /* solution parsing might fail; skip */ }
            }
            return map;
        }

        private static List<ProjectEntry> BuildProjectEntries(List<CompileCommand> commands)
        {
            IOrderedEnumerable<IGrouping<string, CompileCommand>> byProject = commands
                .GroupBy(c => NormalizePath(c.ProjectPath ?? "unknown"))
                .OrderBy(g => g.Key);

            List<ProjectEntry> projects = new List<ProjectEntry>();
            foreach (IGrouping<string, CompileCommand> projGroup in byProject)
            {
                CompileCommand first = projGroup.First();
                string projectName = first.ProjectName ?? Path.GetFileNameWithoutExtension(projGroup.Key);

                IOrderedEnumerable<IGrouping<(string, string), CompileCommand>> byConfig = projGroup
                    .GroupBy(c => (c.Configuration ?? "Unknown", c.Platform ?? "Unknown"))
                    .OrderBy(g => g.Key);

                List<ConfigurationEntry> configs = new List<ConfigurationEntry>();
                foreach (IGrouping<(string, string), CompileCommand> configGroup in byConfig)
                {
                    List<RichCompileCommand> richCommands = configGroup.Select(c => ParseToRichCommand(c)).ToList();
                    configs.Add(new ConfigurationEntry(
                        Configuration: configGroup.Key.Item1,
                        Platform: configGroup.Key.Item2,
                        CompileCommands: richCommands
                    ));
                }

                projects.Add(new ProjectEntry(
                    Path: projGroup.Key,
                    Name: projectName,
                    Configurations: configs
                ));
            }
            return projects;
        }

        /// <summary>
        /// Parse a flat CompileCommand into a structured RichCompileCommand by
        /// extracting includes, defines, standard version, and flags from arguments.
        /// </summary>
        private static RichCompileCommand ParseToRichCommand(CompileCommand cmd)
        {
            List<string> includes = new List<string>();
            List<string> systemIncludes = new List<string>();
            List<string> defines = new List<string>();
            string? standardVersion = null;
            List<string> additionalFlags = new List<string>();
            string[] args = cmd.Arguments;

            // Skip argv[0] (compiler path) and the source file (last arg, or matching cmd.File)
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];

                // Source file, skip
                if (IsSourceFile(arg, cmd.File)) {
                    continue;
                }
                ReadOnlySpan<char> argSpan = arg.AsSpan();

                // /I<path> or /I <path>, user include
                if (arg.StartsWith("/I", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-I", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> pathSpan = argSpan.Slice(2);
                    string path = string.Empty;
                    if (pathSpan.IsEmpty && i + 1 < args.Length) {
                        path = args[++i];
                    }
                    else
                    {
                        path = pathSpan.ToString();
                    }
                    if (!string.IsNullOrEmpty(path)) {
                        includes.Add(path);
                    }
                    continue;
                }

                // /external:I<path>, system/external include
                if (arg.StartsWith("/external:I", StringComparison.OrdinalIgnoreCase))
                {
                    string path = arg.Substring("/external:I".Length);
                    if (!string.IsNullOrEmpty(path))
                        systemIncludes.Add(path);
                    continue;
                }

                // /D<name>[=<value>] or /D <name>
                if (arg.StartsWith("/D", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-D", StringComparison.OrdinalIgnoreCase))
                {
                    string def = arg.Substring(2);
                    if (string.IsNullOrEmpty(def) && i + 1 < args.Length)
                        def = args[++i];
                    if (!string.IsNullOrEmpty(def))
                        defines.Add(def);
                    continue;
                }

                // /std:c++<ver> or /std:c<ver>
                if (arg.StartsWith("/std:", StringComparison.OrdinalIgnoreCase))
                {
                    standardVersion = arg.Substring("/std:".Length);
                    continue;
                }

                // /c, compile only, skip (it's implicit)
                if (arg.Equals("/c", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // Everything else is an additional flag
                additionalFlags.Add(arg);
            }

            return new RichCompileCommand(
                File: cmd.File,
                Directory: cmd.Directory,
                Includes: includes,
                SystemIncludes: systemIncludes,
                Defines: defines,
                StandardVersion: standardVersion,
                AdditionalFlags: additionalFlags,
                Arguments: cmd.Arguments
            );
        }

        private static bool IsSourceFile(string arg, string commandFile)
        {
            if (arg.StartsWith("/") || arg.StartsWith("-"))
                return false;
            // Match if it's the same file path
            try
            {
                return string.Equals(
                    Path.GetFullPath(arg),
                    Path.GetFullPath(commandFile),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(arg, commandFile, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static ToolchainInfo? BuildToolchainInfo(List<CompileCommand> commands, CommandLineOptions options)
        {
            // Try to find compiler path from first command's arguments
            string? compiler = null;
            string? compilerVersion = null;

            if (commands.Count > 0 && commands[0].Arguments.Length > 0)
                compiler = commands[0].Arguments[0];

            // Try to extract version from VcToolsInstallDir path (e.g. ...\\MSVC\\14.50.35717\\)
            string vcToolsDir = options.VcToolsInstallDir;
            if (vcToolsDir != null)
            {
                Match match = Regex.Match(vcToolsDir, @"[\\/](\d+\.\d+\.\d+)[\\/]?$");
                if (match.Success)
                    compilerVersion = match.Groups[1].Value;
            }

            // Try to find Windows SDK version from system include paths
            string? windowsSdkVersion = null;
            if (commands.Count > 0)
            {
                foreach (string arg in commands[0].Arguments)
                {
                    Match sdkMatch = Regex.Match(arg, @"Windows Kits[\\/]\d+[\\/]Include[\\/](\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
                    if (sdkMatch.Success)
                    {
                        windowsSdkVersion = sdkMatch.Groups[1].Value;
                        break;
                    }
                }
            }

            if (compiler == null && vcToolsDir == null)
                return null;

            return new ToolchainInfo(
                Compiler: compiler,
                CompilerVersion: compilerVersion,
                VcToolsInstallDir: vcToolsDir,
                WindowsSdkVersion: windowsSdkVersion
            );
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static FingerprintInfo BuildFingerprint(List<CompileCommand> commands)
        {
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            using MemoryStream stream = new MemoryStream();
            using StreamWriter writer = new StreamWriter(stream);

            foreach (CompileCommand cmd in commands.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase))
            {
                writer.Write(cmd.File);
                writer.Write('\0');
                foreach (string arg in cmd.Arguments)
                {
                    writer.Write(arg);
                    writer.Write('\0');
                }
                writer.Write('\n');
            }
            writer.Flush();
            stream.Position = 0;

            byte[] hash = sha.ComputeHash(stream);
            string checksum = "sha256:" + BitConverter.ToString(hash).Replace("-", "").ToLower();

            return new FingerprintInfo(
                Tool: "msbuild-extractor",
                ToolVersion: "1.0.0",
                SchemaVersion: SchemaVersion,
                Checksum: checksum
            );
        }

        /// <summary>
        /// Serialize a Root to JSON string.
        /// </summary>
        public static string Serialize(Root root)
        {
            return JsonSerializer.Serialize(root, SerializerOptions);
        }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}

