using CSAgent.LLM;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools
{
    public class ExternalTool : ITool
    {
        private readonly string command_;
        private readonly string argumentTemplate_;
        private readonly string workingDirectory_;
        private readonly Dictionary<string, string> environmentVariables_;
        private readonly ToolSchema parametersSchema_;

        public string Name { get; }
        public string Description { get; }

        public ExternalTool(
            string name,
            string description,
            string command,
            string argumentTemplate,
            string workingDirectory,
            Dictionary<string, string> environmentVariables,
            ToolSchema parametersSchema)
        {
            Name = name;
            Description = description;
            command_ = command;
            argumentTemplate_ = argumentTemplate ?? string.Empty;
            workingDirectory_ = workingDirectory;
            environmentVariables_ = environmentVariables;
            parametersSchema_ = parametersSchema;
        }

        public ToolSchema GetParametersSchema() => parametersSchema_;

        public async Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            string arguments = BuildArguments(parametersJson);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = command_,
                Arguments = arguments,
                WorkingDirectory = workingDirectory_ ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (environmentVariables_ != null)
            {
                foreach (KeyValuePair<string, string> kv in environmentVariables_)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

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

                string result = stdout.ToString();
                if (stderr.Length > 0)
                    result += "\n[stderr]\n" + stderr.ToString();

                return result.TrimEnd();
            }
        }

        private string BuildArguments(string parametersJson)
        {
            if (string.IsNullOrEmpty(parametersJson))
                return argumentTemplate_;

            string result = argumentTemplate_;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(parametersJson))
                {
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        string value = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()
                            : prop.Value.ToString();
                        result = result.Replace($"{{{prop.Name}}}", value ?? string.Empty);
                    }
                }
            }
            catch
            {
            }
            return result;
        }
    }
}
