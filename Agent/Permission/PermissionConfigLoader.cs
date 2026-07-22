using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSAgent.Permission
{
    public static class PermissionConfigLoader
    {
        public static PermissionConfig LoadOrDefault(string path)
        {
            if (!File.Exists(path))
                return Default();

            try
            {
                string json = File.ReadAllText(path);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                PermissionConfig config = new PermissionConfig();

                if (root.TryGetProperty("approvalPolicy", out JsonElement policy))
                    config.ApprovalPolicy = policy.GetString() ?? "ask";

                if (root.TryGetProperty("workspaceRoot", out JsonElement ws) && ws.ValueKind == JsonValueKind.String)
                    config.WorkspaceRoot = ws.GetString();

                if (root.TryGetProperty("allowWriteOutsideWorkspace", out JsonElement allowWrite))
                    config.AllowWriteOutsideWorkspace = allowWrite.ValueKind != JsonValueKind.False;

                if (root.TryGetProperty("permissions", out JsonElement perms) && perms.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in perms.EnumerateObject())
                        config.Permissions[p.Name] = p.Value.GetString() ?? "ask";
                }

                return config;
            }
            catch
            {
                return Default();
            }
        }

        private static PermissionConfig Default() => new PermissionConfig
        {
            ApprovalPolicy = "ask",
            Permissions = new Dictionary<string, string>
            {
                ["read_file"]      = "allow",
                ["list_directory"] = "allow",
                ["search_files"]   = "allow",
                ["grep"]           = "allow",
                ["write_file"]     = "ask",
                ["apply_patch"]    = "ask",
                ["run_command"]    = "ask",
            },
        };
    }
}
