namespace CSAgent.LLM
{
    public class LLMOptions
    {
        public string Model { get; set; } = "gpt-4o-mini";
        public int MaxTokens { get; set; } = 4096;
        public float Temperature { get; set; } = 0.7f;
    }
}
