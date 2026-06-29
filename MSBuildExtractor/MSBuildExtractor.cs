using Microsoft.Build.Locator;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MSBuild.CompileCommands.Extractor
{
    public class MSBuildExtractor
    {
        public int Run(string[] args)
        {
            CommandLineOptions options = CommandLineOptions.Parse(args);

            if (options.ListInstances)
            {
                var instances = VsWhereHelper.ListInstances();
                if (instances.Count == 0)
                {
                    Console.WriteLine("No Visual Studio installations found.");
                    return 0;
                }
                Console.WriteLine("Visual Studio Installations:");
                Console.WriteLine();
                foreach (var inst in instances)
                {
                    Console.WriteLine($"  ID:       {inst.InstanceId}");
                    Console.WriteLine($"  Name:     {inst.DisplayName}");
                    Console.WriteLine($"  Path:     {inst.InstallationPath}");
                    Console.WriteLine($"  Version:  {inst.Version}");
                    Console.WriteLine($"  VC Tools: {(inst.HasVcTools ? "Yes" : "No")}");
                    if (inst.VCTargetsPath != null)
                        Console.WriteLine($"  VCTargets: {inst.VCTargetsPath}");
                    if (inst.VCToolsInstallDir != null)
                        Console.WriteLine($"  VCTools:   {inst.VCToolsInstallDir}");
                    Console.WriteLine();
                }
                return 0;
            }

            if (options.VsInstance != null)
            {
                var instances = VsWhereHelper.ListInstances(options.EnableLogger);
                var selected = instances.FirstOrDefault(i =>
                    i.InstanceId.Equals(options.VsInstance, StringComparison.OrdinalIgnoreCase) ||
                    i.InstanceId.StartsWith(options.VsInstance, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    Console.Error.WriteLine($"Error: VS instance '{options.VsInstance}' not found. Use --list-instances.");
                    return 1;
                }
                options.VsPath = selected.InstallationPath;
                if (options.VcTargetsPath == null) options.VcTargetsPath = selected.VCTargetsPath;
                if (options.VcToolsInstallDir == null) options.VcToolsInstallDir = selected.VCToolsInstallDir;
            }

            if (options.UseDevEnv)
            {
                DevEnvReader devEnv = new DevEnvReader();
                if (options.EnableLogger)
                    devEnv.PrintDiagnostics();

                if (!devEnv.IsDevEnvAvailable)
                {
                    Console.Error.WriteLine("Error: --use-dev-env specified but no Developer Command Prompt environment detected.");
                    Console.Error.WriteLine("Run this tool from a Developer Command Prompt or Developer PowerShell.");
                    return 1;
                }

                if (options.VcTargetsPath == null && devEnv.VCTargetsPath != null)
                    options.VcTargetsPath = devEnv.VCTargetsPath;
                if (options.VcToolsInstallDir == null && devEnv.VCToolsInstallDir != null)
                    options.VcToolsInstallDir = devEnv.VCToolsInstallDir;

                devEnv.ApplyToEnvironment();
            }

            if (options.VcTargetsPath == null || options.VcToolsInstallDir == null)
            {
                VsWhereResult vsResult = VsWhereHelper.DetectVisualStudio(options.EnableLogger);
                if (vsResult != null)
                {
                    if (options.VcTargetsPath == null && vsResult.VCTargetsPath != null)
                        options.VcTargetsPath = vsResult.VCTargetsPath;
                    if (options.VcToolsInstallDir == null && vsResult.VCToolsInstallDir != null)
                        options.VcToolsInstallDir = vsResult.VCToolsInstallDir;
                    if (options.VsPath == null && vsResult.InstallationPath != null)
                        options.VsPath = vsResult.InstallationPath;
                }
            }

            bool isOutOfProcess = options.MsBuildPath != null;

            if (options.EnableLogger)
                Console.WriteLine(isOutOfProcess
                    ? $"Mode: Out-of-process (MSBuild: {options.MsBuildPath})"
                    : "Mode: In-process");

            RegisterMSBuild(options.VsPath, options.EnableLogger);
            SetVcTargetsPath(options.VcTargetsPath);

            if (!options.AllConfigurations)
            {
                int validateResult = ValidateConfigurations(options);
                if (validateResult != 0) return validateResult;
            }

            bool isMultiInput = options.Solutions.Length + options.Projects.Length > 1;

            if (options.AllConfigurations)
            {
                return RunAllConfigurations(options);
            }

            ExtractorCache? cache = null;
            try
            {
                if (options.UseCache)
                {
                    var cacheDir = options.SplitByProject && options.Output != null
                        ? Path.GetFullPath(options.Output)
                        : GetOutputDirectory(options);
                    cache = new ExtractorCache(cacheDir);
                }

                List<CompileCommand>? commands = null;
                if (isMultiInput)
                    commands = ExtractMultipleInputs(options, cache);
                else if (isOutOfProcess)
                    commands = ExtractOutOfProcess(options);
                else
                    commands = ExtractInProcess(options, cache);

                if (commands != null)
                {
                    if (options.Deduplicate)
                    {
                        int beforeCount = commands.Count;
                        commands = CompileCommandDeduplicator.Deduplicate(
                            commands, options.PreferConfiguration, options.PreferPlatform);
                        Console.WriteLine($"Deduplicated: {beforeCount} entries → {commands.Count} unique files");
                    }

                    if (options.SplitByProject)
                    {
                        var splitDir = options.Output != null
                            ? Path.GetFullPath(options.Output)
                            : GetOutputDirectory(options);
                        WriteSplitProjectJson(commands, splitDir, options);
                    }
                    else
                    {
                        string outputPath = options.Output ?? GetDefaultOutputPath(options);
                        WriteJson(commands, outputPath, options: options);
                        Console.WriteLine($"Wrote {commands.Count} entries to {outputPath}");
                        if (options.EmitCCppProperties)
                        {
                            var baseDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
                            VsCodeSettings.GenerateCCppProperties(outputPath, options.Platform, baseDir);
                        }
                        if (options.Validate)
                            ClExeValidator.Run(commands);
                    }

                    cache?.Save();
                }
            }
            finally
            {
                cache?.Dispose();
            }

            return 0;
        }

        int ValidateConfigurations(CommandLineOptions options)
        {
            foreach (string sln in options.Solutions)
            {
                var projects = ProjectDiscovery.GetVcProjectsFromSolution(sln);
                var errors = new List<string>();

                foreach (var project in projects)
                {
                    if (!project.HasConfigurationPlatform(options.Configuration, options.Platform))
                    {
                        string available = string.Join(", ", project.ConfigurationPlatforms.Select(cp => cp.ToString()));
                        errors.Add($"Configuration '{options.Configuration}|{options.Platform}' not found in {project.Name}. Available: {available}");
                    }
                }

                if (errors.Count > 0)
                {
                    foreach (var error in errors)
                        Console.Error.WriteLine(options.Strict ? $"Error: {error}" : $"Warning: {error}");

                    if (options.Strict)
                    {
                        Console.Error.WriteLine($"Aborting: {errors.Count} project(s) do not support the requested configuration.");
                        return 1;
                    }
                }
            }

            foreach (string proj in options.Projects)
            {
                string validationError = ProjectDiscovery.ValidateConfigurationPlatform(
                    proj, options.Configuration, options.Platform);
                if (validationError != null)
                {
                    if (options.Strict)
                    {
                        Console.Error.WriteLine($"Error: {validationError}");
                        return 1;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: {validationError}");
                    }
                }
            }

            return 0;
        }

        int RunAllConfigurations(CommandLineOptions options)
        {
            var configPlatforms = DiscoverAllConfigurations(options);
            if (configPlatforms.Count == 0)
            {
                Console.Error.WriteLine("Error: No configuration/platform pairs found.");
                return 1;
            }

            Console.WriteLine($"Extracting for {configPlatforms.Count} configuration(s): {string.Join(", ", configPlatforms)}");

            bool isOutOfProcess = options.MsBuildPath != null;
            var allCommands = new List<CompileCommand>();
            string savedConfig = options.Configuration;
            string savedPlatform = options.Platform;

            foreach (var cp in configPlatforms)
            {
                Console.WriteLine($"  Extracting: {cp}...");
                options.Configuration = cp.Configuration;
                options.Platform = cp.Platform;

                try
                {
                    bool isMultiInput = options.Solutions.Length + options.Projects.Length > 1;
                    List<CompileCommand> commands;
                    if (isMultiInput)
                        commands = ExtractMultipleInputs(options);
                    else if (isOutOfProcess)
                        commands = ExtractOutOfProcess(options);
                    else
                        commands = ExtractInProcess(options);

                    if (options.Merge)
                    {
                        allCommands.AddRange(commands);
                    }
                    else
                    {
                        string outputDir = GetOutputDirectory(options);
                        var outputPath = Path.Combine(outputDir, $"compile_commands_{cp.Configuration}_{cp.Platform}.json");
                        WriteJson(commands, outputPath, options: options);
                        Console.WriteLine($"    Wrote {commands.Count} entries to {outputPath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    Warning: Failed for {cp}: {ex.Message}");
                }
            }

            options.Configuration = savedConfig;
            options.Platform = savedPlatform;

            if (options.Merge)
            {
                if (options.Deduplicate)
                {
                    int beforeCount = allCommands.Count;
                    allCommands = CompileCommandDeduplicator.Deduplicate(
                        allCommands, options.PreferConfiguration, options.PreferPlatform);
                    Console.WriteLine($"Deduplicated: {beforeCount} entries → {allCommands.Count} unique files");
                }
                var outputPath = options.Output ?? Path.Combine(GetOutputDirectory(options), "compile_commands.json");
                WriteJson(allCommands, outputPath, includeMetadata: !options.Deduplicate, options: options);
                Console.WriteLine($"Wrote {allCommands.Count} entries{(options.Deduplicate ? " (deduplicated)" : " (merged)")} to {outputPath}");
                if (options.EmitCCppProperties)
                {
                    var baseDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
                    VsCodeSettings.GenerateCCppProperties(outputPath, options.Platform, baseDir);
                }
            }

            return 0;
        }

        static List<ConfigurationPlatform> DiscoverAllConfigurations(CommandLineOptions options)
        {
            var all = new List<ConfigurationPlatform>();

            foreach (string sln in options.Solutions)
                all.AddRange(ProjectDiscovery.GetVcProjectsFromSolution(sln).SelectMany(p => p.ConfigurationPlatforms));

            foreach (string proj in options.Projects)
                all.AddRange(ProjectDiscovery.GetProjectConfigurations(proj));

            return all.Distinct().ToList();
        }

        static string GetOutputDirectory(CommandLineOptions options)
        {
            if (options.Output != null)
                return Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".";
            var inputPath = options.Solutions.FirstOrDefault() ?? options.Projects.First();
            return Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
        }

        static List<CompileCommand> ExtractInProcess(CommandLineOptions options, ExtractorCache? cache = null)
        {
            if (options.Solutions.Length > 0 && options.Solution != null)
            {
                return InProcessExtractor.ExtractCompileCommandsFromSolution(
                    options.Solution, options.Configuration, options.Platform,
                    options.EnableLogger, options.VcToolsInstallDir,
                    options.EmitDefaults, options.MergeDefaults, cache);
            }
            else
            {
                string projectPath = options.Projects[0];

                if (cache != null &&
                    cache.TryGetCachedCommands(projectPath, options.Configuration, options.Platform, out List<CompileCommand>? cached))
                {
                    if (options.EnableLogger)
                        Console.WriteLine($"Cache hit: {Path.GetFileName(projectPath)}");
                    return cached!;
                }

                InProcessExtractor extractor = new InProcessExtractor(
                    projectPath, options.Configuration, options.Platform,
                    options.EnableLogger, options.SolutionDir, options.VcToolsInstallDir,
                    options.EmitDefaults, options.MergeDefaults);
                var commands = extractor.ExtractCompileCommands();
                cache?.CacheCommands(projectPath, options.Configuration, options.Platform, commands);
                return commands;
            }
        }

        static List<CompileCommand> ExtractOutOfProcess(CommandLineOptions options)
        {
            if (options.Solutions.Length > 0 && options.Solution != null)
            {
                return OutOfProcessExtractor.ExtractCompileCommandsFromSolution(
                    options.MsBuildPath!, options.Solution, options.Configuration, options.Platform,
                    options.EnableLogger, options.VcToolsInstallDir, options.VcTargetsPath,
                    emitDefaults: options.EmitDefaults, mergeDefaults: options.MergeDefaults);
            }
            else
            {
                OutOfProcessExtractor extractor = new OutOfProcessExtractor(
                    options.MsBuildPath!, options.Projects[0], options.Configuration, options.Platform,
                    options.EnableLogger, options.SolutionDir, options.VcToolsInstallDir, options.VcTargetsPath,
                    options.ClPath,
                    msbuildProperties: options.MsBuildProperties,
                    msbuildEnv: options.MsBuildEnv,
                    launcher: ParseLauncher(options.MsBuildLauncher),
                    includePathOrder: ParseIncludePathOrder(options.IncludePathOrder),
                    emitDefaults: options.EmitDefaults,
                    mergeDefaults: options.MergeDefaults);
                return extractor.ExtractCompileCommands();
            }
        }

        static List<CompileCommand> ExtractMultipleInputs(CommandLineOptions options, ExtractorCache? cache = null)
        {
            bool isOutOfProcess = options.MsBuildPath != null;
            var allCommands = new List<CompileCommand>();

            foreach (string sln in options.Solutions)
            {
                Console.WriteLine($"Extracting from solution: {Path.GetFileName(sln)}...");
                try
                {
                    List<CompileCommand> commands;
                    if (isOutOfProcess)
                        commands = OutOfProcessExtractor.ExtractCompileCommandsFromSolution(
                            options.MsBuildPath!, sln, options.Configuration, options.Platform,
                            options.EnableLogger, options.VcToolsInstallDir, options.VcTargetsPath,
                            msbuildProperties: options.MsBuildProperties,
                            msbuildEnv: options.MsBuildEnv,
                            launcher: ParseLauncher(options.MsBuildLauncher),
                            includePathOrder: ParseIncludePathOrder(options.IncludePathOrder),
                            emitDefaults: options.EmitDefaults,
                            mergeDefaults: options.MergeDefaults);
                    else
                        commands = InProcessExtractor.ExtractCompileCommandsFromSolution(
                            sln, options.Configuration, options.Platform,
                            options.EnableLogger, options.VcToolsInstallDir,
                            options.EmitDefaults, options.MergeDefaults, cache);
                    Console.WriteLine($"  Got {commands.Count} entries");
                    allCommands.AddRange(commands);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Failed for {sln}: {ex.Message}");
                }
            }

            foreach (string proj in options.Projects)
            {
                Console.WriteLine($"Extracting from project: {Path.GetFileName(proj)}...");
                try
                {
                    List<CompileCommand> commands;
                    if (isOutOfProcess)
                    {
                        OutOfProcessExtractor extractor = new OutOfProcessExtractor(
                            options.MsBuildPath!, proj, options.Configuration, options.Platform,
                            options.EnableLogger, options.SolutionDir, options.VcToolsInstallDir, options.VcTargetsPath,
                            options.ClPath,
                            msbuildProperties: options.MsBuildProperties,
                            msbuildEnv: options.MsBuildEnv,
                            launcher: ParseLauncher(options.MsBuildLauncher),
                            includePathOrder: ParseIncludePathOrder(options.IncludePathOrder),
                            emitDefaults: options.EmitDefaults,
                            mergeDefaults: options.MergeDefaults);
                        commands = extractor.ExtractCompileCommands();
                    }
                    else
                    {
                        if (cache != null &&
                            cache.TryGetCachedCommands(proj, options.Configuration, options.Platform, out List<CompileCommand>? cached))
                        {
                            if (options.EnableLogger)
                                Console.WriteLine($"  Cache hit: {Path.GetFileName(proj)}");
                            allCommands.AddRange(cached!);
                            continue;
                        }

                        InProcessExtractor extractor = new InProcessExtractor(
                            proj, options.Configuration, options.Platform,
                            options.EnableLogger, options.SolutionDir, options.VcToolsInstallDir,
                            options.EmitDefaults, options.MergeDefaults);
                        commands = extractor.ExtractCompileCommands();
                        cache?.CacheCommands(proj, options.Configuration, options.Platform, commands);
                    }
                    Console.WriteLine($"  Got {commands.Count} entries");
                    allCommands.AddRange(commands);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Failed for {proj}: {ex.Message}");
                }
            }

            return allCommands;
        }

        static void WriteSplitProjectJson(List<CompileCommand> commands, string outputDir, CommandLineOptions options)
        {
            Directory.CreateDirectory(outputDir);

            var grouped = commands
                .GroupBy(c => c.ProjectName ?? "_unknown", StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in grouped)
            {
                string safeName = SanitizeFileName(group.Key);
                string outputPath = Path.Combine(outputDir, $"compile_commands_{safeName}.json");
                List<CompileCommand> projectCommands = group.ToList();
                WriteJson(projectCommands, outputPath, options: options);
                Console.WriteLine($"  Wrote {projectCommands.Count} entries to {outputPath}");
            }
            Console.WriteLine($"Split into {grouped.Count} project file(s) in {outputDir}");
        }

        static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        }

        static void WriteJson(List<CompileCommand> commands, string outputPath, bool includeMetadata = false,
            CommandLineOptions? options = null)
        {
            if (options?.Format == "rich")
            {
                RichDatabase.Root root = RichDatabase.Build(commands, options);
                File.WriteAllText(outputPath, RichDatabase.Serialize(root));
                return;
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
            var sentinel = new
            {
                file = ".msbuild-extractor-sample",
                directory = outputDir,
                command = "generated-by:msbuild-extractor-sample/1.0.0"
            };

            JsonSerializerOptions jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            string json;
            if (includeMetadata)
            {
                var entries = commands.Select(c => (object)new
                {
                    file = c.File,
                    arguments = c.Arguments,
                    directory = c.Directory,
                    projectPath = c.ProjectPath,
                    projectName = c.ProjectName,
                    configuration = c.Configuration,
                    platform = c.Platform
                });
                var all = new object[] { sentinel }.Concat(entries).ToArray();
                json = JsonSerializer.Serialize(all, jsonOptions);
            }
            else
            {
                var entries = commands.Select(c => (object)new
                {
                    file = c.File,
                    arguments = c.Arguments,
                    directory = c.Directory
                });
                var all = new object[] { sentinel }.Concat(entries).ToArray();
                json = JsonSerializer.Serialize(all, jsonOptions);
            }
            File.WriteAllText(outputPath, json);
        }

        static void SetVcTargetsPath(string? vcTargetsPath)
        {
            if (vcTargetsPath != null)
            {
                string path = vcTargetsPath.EndsWith("\\") ? vcTargetsPath : vcTargetsPath + "\\";
                Environment.SetEnvironmentVariable("VCTargetsPath", path);
            }
        }

        static string GetDefaultOutputPath(CommandLineOptions options)
        {
            var inputPath = options.Solutions.FirstOrDefault() ?? options.Projects.First();
            var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
            string filename = options.Format == "rich" ? "compile_database.json" : "compile_commands.json";
            return Path.Combine(dir, filename);
        }

        static void RegisterMSBuild(string? vsPath, bool enableLogger = false)
        {
            if (vsPath != null)
            {
                string[] possiblePaths = new[]
                {
                    Path.Combine(vsPath, "MSBuild", "Current", "Bin", "amd64"),
                    Path.Combine(vsPath, "MSBuild", "Current", "Bin"),
                    Path.Combine(vsPath, "MSBuild", "Current", "Bin", "x86"),
                };

                foreach (string msbuildPath in possiblePaths)
                {
                    if (Directory.Exists(msbuildPath) && File.Exists(Path.Combine(msbuildPath, "MSBuild.dll")))
                    {
                        if (enableLogger)
                            Console.WriteLine($"MSBuild: Using --vs-path: {msbuildPath}");
                        MSBuildLocator.RegisterMSBuildPath(msbuildPath);
                        return;
                    }
                }

                Console.Error.WriteLine($"Warning: Could not find MSBuild in {vsPath}, falling back to discovery");
            }

            var instances = MSBuildLocator.QueryVisualStudioInstances()
                .Where(i => i.DiscoveryType == DiscoveryType.VisualStudioSetup)
                .OrderByDescending(i => i.Version)
                .ToList();

            if (enableLogger)
            {
                Console.WriteLine("MSBuild Discovery:");
                foreach (var inst in MSBuildLocator.QueryVisualStudioInstances())
                {
                    string marker = instances.Count > 0 && inst == instances[0] ? " <-- selected" : "";
                    Console.WriteLine($"  [{inst.DiscoveryType}] {inst.Name} v{inst.Version} @ {inst.MSBuildPath}{marker}");
                }
                Console.WriteLine();
            }

            if (instances.Count > 0)
                MSBuildLocator.RegisterInstance(instances[0]);
            else
                MSBuildLocator.RegisterDefaults();
        }

        static MsBuildLauncher ParseLauncher(string s) => s.ToLowerInvariant() switch
        {
            "cmd" => MsBuildLauncher.Cmd,
            "direct" => MsBuildLauncher.Direct,
            "dotnet" => MsBuildLauncher.Dotnet,
            _ => MsBuildLauncher.Auto
        };

        static IncludePathOrder ParseIncludePathOrder(string s) => s.ToLowerInvariant() switch
        {
            "prepend" => IncludePathOrder.Prepend,
            "append" => IncludePathOrder.Append,
            _ => IncludePathOrder.Auto
        };
    }
}
