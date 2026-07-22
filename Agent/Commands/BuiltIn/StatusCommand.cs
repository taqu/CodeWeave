using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Commands.BuiltIn
{
    public sealed class StatusCommand : IBuiltInCommand
    {
        public string Name => "status";
        public string Description => "Show current agent state and session information.";

        public Task<CommandResult> ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"State:   {context.Controller.State}");

            CSAgent.Session.Session session = context.SessionManager.CurrentSession;
            if (session != null)
            {
                sb.AppendLine($"Session: {session.Metadata.Id}");
                sb.AppendLine($"Model:   {session.Metadata.Model ?? "(none)"}");
                sb.AppendLine($"Messages: {session.Messages.Count}");
            }
            else
            {
                sb.AppendLine("Session: (none)");
            }

            sb.AppendLine($"Tools:   {context.ToolRegistry.GetAll().Count}");
            sb.AppendLine();

            return Task.FromResult(CommandResult.Success(sb.ToString()));
        }
    }
}
