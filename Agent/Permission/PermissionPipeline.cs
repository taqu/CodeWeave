using System;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Permission
{
    public sealed class PermissionPipeline
    {
        public IPermissionManager Manager { get; }
        public Func<ToolInvocation, CancellationToken, Task<ApprovalResult>> RequestApproval { get; }
        public Action<ToolInvocation, string> OnDenied { get; }

        public PermissionPipeline(
            IPermissionManager manager,
            Func<ToolInvocation, CancellationToken, Task<ApprovalResult>> requestApproval,
            Action<ToolInvocation, string> onDenied)
        {
            Manager = manager ?? throw new ArgumentNullException(nameof(manager));
            RequestApproval = requestApproval;
            OnDenied = onDenied;
        }
    }
}
