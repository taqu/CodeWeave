using CSAgent.LLM;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class SearchFilesTool : ITool
    {
        public string Name => "search_files";
        public string Description => "Search for files matching a glob pattern within a directory.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["pattern"] = new ToolSchema { Type = "string", Description = "Filename glob pattern (e.g. *.cs, **/*.txt)." },
                ["directory"] = new ToolSchema { Type = "string", Description = "Root directory to search. Defaults to current directory." }
            },
            Required = new List<string> { "pattern" }
        };

        public Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            string pattern = "*";
            string directory = Directory.GetCurrentDirectory();

            using (JsonDocument doc = JsonDocument.Parse(parametersJson))
            {
                if (doc.RootElement.TryGetProperty("pattern", out JsonElement patEl))
                    pattern = patEl.GetString() ?? "*";
                if (doc.RootElement.TryGetProperty("directory", out JsonElement dirEl) && dirEl.ValueKind == JsonValueKind.String)
                    directory = dirEl.GetString() ?? directory;
            }

            if (!Directory.Exists(directory))
                return Task.FromResult($"[Error] Directory not found: {directory}");

            string[] files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);

            if (files.Length == 0)
                return Task.FromResult($"No files found matching '{pattern}' in {directory}");

            StringBuilder sb = new StringBuilder();
            foreach (string file in files)
                sb.AppendLine(file);

            return Task.FromResult(sb.ToString().TrimEnd());
        }
    }
}
