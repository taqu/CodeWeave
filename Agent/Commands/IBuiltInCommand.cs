using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Commands
{
    public interface IBuiltInCommand
    {
        string Name { get; }
        string Description { get; }
        Task<CommandResult> ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken);
    }
}
