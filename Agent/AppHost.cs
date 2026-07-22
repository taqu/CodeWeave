using CSAgent.Commands;
using CSAgent.Commands.BuiltIn;
using CSAgent.LLM;
using CSAgent.Permission;
using CSAgent.Session;
using CSAgent.Tools;
using CSAgent.Tools.BuiltIn;
using System;
using System.Diagnostics;

namespace CSAgent
{
    public sealed class AppHost
    {
        private static IConfigurationService configurationService_;
        private static readonly Lazy<AppHost> instance_ = new Lazy<AppHost>(() => new AppHost());

        public static AppHost Instance => instance_.Value;

        public static void Initialize(IConfigurationService configurationService)
        {
            configurationService_ = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        }

        public AgentController AgentController { get; }

        private AppHost()
        {
            AgentInstructionProvider instructionProvider = new AgentInstructionProvider();

            ToolRegistry toolRegistry = new ToolRegistry();
            toolRegistry.Register(new ReadFileTool());
            toolRegistry.Register(new WriteFileTool());
            toolRegistry.Register(new ListDirectoryTool());
            toolRegistry.Register(new SearchFilesTool());
            toolRegistry.Register(new GrepTool());
            toolRegistry.Register(new RunCommandTool());
            toolRegistry.Register(new ApplyPatchTool());

            ToolConfigurationLoader configLoader = new ToolConfigurationLoader();
            foreach (ExternalTool tool in configLoader.LoadFromFile("tools.json"))
                toolRegistry.Register(tool);

            TimeSpan toolTimeout = configurationService_?.ToolTimeout ?? TimeSpan.FromSeconds(30);
            ToolExecutor toolExecutor = new ToolExecutor(toolTimeout);

            string apiKey = configurationService_?.OpenAIApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Trace.WriteLine("CodeWeave: OpenAI API key is not configured. Set it in Tools > Options > CodeWeave > OpenAI.");
                return;
            }

            ILLMClient llmClient = new OpenAILLMClient(apiKey);
            string modelName = configurationService_?.ModelName ?? "gpt-4o-mini";
            LLMOptions llmOptions = new LLMOptions { Model = modelName };

            SessionManager sessionManager = new SessionManager(new FileSessionStorage());
            sessionManager.LoadLatestOrCreateAsync(modelName).GetAwaiter().GetResult();

            PermissionConfig permissionConfig = PermissionConfigLoader.LoadOrDefault("permissions.json");
            IPermissionManager permissionManager = new PermissionManager(permissionConfig);
            IApprovalService approvalService = new EventApprovalService();

            ICommandDispatcher commandDispatcher = new CommandDispatcher(new IBuiltInCommand[]
            {
                new HelpCommand(),
                new StatusCommand(),
                new ClearCommand(),
            });

            Agent agent = new Agent();

            AgentController = new AgentController(
                agent, sessionManager, instructionProvider, toolRegistry, toolExecutor,
                llmClient, llmOptions, permissionManager, approvalService, commandDispatcher);
        }
    }
}
