using System.Collections.Generic;

namespace CSAgent.LLM
{
    public class ToolSchema
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public string Title { get; set; }
        public Dictionary<string, ToolSchema> Properties { get; set; }
        public List<string> Required { get; set; }
        public List<string> Enum { get; set; }
    }
}
