using CSAgent.LLM;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class ListDirectoryTool : ITool
    {
        public string Name => "list_directory";
        public string Description => "List files and subdirectories in a directory.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["path"] = new ToolSchema { Type = "string", Description = "Directory path to list. Defaults to current directory." },
                ["recursive"] = new ToolSchema { Type = "boolean", Description = "Whether to list recursively. Defaults to false." }
            },
            Required = new List<string>()
        };

        public Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            string path = Directory.GetCurrentDirectory();
            bool recursive = false;

            if (!string.IsNullOrEmpty(parametersJson))
            {
                using (JsonDocument doc = JsonDocument.Parse(parametersJson))
                {
                    if (doc.RootElement.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String)
                        path = pathEl.GetString() ?? path;
                    if (doc.RootElement.TryGetProperty("recursive", out JsonElement recEl) && recEl.ValueKind == JsonValueKind.True)
                        recursive = true;
                }
            }

            if (!Directory.Exists(path))
                return Task.FromResult($"[Error] Directory not found: {path}");

            SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            StringBuilder sb = new StringBuilder();

            foreach (string dir in Directory.GetDirectories(path, "*", option))
                sb.AppendLine("[DIR] " + dir);
            foreach (string file in Directory.GetFiles(path, "*", option))
                sb.AppendLine(file);

            return Task.FromResult(sb.Length > 0 ? sb.ToString().TrimEnd() : "(empty)");
        }
    }
}
