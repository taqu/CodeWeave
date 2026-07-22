using System.Collections.Generic;

namespace CSAgent.Permission
{
    public sealed class PermissionConfig
    {
        public string ApprovalPolicy { get; set; } = "ask";
        public Dictionary<string, string> Permissions { get; set; } = new Dictionary<string, string>();
        public string WorkspaceRoot { get; set; }
        public bool AllowWriteOutsideWorkspace { get; set; } = true;
    }
}
