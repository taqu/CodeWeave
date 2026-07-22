namespace CSAgent.LLM
{
    public class LLMStreamDelta
    {
        public string Id { get; set; }
        public string ContentDelta { get; set; }
        public LLMToolCallDelta ToolCallDelta { get; set; }
    }

    public class LLMToolCallDelta
    {
        public int Index { get; set; }
        public string Id { get; set; }
        public string Type { get; set; }
        public string FunctionNameDelta { get; set; }
        public string FunctionArgumentsDelta { get; set; }
    }
}
