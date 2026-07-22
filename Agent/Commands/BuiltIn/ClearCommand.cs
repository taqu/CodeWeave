using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Commands.BuiltIn
{
    public sealed class ClearCommand : IBuiltInCommand
    {
        public string Name => "clear";
        public string Description => "Start a new conversation session.";

        public async Task<CommandResult> ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
        {
            string model = context.SessionManager.CurrentSession?.Metadata.Model;
            await context.SessionManager.CreateSessionAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
            return CommandResult.Success("New session started.\n");
        }
    }
}
