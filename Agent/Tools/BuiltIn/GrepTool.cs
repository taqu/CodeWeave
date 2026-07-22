using CSAgent.LLM;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class GrepTool : ITool
    {
        public string Name => "grep";
        public string Description => "Search for a pattern in files and return matching lines with line numbers.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["pattern"] = new ToolSchema { Type = "string", Description = "Regular expression or literal string to search for." },
                ["path"] = new ToolSchema { Type = "string", Description = "File or directory to search. Defaults to current directory." },
                ["file_pattern"] = new ToolSchema { Type = "string", Description = "Glob pattern for files to include (e.g. *.cs). Defaults to *." },
                ["ignore_case"] = new ToolSchema { Type = "boolean", Description = "Perform case-insensitive matching. Defaults to false." },
                ["literal"] = new ToolSchema { Type = "boolean", Description = "Treat pattern as a literal string, not a regex. Defaults to false." }
            },
            Required = new List<string> { "pattern" }
        };

        public Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            string pattern = string.Empty;
            string searchPath = Directory.GetCurrentDirectory();
            string filePattern = "*";
            bool ignoreCase = false;
            bool literal = false;

            using (JsonDocument doc = JsonDocument.Parse(parametersJson))
            {
                if (doc.RootElement.TryGetProperty("pattern", out JsonElement patEl))
                    pattern = patEl.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String)
                    searchPath = pathEl.GetString() ?? searchPath;
                if (doc.RootElement.TryGetProperty("file_pattern", out JsonElement fpEl) && fpEl.ValueKind == JsonValueKind.String)
                    filePattern = fpEl.GetString() ?? "*";
                if (doc.RootElement.TryGetProperty("ignore_case", out JsonElement icEl) && icEl.ValueKind == JsonValueKind.True)
                    ignoreCase = true;
                if (doc.RootElement.TryGetProperty("literal", out JsonElement litEl) && litEl.ValueKind == JsonValueKind.True)
                    literal = true;
            }

            if (string.IsNullOrEmpty(pattern)) {
                return Task.FromResult("[Error] Missing required parameter: pattern");
            }
            RegexOptions options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            Regex regex = new Regex(literal ? Regex.Escape(pattern) : pattern, options);

            IEnumerable<string> files;
            if (File.Exists(searchPath))
            {
                files = new[] { searchPath };
            }
            else if (Directory.Exists(searchPath))
            {
                files = Directory.GetFiles(searchPath, filePattern, SearchOption.AllDirectories);
            }
            else
            {
                return Task.FromResult($"[Error] Path not found: {searchPath}");
            }

            StringBuilder sb = new StringBuilder();
            int matchCount = 0;

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            sb.AppendLine($"{file}:{i + 1}: {lines[i]}");
                            matchCount++;
                            if (matchCount >= 1000)
                            {
                                sb.AppendLine("[Output truncated at 1000 matches]");
                                return Task.FromResult(sb.ToString().TrimEnd());
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            return Task.FromResult(matchCount > 0 ? sb.ToString().TrimEnd() : $"No matches found for '{pattern}'");
        }
    }
}
