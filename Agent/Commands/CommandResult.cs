namespace CSAgent.Commands
{
    public sealed class CommandResult
    {
        public string Output { get; }

        private CommandResult(string output) { Output = output; }

        public static CommandResult Success(string output) => new CommandResult(output);
    }
}
