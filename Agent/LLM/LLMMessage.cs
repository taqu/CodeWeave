using System.Collections.Generic;

namespace CSAgent.LLM
{
    public class LLMMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string ToolCallId { get; set; }
        public List<LLMToolCall> ToolCalls { get; set; }

        public static LLMMessage System(string content) =>
            new LLMMessage { Role = "system", Content = content };

        public static LLMMessage User(string content) =>
            new LLMMessage { Role = "user", Content = content };

        public static LLMMessage Assistant(string content, List<LLMToolCall> toolCalls = null) =>
            new LLMMessage { Role = "assistant", Content = content, ToolCalls = toolCalls };

        public static LLMMessage Tool(string content, string toolCallId) =>
            new LLMMessage { Role = "tool", Content = content, ToolCallId = toolCallId };
    }

    public class LLMToolCall
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string FunctionName { get; set; }
        public string FunctionArguments { get; set; }
    }
}
