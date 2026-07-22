using CSAgent.LLM;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class RunCommandTool : ITool
    {
        public string Name => "run_command";
        public string Description => "Execute a shell command and return its output. Use with caution.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["command"] = new ToolSchema { Type = "string", Description = "The command to execute." },
                ["working_directory"] = new ToolSchema { Type = "string", Description = "Working directory for the command. Defaults to current directory." }
            },
            Required = new List<string> { "command" }
        };

        public async Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            string command = string.Empty;
            string workingDirectory = Environment.CurrentDirectory;

            using (JsonDocument doc = JsonDocument.Parse(parametersJson))
            {
                if (doc.RootElement.TryGetProperty("command", out JsonElement cmdEl))
                    command = cmdEl.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("working_directory", out JsonElement wdEl) && wdEl.ValueKind == JsonValueKind.String)
                    workingDirectory = wdEl.GetString() ?? workingDirectory;
            }

            if (string.IsNullOrWhiteSpace(command))
                return "[Error] Missing required parameter: command";

            bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            string shell = isWindows ? "cmd.exe" : "/bin/sh";
            string shellArgs = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = shellArgs,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (Process process = new Process { StartInfo = psi })
            {
                StringBuilder stdout = new StringBuilder();
                StringBuilder stderr = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (cancellationToken.Register(() => { try { process.Kill(); } catch { } }))
                {
                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                }

                StringBuilder result = new StringBuilder();
                if (stdout.Length > 0)
                    result.Append(stdout);
                if (stderr.Length > 0)
                {
                    if (result.Length > 0) result.AppendLine();
                    result.Append("[stderr]\n").Append(stderr);
                }
                result.Append($"\n[exit code: {process.ExitCode}]");

                return result.ToString().TrimEnd();
            }
        }
    }
}
