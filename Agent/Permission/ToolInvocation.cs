namespace CSAgent.Permission
{
    public sealed class ToolInvocation
    {
        public string ToolName { get; }
        public string ArgumentsJson { get; }

        public ToolInvocation(string toolName, string argumentsJson)
        {
            ToolName = toolName;
            ArgumentsJson = argumentsJson ?? "{}";
        }
    }
}
