using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Commands
{
    public interface ICommandDispatcher
    {
        bool TryDispatch(string input, CommandContext context, CancellationToken cancellationToken, out Task<CommandResult> task);
        IReadOnlyList<string> GetAvailableCommands();
    }
}
