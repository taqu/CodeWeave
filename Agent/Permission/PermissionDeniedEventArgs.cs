using System;

namespace CSAgent.Permission
{
    public sealed class PermissionDeniedEventArgs : EventArgs
    {
        public ToolInvocation Invocation { get; }
        public string Reason { get; }

        public PermissionDeniedEventArgs(ToolInvocation invocation, string reason)
        {
            Invocation = invocation;
            Reason = reason;
        }
    }
}
