using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Commands.BuiltIn
{
    public sealed class HelpCommand : IBuiltInCommand
    {
        public string Name => "help";
        public string Description => "Show available commands.";

        public Task<CommandResult> ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Available commands:");
            foreach (string cmd in context.Dispatcher.GetAvailableCommands())
                sb.AppendLine($"  {cmd}");
            sb.AppendLine();
            return Task.FromResult(CommandResult.Success(sb.ToString()));
        }
    }
}
