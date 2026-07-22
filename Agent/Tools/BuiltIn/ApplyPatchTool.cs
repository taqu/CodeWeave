using CSAgent.LLM;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools.BuiltIn
{
    public class ApplyPatchTool : ITool
    {
        public string Name => "apply_patch";
        public string Description => "Apply a unified diff patch to a file.";

        public ToolSchema GetParametersSchema() => new ToolSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolSchema>
            {
                ["path"] = new ToolSchema { Type = "string", Description = "Path of the file to patch." },
                ["patch"] = new ToolSchema { Type = "string", Description = "Unified diff patch content." }
            },
            Required = new List<string> { "path", "patch" }
        };

        public Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken)
        {
            string path = null;
            string patch = null;

            using (JsonDocument doc = JsonDocument.Parse(parametersJson))
            {
                if (doc.RootElement.TryGetProperty("path", out JsonElement pathEl))
                    path = pathEl.GetString();
                if (doc.RootElement.TryGetProperty("patch", out JsonElement patchEl))
                    patch = patchEl.GetString();
            }

            if (string.IsNullOrEmpty(path))
                return Task.FromResult("[Error] Missing required parameter: path");
            if (string.IsNullOrEmpty(patch))
                return Task.FromResult("[Error] Missing required parameter: patch");
            if (!File.Exists(path))
                return Task.FromResult($"[Error] File not found: {path}");

            try
            {
                string[] originalLines = File.ReadAllLines(path);
                List<string> result = ApplyUnifiedDiff(originalLines, patch);
                File.WriteAllLines(path, result);
                return Task.FromResult($"Patch applied successfully to {path}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"[Error] Failed to apply patch: {ex.Message}");
            }
        }

        private static readonly Regex HunkHeader = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

        private static List<string> ApplyUnifiedDiff(string[] original, string patchText)
        {
            List<string> result = new List<string>(original);
            string[] patchLines = patchText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            int lineOffset = 0;
            int i = 0;

            while (i < patchLines.Length)
            {
                Match m = HunkHeader.Match(patchLines[i]);
                if (!m.Success)
                {
                    i++;
                    continue;
                }

                int origStart = int.Parse(m.Groups[1].Value) - 1;
                int origCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                i++;

                List<string> removals = new List<string>();
                List<string> additions = new List<string>();
                List<(bool isContext, string line)> hunkLines = new List<(bool, string)>();

                while (i < patchLines.Length && !patchLines[i].StartsWith("@@") &&
                       !patchLines[i].StartsWith("--- ") && !patchLines[i].StartsWith("+++ "))
                {
                    string pl = patchLines[i];
                    if (pl.StartsWith("-"))
                        hunkLines.Add((false, pl.Substring(1)));
                    else if (pl.StartsWith("+"))
                        hunkLines.Add((false, "+" + pl.Substring(1)));
                    else
                        hunkLines.Add((true, pl.Length > 0 ? pl.Substring(1) : string.Empty));
                    i++;
                }

                int insertAt = origStart + lineOffset;
                int deleteCount = 0;
                List<string> toInsert = new List<string>();

                foreach ((bool isContext, string line) in hunkLines)
                {
                    if (isContext)
                    {
                        deleteCount++;
                        toInsert.Add(line);
                    }
                    else if (line.StartsWith("+"))
                    {
                        toInsert.Add(line.Substring(1));
                    }
                    else
                    {
                        deleteCount++;
                    }
                }

                if (insertAt >= 0 && insertAt <= result.Count)
                {
                    int removeCount = Math.Min(deleteCount, result.Count - insertAt);
                    result.RemoveRange(insertAt, removeCount);
                    result.InsertRange(insertAt, toInsert);
                    lineOffset += toInsert.Count - removeCount;
                }
            }

            return result;
        }
    }
}
