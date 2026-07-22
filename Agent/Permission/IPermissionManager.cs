namespace CSAgent.Permission
{
    public interface IPermissionManager
    {
        PermissionDecision Evaluate(ToolInvocation invocation);
        void UpdatePolicy(string toolName, ApprovalResult persistentDecision);
    }
}
