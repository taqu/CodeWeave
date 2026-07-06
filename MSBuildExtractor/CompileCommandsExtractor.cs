using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWeave
{
    public sealed class CompileCommandsExtractor
    {
        private readonly string extractorExePath_;

        public CompileCommandsExtractor(string extractorExePath)
        {
            if (!File.Exists(extractorExePath))
            {
                throw new FileNotFoundException(
                    "Extractor executable not found.",
                    extractorExePath);
            }

            extractorExePath_ = extractorExePath;
        }

        /// <summary>
        /// sln または vcxproj から compile_commands.json を生成する。
        /// </summary>
        public async Task<string> ExtractAsync(
            string inputPath,
            string outputPath,
            string configuration = "Debug",
            string platform = "x64",
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException(inputPath);
            }

            bool solution;
            string arguments = BuildArguments(
                out solution,
                inputPath,
                outputPath,
                configuration,
                platform);

            var psi = new ProcessStartInfo
            {
                FileName = extractorExePath_,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process();
            process.StartInfo = psi;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stdout.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stderr.AppendLine(e.Data);
            };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"""
                compile_commands extraction failed.

                ExitCode: {process.ExitCode}

                StdOut:
                {stdout}

                StdErr:
                {stderr}
                """);
            }
            if (!solution && !File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    $"Extractor succeeded but output file was not generated: {outputPath}");
            }

            return outputPath;
        }

        private static string BuildArguments(
            out bool solution,
            string inputPath,
            string outputPath,
            string configuration,
            string platform)
        {
            StringBuilder sb = new StringBuilder();

            if (Path.GetExtension(inputPath)
                .Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                solution = true;
                sb.Append($"--solution \"{inputPath}\" ");
                sb.Append("--split-by-project true ");
            }
            else
            {
                solution = false;
                sb.Append($"--project \"{inputPath}\" ");
                sb.Append("--split-by-project false ");
            }

            sb.Append($"--configuration \"{configuration}\" ");
            sb.Append($"--platform \"{platform}\" ");
            sb.Append($"--output \"{outputPath}\" ");
            sb.Append("--deduplicate true ");

            return sb.ToString();
        }
    }
}
