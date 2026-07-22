using CSAgent.LLM;
using CSAgent.Permission;
using CSAgent.Session;
using CSAgent.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent
{
    public class Agent
    {
        public async Task RunAsync(
            string userMessage,
            SessionManager sessionManager,
            AgentInstructionProvider instructions,
            ToolRegistry toolRegistry,
            ToolExecutor toolExecutor,
            ILLMClient llm,
            LLMOptions options,
            Action onStart,
            Action<string, string> onProgress,
            Action onFinish,
            Action<string> onError,
            Action<string> onToolStart,
            Action onToolFinish,
            PermissionPipeline permissions,
            CancellationToken cancellationToken)
        {
            string agentInstructions = instructions.GetInstructions();
            string systemPrompt = string.IsNullOrEmpty(agentInstructions)
                ? "You are a helpful assistant."
                : $"You are a helpful assistant.\n\nThe following project instructions are from AGENT.md.\n\n{agentInstructions}";

            List<LLMMessage> messages = new List<LLMMessage> { LLMMessage.System(systemPrompt) };

            var currentSession = sessionManager?.CurrentSession;
            if (currentSession != null)
            {
                foreach (SessionMessage sessionMsg in currentSession.Messages)
                {
                    LLMMessage llmMsg = ConvertToLLMMessage(sessionMsg);
                    if (llmMsg != null)
                        messages.Add(llmMsg);
                }
            }

            messages.Add(LLMMessage.User(userMessage));

            await TrySaveMessageAsync(sessionManager, new SessionMessage
            {
                Role = "user",
                Content = userMessage,
                Timestamp = DateTime.UtcNow,
            }, onError, cancellationToken).ConfigureAwait(false);

            List<LLMTool> tools = toolRegistry.GetAll()
                .Select(t => new LLMTool { Name = t.Name, Description = t.Description, Parameters = t.GetParametersSchema() })
                .ToList();

            bool started = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string accumulatedContent = string.Empty;
                Dictionary<int, (string id, string type, string name, string args)> pendingToolCalls =
                    new Dictionary<int, (string, string, string, string)>();
                bool hasToolCalls = false;

                await foreach (LLMStreamDelta delta in llm.StreamAsync(messages, tools, options, cancellationToken))
                {
                    if (delta.ContentDelta != null)
                    {
                        if (!started)
                        {
                            started = true;
                            onStart?.Invoke();
                        }
                        accumulatedContent += delta.ContentDelta;
                        onProgress?.Invoke(delta.Id, delta.ContentDelta);
                    }

                    if (delta.ToolCallDelta != null)
                    {
                        LLMToolCallDelta tcd = delta.ToolCallDelta;
                        int idx = tcd.Index;

                        if (!pendingToolCalls.TryGetValue(idx, out var existing))
                        {
                            existing = (tcd.Id, tcd.Type, string.Empty, string.Empty);
                            hasToolCalls = true;
                        }

                        existing = (
                            tcd.Id ?? existing.id,
                            tcd.Type ?? existing.type,
                            existing.name + (tcd.FunctionNameDelta ?? string.Empty),
                            existing.args + (tcd.FunctionArgumentsDelta ?? string.Empty)
                        );
                        pendingToolCalls[idx] = existing;
                    }
                }

                if (!hasToolCalls)
                {
                    if (!started) onStart?.Invoke();

                    await TrySaveMessageAsync(sessionManager, new SessionMessage
                    {
                        Role = "assistant",
                        Content = accumulatedContent,
                        Timestamp = DateTime.UtcNow,
                    }, onError, cancellationToken).ConfigureAwait(false);

                    onFinish?.Invoke();
                    break;
                }

                if (!started)
                {
                    started = true;
                    onStart?.Invoke();
                }

                List<LLMToolCall> orderedToolCalls = pendingToolCalls
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new LLMToolCall
                    {
                        Id = kv.Value.id,
                        Type = kv.Value.type,
                        FunctionName = kv.Value.name,
                        FunctionArguments = kv.Value.args,
                    })
                    .ToList();

                await TrySaveMessageAsync(sessionManager, new SessionMessage
                {
                    Role = "assistant",
                    Content = accumulatedContent,
                    Timestamp = DateTime.UtcNow,
                    ToolCalls = orderedToolCalls.Select(tc => new SerializedToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        FunctionName = tc.FunctionName,
                        FunctionArguments = tc.FunctionArguments,
                    }).ToList(),
                }, onError, cancellationToken).ConfigureAwait(false);

                messages.Add(LLMMessage.Assistant(accumulatedContent, orderedToolCalls));

                foreach (LLMToolCall toolCall in orderedToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string toolName = toolCall.FunctionName ?? string.Empty;
                    string toolArgs = toolCall.FunctionArguments ?? "{}";

                    onProgress?.Invoke(toolCall.Id, $"\n[tool: {toolName}]\n");

                    var invocation = new ToolInvocation(toolName, toolArgs);
                    PermissionDecision decision = permissions?.Manager.Evaluate(invocation) ?? PermissionDecision.Allow;

                    if (decision == PermissionDecision.Ask && permissions?.RequestApproval != null)
                    {
                        onToolStart?.Invoke(toolName);
                        ApprovalResult approval = await permissions.RequestApproval(invocation, cancellationToken).ConfigureAwait(false);
                        onToolFinish?.Invoke();

                        if (approval == ApprovalResult.AlwaysAllow || approval == ApprovalResult.AlwaysDeny)
                            permissions.Manager.UpdatePolicy(toolName, approval);

                        if (approval == ApprovalResult.DenyOnce || approval == ApprovalResult.AlwaysDeny)
                            decision = PermissionDecision.Deny;
                        else
                            decision = PermissionDecision.Allow;
                    }

                    DateTime executedAt = DateTime.UtcNow;
                    Stopwatch sw = Stopwatch.StartNew();
                    string toolResult;
                    bool toolSuccess;

                    if (decision == PermissionDecision.Deny)
                    {
                        string reason = $"[Permission Denied] Execution of '{toolName}' was not permitted.";
                        toolResult = reason;
                        toolSuccess = false;
                        permissions?.OnDenied?.Invoke(invocation, reason);
                    }
                    else
                    {
                        onToolStart?.Invoke(toolName);
                        if (toolRegistry.TryResolve(toolName, out ITool tool))
                        {
                            toolResult = await toolExecutor.ExecuteAsync(tool, toolArgs, cancellationToken).ConfigureAwait(false);
                            toolSuccess = !toolResult.StartsWith("[Error]") && !toolResult.StartsWith("[Timeout]");
                        }
                        else
                        {
                            toolResult = $"[Error] Unknown tool: {toolName}";
                            toolSuccess = false;
                        }
                        onToolFinish?.Invoke();
                    }

                    sw.Stop();

                    await TrySaveToolRecordAsync(sessionManager, new ToolRecord
                    {
                        ToolName = toolName,
                        Arguments = toolArgs,
                        ExecutedAt = executedAt,
                        ExecutionMs = sw.ElapsedMilliseconds,
                        Stdout = toolResult,
                        Stderr = string.Empty,
                        Success = toolSuccess,
                    }, onError, cancellationToken).ConfigureAwait(false);

                    await TrySaveMessageAsync(sessionManager, new SessionMessage
                    {
                        Role = "tool",
                        Content = toolResult,
                        Timestamp = DateTime.UtcNow,
                        ToolCallId = toolCall.Id,
                        ToolName = toolName,
                    }, onError, cancellationToken).ConfigureAwait(false);

                    messages.Add(LLMMessage.Tool(toolResult, toolCall.Id));
                }
            }
        }

        private static LLMMessage ConvertToLLMMessage(SessionMessage msg)
        {
            switch (msg.Role)
            {
                case "user":
                    return LLMMessage.User(msg.Content ?? string.Empty);
                case "assistant":
                    List<LLMToolCall> toolCalls = null;
                    if (msg.ToolCalls?.Count > 0)
                    {
                        toolCalls = msg.ToolCalls.Select(tc => new LLMToolCall
                        {
                            Id = tc.Id,
                            Type = tc.Type,
                            FunctionName = tc.FunctionName,
                            FunctionArguments = tc.FunctionArguments,
                        }).ToList();
                    }
                    return LLMMessage.Assistant(msg.Content, toolCalls);
                case "tool":
                    return LLMMessage.Tool(msg.Content, msg.ToolCallId);
                default:
                    return null;
            }
        }

        private static async Task TrySaveMessageAsync(
            SessionManager sessionManager,
            SessionMessage message,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            if (sessionManager == null) return;
            try
            {
                await sessionManager.AppendMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"[Session] 保存失敗: {ex.Message}");
            }
        }

        private static async Task TrySaveToolRecordAsync(
            SessionManager sessionManager,
            ToolRecord record,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            if (sessionManager == null) return;
            try
            {
                await sessionManager.AppendToolRecordAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"[Session] ツール記録保存失敗: {ex.Message}");
            }
        }
    }
}
