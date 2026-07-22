using System;
using System.Text.Json.Serialization;

namespace CSAgent.Session
{
    public class ToolRecord
    {
        [JsonPropertyName("toolName")]
        public string ToolName { get; set; }

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }

        [JsonPropertyName("executedAt")]
        public DateTime ExecutedAt { get; set; }

        [JsonPropertyName("executionMs")]
        public long ExecutionMs { get; set; }

        [JsonPropertyName("stdout")]
        public string Stdout { get; set; }

        [JsonPropertyName("stderr")]
        public string Stderr { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }
}
