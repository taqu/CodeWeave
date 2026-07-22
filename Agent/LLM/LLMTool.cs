namespace CSAgent.LLM
{
    public class LLMTool
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ToolSchema Parameters { get; set; }
    }
}
