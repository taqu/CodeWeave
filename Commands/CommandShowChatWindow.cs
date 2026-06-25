namespace CodeWeave
{
    [Command(PackageGuids.CodeWeaveString, PackageIds.CommandShowChatWindow)]
	internal sealed class CommandShowChatWindow : BaseCommand<CommandShowChatWindow>
	{
		protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
		{
			 await ToolWindowChat.ShowAsync();
		}
	}
}
