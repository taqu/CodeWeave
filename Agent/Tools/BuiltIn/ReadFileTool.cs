using CSAgent.LLM;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class ReadFileTool : ITool
    {
        public string Name => "read_file";
        public string Description => "Read the contents of a file at the specified path.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["path"] = new ToolSchema { Type = "string", Description = "Absolute or relative path of the file to read." }
            },
            Required = new List<string> { "path" }
        };

        public Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            using (JsonDocument doc = JsonDocument.Parse(parametersJson))
            {
                if (!doc.RootElement.TryGetProperty("path", out JsonElement pathEl))
                    return Task.FromResult("[Error] Missing required parameter: path");

                string path = pathEl.GetString();
                if (string.IsNullOrEmpty(path))
                    return Task.FromResult("[Error] Parameter 'path' must not be empty.");
                if (!File.Exists(path))
                    return Task.FromResult($"[Error] File not found: {path}");

                string content = File.ReadAllText(path);
                return Task.FromResult(content);
            }
        }
    }
}
