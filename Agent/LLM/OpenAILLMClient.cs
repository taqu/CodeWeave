using Betalgo.Ranul.OpenAI.Managers;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.ResponseModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CSAgent.LLM
{
    public class OpenAILLMClient : ILLMClient
    {
        private readonly OpenAIService openAIService_;

        public OpenAILLMClient(string apiKey, string baseDomain = "https://api.openai.com/")
        {
            openAIService_ = new OpenAIService(new Betalgo.Ranul.OpenAI.OpenAIOptions
            {
                ApiKey = apiKey,
                BaseDomain = baseDomain,
            });
        }

        public async IAsyncEnumerable<LLMStreamDelta> StreamAsync(
            IReadOnlyList<LLMMessage> messages,
            IReadOnlyList<LLMTool> tools,
            LLMOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            List<ChatMessage> chatMessages = new List<ChatMessage>();
            foreach (LLMMessage msg in messages)
                chatMessages.Add(ConvertMessage(msg));

            List<ToolDefinition> toolDefs = new List<ToolDefinition>();
            foreach (LLMTool tool in tools)
            {
                toolDefs.Add(new ToolDefinition
                {
                    Type = "function",
                    Function = new FunctionDefinition
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        Parameters = ConvertSchema(tool.Parameters),
                    }
                });
            }

            ChatCompletionCreateRequest request = new ChatCompletionCreateRequest
            {
                Model = options.Model,
                Messages = chatMessages,
                MaxTokens = options.MaxTokens,
                Temperature = options.Temperature,
            };

            if (toolDefs.Count > 0)
                request.Tools = toolDefs;

            await foreach (ChatCompletionCreateResponse update in openAIService_.ChatCompletion
                .CreateCompletionAsStream(request, cancellationToken: cancellationToken))
            {
                foreach (ChatChoiceResponse choice in update.Choices)
                {
                    string content = choice.Message?.Content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return new LLMStreamDelta { Id = update.Id, ContentDelta = content };
                    }

                    IList<ToolCall> toolCallDeltas = choice.Message?.ToolCalls;
                    if (toolCallDeltas != null)
                    {
                        foreach (ToolCall delta in toolCallDeltas)
                        {
                            yield return new LLMStreamDelta
                            {
                                Id = update.Id,
                                ToolCallDelta = new LLMToolCallDelta
                                {
                                    Index = delta.Index,
                                    Id = delta.Id,
                                    Type = delta.Type,
                                    FunctionNameDelta = delta.FunctionCall?.Name,
                                    FunctionArgumentsDelta = delta.FunctionCall?.Arguments,
                                }
                            };
                        }
                    }
                }
            }
        }

        private static PropertyDefinition ConvertSchema(ToolSchema schema)
        {
            if (schema == null) return null;
            var def = new PropertyDefinition
            {
                Type = schema.Type,
                Description = schema.Description,
                Title = schema.Title,
                Required = schema.Required,
                Enum = schema.Enum,
            };
            if (schema.Properties != null)
            {
                def.Properties = new Dictionary<string, PropertyDefinition>();
                foreach (var kv in schema.Properties)
                    def.Properties[kv.Key] = ConvertSchema(kv.Value);
            }
            return def;
        }

        private static ChatMessage ConvertMessage(LLMMessage msg)
        {
            if (msg.Role == "system") return ChatMessage.FromSystem(msg.Content ?? string.Empty);
            if (msg.Role == "user") return ChatMessage.FromUser(msg.Content ?? string.Empty);

            List<ToolCall> toolCalls = null;
            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                toolCalls = new List<ToolCall>();
                foreach (LLMToolCall tc in msg.ToolCalls)
                {
                    toolCalls.Add(new ToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        FunctionCall = new FunctionCall
                        {
                            Name = tc.FunctionName ?? string.Empty,
                            Arguments = tc.FunctionArguments ?? string.Empty,
                        }
                    });
                }
            }

            var role = new Betalgo.Ranul.OpenAI.Contracts.Enums.ChatCompletionRole(msg.Role);
            return new ChatMessage(role, msg.Content, null, toolCalls, msg.ToolCallId);
        }
    }
}
