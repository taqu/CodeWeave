using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Commands
{
    public sealed class CommandDispatcher : ICommandDispatcher
    {
        private readonly Dictionary<string, IBuiltInCommand> commands_ =
            new Dictionary<string, IBuiltInCommand>(StringComparer.OrdinalIgnoreCase);

        public CommandDispatcher(IEnumerable<IBuiltInCommand> commands)
        {
            foreach (IBuiltInCommand cmd in commands)
                commands_[cmd.Name] = cmd;
        }

        public bool TryDispatch(string input, CommandContext context, CancellationToken cancellationToken, out Task<CommandResult> task)
        {
            if (string.IsNullOrEmpty(input) || input[0] != '/')
            {
                task = null;
                return false;
            }

            string body = input.Substring(1);
            string[] parts = body.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string name = parts.Length > 0 ? parts[0] : string.Empty;

            string[] args = new string[parts.Length > 1 ? parts.Length - 1 : 0];
            for (int i = 1; i < parts.Length; i++)
                args[i - 1] = parts[i];

            if (commands_.TryGetValue(name, out IBuiltInCommand cmd))
            {
                task = cmd.ExecuteAsync(context, args, cancellationToken);
                return true;
            }

            task = Task.FromResult(CommandResult.Success(
                $"Unknown command: /{name}\n\nType /help to view available commands.\n"));
            return true;
        }

        public IReadOnlyList<string> GetAvailableCommands()
        {
            List<string> names = new List<string>(commands_.Count);
            foreach (string key in commands_.Keys)
                names.Add("/" + key);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }
}
