using System;
using System.IO;
using System.Text.Json;

namespace CSAgent.Permission
{
    public sealed class PermissionManager : IPermissionManager
    {
        private readonly PermissionConfig config_;

        public PermissionManager(PermissionConfig config)
        {
            config_ = config ?? throw new ArgumentNullException(nameof(config));
        }

        public PermissionDecision Evaluate(ToolInvocation invocation)
        {
            PermissionDecision decision = GetBaseDecision(invocation.ToolName);

            if (decision == PermissionDecision.Allow
                && !config_.AllowWriteOutsideWorkspace
                && !string.IsNullOrEmpty(config_.WorkspaceRoot))
            {
                string path = TryExtractPath(invocation.ArgumentsJson);
                if (path != null && !IsInsideWorkspace(path, config_.WorkspaceRoot))
                    return PermissionDecision.Deny;
            }

            return decision;
        }

        public void UpdatePolicy(string toolName, ApprovalResult persistentDecision)
        {
            if (persistentDecision == ApprovalResult.AlwaysAllow)
                config_.Permissions[toolName] = "allow";
            else if (persistentDecision == ApprovalResult.AlwaysDeny)
                config_.Permissions[toolName] = "deny";
        }

        private PermissionDecision GetBaseDecision(string toolName)
        {
            if (config_.Permissions.TryGetValue(toolName, out string policy))
                return Parse(policy);

            return Parse(config_.ApprovalPolicy);
        }

        private static PermissionDecision Parse(string policy)
        {
            switch (policy?.ToLowerInvariant())
            {
                case "allow": return PermissionDecision.Allow;
                case "deny":  return PermissionDecision.Deny;
                default:      return PermissionDecision.Ask;
            }
        }

        private static string TryExtractPath(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("path", out JsonElement el))
                    return el.GetString();
            }
            catch { }
            return null;
        }

        private static bool IsInsideWorkspace(string path, string workspaceRoot)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(workspaceRoot);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }
    }
}
