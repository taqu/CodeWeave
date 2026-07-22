using CSAgent.LLM;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class WriteFileTool : ITool
    {
        public string Name => "write_file";
        public string Description => "Write content to a file, creating it or overwriting if it already exists.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["path"] = new ToolSchema { Type = "string", Description = "Absolute or relative path of the file to write." },
                ["content"] = new ToolSchema { Type = "string", Description = "Content to write to the file." }
            },
            Required = new List<string> { "path", "content" }
        };

        public Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            using (JsonDocument doc = JsonDocument.Parse(parametersJson))
            {
                if (!doc.RootElement.TryGetProperty("path", out JsonElement pathEl))
                    return Task.FromResult("[Error] Missing required parameter: path");
                if (!doc.RootElement.TryGetProperty("content", out JsonElement contentEl))
                    return Task.FromResult("[Error] Missing required parameter: content");

                string path = pathEl.GetString();
                string content = contentEl.GetString() ?? string.Empty;

                if (string.IsNullOrEmpty(path))
                    return Task.FromResult("[Error] Parameter 'path' must not be empty.");

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(path, content);
                return Task.FromResult($"Successfully wrote {content.Length} characters to {path}");
            }
        }
    }
}
