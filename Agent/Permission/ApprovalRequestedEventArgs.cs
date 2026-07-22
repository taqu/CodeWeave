using System;

namespace CSAgent.Permission
{
    public sealed class ApprovalRequestedEventArgs : EventArgs
    {
        private readonly Action<ApprovalResult> setResult_;

        public ToolInvocation Invocation { get; }

        public ApprovalRequestedEventArgs(ToolInvocation invocation, Action<ApprovalResult> setResult)
        {
            Invocation = invocation;
            setResult_ = setResult;
        }

        public void SetResult(ApprovalResult result) => setResult_(result);
    }
}
