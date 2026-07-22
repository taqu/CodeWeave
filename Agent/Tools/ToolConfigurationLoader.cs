using CSAgent.LLM;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSAgent.Tools
{
    public class ToolConfigurationLoader
    {
        public TimeSpan? ToolTimeout { get; private set; }

        public IEnumerable<ExternalTool> LoadFromFile(string configPath)
        {
            if (!File.Exists(configPath))
                yield break;

            string json;
            try
            {
                json = File.ReadAllText(configPath);
            }
            catch
            {
                yield break;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch
            {
                yield break;
            }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("toolTimeout", out JsonElement timeoutEl))
                {
                    if (timeoutEl.TryGetInt32(out int seconds))
                        ToolTimeout = TimeSpan.FromSeconds(seconds);
                }

                if (!doc.RootElement.TryGetProperty("tools", out JsonElement toolsEl) ||
                    toolsEl.ValueKind != JsonValueKind.Array)
                    yield break;

                foreach (JsonElement toolEl in toolsEl.EnumerateArray())
                {
                    ExternalTool tool = TryParseToolDefinition(toolEl);
                    if (tool != null)
                        yield return tool;
                }
            }
        }

        private static ExternalTool TryParseToolDefinition(JsonElement el)
        {
            try
            {
                if (!el.TryGetProperty("name", out JsonElement nameEl) || string.IsNullOrWhiteSpace(nameEl.GetString()))
                    return null;
                if (!el.TryGetProperty("description", out JsonElement descEl))
                    return null;
                if (!el.TryGetProperty("command", out JsonElement cmdEl) || string.IsNullOrWhiteSpace(cmdEl.GetString()))
                    return null;

                string name = nameEl.GetString();
                string description = descEl.GetString() ?? string.Empty;
                string command = cmdEl.GetString();
                string argumentTemplate = el.TryGetProperty("argumentTemplate", out JsonElement argTplEl) ? argTplEl.GetString() : string.Empty;
                string workingDirectory = el.TryGetProperty("workingDirectory", out JsonElement wdEl) && wdEl.ValueKind != JsonValueKind.Null
                    ? wdEl.GetString()
                    : null;

                Dictionary<string, string> envVars = null;
                if (el.TryGetProperty("environmentVariables", out JsonElement envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    envVars = new Dictionary<string, string>();
                    foreach (JsonProperty kv in envEl.EnumerateObject())
                        envVars[kv.Name] = kv.Value.GetString() ?? string.Empty;
                }

                ToolSchema schema = el.TryGetProperty("parametersSchema", out JsonElement schemaEl)
                    ? BuildPropertyDefinition(schemaEl)
                    : new ToolSchema { Type = "object", Properties = new Dictionary<string, ToolSchema>() };

                return new ExternalTool(name, description, command, argumentTemplate, workingDirectory, envVars, schema);
            }
            catch
            {
                return null;
            }
        }

        internal static ToolSchema BuildPropertyDefinition(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
                return new ToolSchema { Type = "string" };

            ToolSchema def = new ToolSchema();

            if (el.TryGetProperty("type", out JsonElement typeEl))
                def.Type = typeEl.GetString();
            if (el.TryGetProperty("description", out JsonElement descEl))
                def.Description = descEl.GetString();
            if (el.TryGetProperty("title", out JsonElement titleEl))
                def.Title = titleEl.GetString();

            if (el.TryGetProperty("properties", out JsonElement propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                def.Properties = new Dictionary<string, ToolSchema>();
                foreach (JsonProperty prop in propsEl.EnumerateObject())
                    def.Properties[prop.Name] = BuildPropertyDefinition(prop.Value);
            }

            if (el.TryGetProperty("required", out JsonElement reqEl) && reqEl.ValueKind == JsonValueKind.Array)
            {
                def.Required = new List<string>();
                foreach (JsonElement r in reqEl.EnumerateArray())
                {
                    string s = r.GetString();
                    if (s != null) def.Required.Add(s);
                }
            }

            if (el.TryGetProperty("enum", out JsonElement enumEl) && enumEl.ValueKind == JsonValueKind.Array)
            {
                def.Enum = new List<string>();
                foreach (JsonElement e in enumEl.EnumerateArray())
                {
                    string s = e.GetString();
                    if (s != null) def.Enum.Add(s);
                }
            }

            return def;
        }
    }
}
