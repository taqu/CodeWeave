using System;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Permission
{
    public interface IApprovalService
    {
        event EventHandler<ApprovalRequestedEventArgs> ApprovalRequested;
        Task<ApprovalResult> RequestApprovalAsync(ToolInvocation invocation, CancellationToken cancellationToken);
    }
}
