namespace CodeWeave.Commands
{
    [Command(PackageGuids.CodeWeaveString, PackageIds.CommandGetAST)]
    internal sealed class CommandGetAST : BaseCommand<CommandGetAST>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CodeWeavePackage package;
            if (!CodeWeavePackage.TryGetPackage(out package))
            {
                return;
            }
            try
            {
                foreach (SolutionItem solutionItem in await VS.Solutions.GetActiveItemsAsync())
                {
                    switch (solutionItem.Type)
                    {
                        case SolutionItemType.PhysicalFile:
                            await package.AST.QueryAsync(solutionItem, 0, 0);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                await Log.OutputAsync(ex.Message);
            }
        }
    }
}

