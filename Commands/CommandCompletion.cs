using EnvDTE;
using System.Threading;

namespace CodeWeave
{
	[Command(PackageGuids.CodeWeaveString, PackageIds.CommandCompletion)]
	internal class CommandCompletion : BaseCommand<CommandCompletion>
	{
		private CancellationTokenSource cancellationTokenSource_ = new CancellationTokenSource();
		protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CodeWeavePackage package = null;
			if(!CodeWeavePackage.TryGetPackage(out package)){
				return;
			}
			using CodeWeavePackage.LlamaCompletionEngineWrapper engine = package.GetLlamaEngine();
			if(null == engine)
			{
				await Log.OutputAsync("Failed to load AI model. Please check settings.");
				return;
			}
			OptionPage optionPage = package.OptionPage;
			if(null == optionPage)
			{
				await Log.OutputAsync("Failed to get an option page.");
                return;
			}
			CodeUtil.TypeLanguage language = CodeUtil.GetLanguageFromDocument(package.DTE.ActiveDocument);
			if(language != CodeUtil.TypeLanguage.C_Cpp)
			{
				return;
			}
			if(null != cancellationTokenSource_)
			{
				cancellationTokenSource_.Cancel();
                cancellationTokenSource_.Dispose();
			}
			float temperature = optionPage.Temperature;
			int prefixTokens = optionPage.PrefixTokens;
            int suffixTokens = optionPage.SuffixTokens;
            int maxTokens = optionPage.MaxTokens;
			DocumentView documentView = await VS.Documents.GetActiveDocumentViewAsync();
			(string prefix, string suffix, int position) = CodeUtil.GetCodeAround(documentView);
			await Log.OutputAsync("prefix: " + prefix + "\n");
			await Log.OutputAsync("suffix: " + suffix + "\n");

			string response = string.Empty;
			try
			{
                await Log.OutputAsync("Handy Tools: Step 1/3");
				response = await engine.Engine.GenerateAsync(prefix, suffix, prefixTokens, suffixTokens, maxTokens, cancellationTokenSource_.Token);
				await Log.OutputAsync("response: " + response + "\n");
                await Log.OutputAsync("Handy Tools: Step 2/3");
			}
			catch (Exception ex)
			{
				await Log.OutputAsync(ex.Message);
				return;
			}

			documentView.TextBuffer.Insert(position, response);
            await Log.OutputAsync("Handy Tools: Step 3/3");
        }
	}
}

