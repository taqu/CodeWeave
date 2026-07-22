using CSAgent.Session;
using CSAgent.Tools;

namespace CSAgent.Commands
{
    public sealed class CommandContext
    {
        public AgentController Controller { get; }
        public SessionManager SessionManager { get; }
        public ToolRegistry ToolRegistry { get; }
        public ICommandDispatcher Dispatcher { get; }

        public CommandContext(
            AgentController controller,
            SessionManager sessionManager,
            ToolRegistry toolRegistry,
            ICommandDispatcher dispatcher)
        {
            Controller = controller;
            SessionManager = sessionManager;
            ToolRegistry = toolRegistry;
            Dispatcher = dispatcher;
        }
    }
}
