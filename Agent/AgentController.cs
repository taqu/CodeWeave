using CSAgent.Commands;
using CSAgent.LLM;
using CSAgent.Permission;
using CSAgent.Session;
using CSAgent.Tools;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent
{
    public sealed class AgentController : IDisposable
    {
        private readonly Agent agent_;
        private readonly SessionManager sessionManager_;
        private readonly AgentInstructionProvider instructionProvider_;
        private readonly ToolRegistry toolRegistry_;
        private readonly ToolExecutor toolExecutor_;
        private readonly ILLMClient llmClient_;
        private readonly LLMOptions llmOptions_;
        private readonly IPermissionManager permissionManager_;
        private readonly IApprovalService approvalService_;
        private readonly ICommandDispatcher commandDispatcher_;

        private CancellationTokenSource currentCts_;
        private bool disposed_;

        public AgentState State { get; private set; } = AgentState.Idle;

        public event EventHandler<AgentStateChangedEventArgs> StateChanged;
        public event EventHandler<StatusChangedEventArgs> StatusChanged;
        public event EventHandler<ContentStreamedEventArgs> ContentStreamed;
        public event EventHandler<ApprovalRequestedEventArgs> ApprovalRequested;
        public event EventHandler<PermissionDeniedEventArgs> PermissionDenied;

        public AgentController(
            Agent agent,
            SessionManager sessionManager,
            AgentInstructionProvider instructionProvider,
            ToolRegistry toolRegistry,
            ToolExecutor toolExecutor,
            ILLMClient llmClient,
            LLMOptions llmOptions,
            IPermissionManager permissionManager,
            IApprovalService approvalService,
            ICommandDispatcher commandDispatcher)
        {
            agent_ = agent ?? throw new ArgumentNullException(nameof(agent));
            sessionManager_ = sessionManager;
            instructionProvider_ = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
            toolRegistry_ = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
            toolExecutor_ = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
            llmClient_ = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
            llmOptions_ = llmOptions ?? new LLMOptions();
            permissionManager_ = permissionManager ?? throw new ArgumentNullException(nameof(permissionManager));
            approvalService_ = approvalService ?? throw new ArgumentNullException(nameof(approvalService));
            commandDispatcher_ = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));

            approvalService_.ApprovalRequested += (s, e) =>
            {
                SetState(AgentState.WaitingForApproval);
                SetStatus($"Waiting for approval: {e.Invocation.ToolName}");
                ApprovalRequested?.Invoke(this, e);
            };
        }

        public IReadOnlyList<string> GetAvailableCommands() => commandDispatcher_.GetAvailableCommands();

        public async Task RunAsync(string input, CancellationToken externalToken = default)
        {
            if (disposed_) throw new ObjectDisposedException(nameof(AgentController));
            if (State != AgentState.Idle) return;

            currentCts_ = externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : new CancellationTokenSource();

            var context = new CommandContext(this, sessionManager_, toolRegistry_, commandDispatcher_);

            if (commandDispatcher_.TryDispatch(input, context, currentCts_.Token, out Task<CommandResult> cmdTask))
            {
                try
                {
                    CommandResult result = await cmdTask.ConfigureAwait(false);
                    OnContentStreamed(result.Output);
                }
                catch (Exception ex)
                {
                    OnContentStreamed($"[Command Error] {ex.Message}\n");
                }
                finally
                {
                    currentCts_?.Dispose();
                    currentCts_ = null;
                }
                return;
            }

            SetState(AgentState.Thinking);
            SetStatus("Thinking...");

            var pipeline = new PermissionPipeline(
                permissionManager_,
                (inv, ct) => approvalService_.RequestApprovalAsync(inv, ct),
                (inv, reason) =>
                {
                    PermissionDenied?.Invoke(this, new PermissionDeniedEventArgs(inv, reason));
                    OnContentStreamed(reason + "\n");
                });

            try
            {
                await agent_.RunAsync(
                    input,
                    sessionManager_,
                    instructionProvider_,
                    toolRegistry_,
                    toolExecutor_,
                    llmClient_,
                    llmOptions_,
                    onStart: null,
                    onProgress: (id, text) => OnContentStreamed(text),
                    onFinish: null,
                    onError: err => OnContentStreamed(err),
                    onToolStart: name =>
                    {
                        SetState(AgentState.ToolRunning);
                        SetStatus($"Running: {name}");
                    },
                    onToolFinish: () =>
                    {
                        SetState(AgentState.Thinking);
                        SetStatus("Thinking...");
                    },
                    permissions: pipeline,
                    currentCts_.Token).ConfigureAwait(false);

                SetStatus("Completed");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled");
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
            finally
            {
                currentCts_?.Dispose();
                currentCts_ = null;
                SetState(AgentState.Idle);
            }
        }

        public void Cancel()
        {
            if (State == AgentState.Idle || State == AgentState.Cancelling) return;
            SetState(AgentState.Cancelling);
            SetStatus("Cancelling...");
            currentCts_?.Cancel();
        }

        public void Dispose()
        {
            if (disposed_) return;
            disposed_ = true;
            currentCts_?.Cancel();
            currentCts_?.Dispose();
            currentCts_ = null;
        }

        private void SetState(AgentState state)
        {
            State = state;
            StateChanged?.Invoke(this, new AgentStateChangedEventArgs(state));
        }

        private void SetStatus(string status)
        {
            StatusChanged?.Invoke(this, new StatusChangedEventArgs(status));
        }

        private void OnContentStreamed(string text)
        {
            ContentStreamed?.Invoke(this, new ContentStreamedEventArgs(text));
        }
    }
}
