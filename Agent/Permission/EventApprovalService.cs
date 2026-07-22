using System;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Permission
{
    public sealed class EventApprovalService : IApprovalService
    {
        public event EventHandler<ApprovalRequestedEventArgs> ApprovalRequested;

        public Task<ApprovalResult> RequestApprovalAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<ApprovalResult>();
            var args = new ApprovalRequestedEventArgs(invocation, result => tcs.TrySetResult(result));

            var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

            tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

            ApprovalRequested?.Invoke(this, args);

            return tcs.Task;
        }
    }
}
