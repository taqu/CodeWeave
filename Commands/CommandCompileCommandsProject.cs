using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeWeave.Commands
{
    #if false
    [Command(PackageGuids.CodeWeaveString, PackageIds.CommandCompileCommandsProject)]
	internal sealed class CommandCompileCommandsProject : BaseCommand<CommandCompileCommandsProject>
	{
		protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CodeWeavePackage package = null;
			if(!CodeWeavePackage.TryGetPackage(out package)){
				return;
			}
#if false
// 1. ソリューションファイルのフルパスを取得 (例: C:\Projects\MySolution.sln)
    string solutionPath = dte.Solution.FullName;

    // 2. ソリューションファイルのディレクトリパスを算出
    string solutionDir = System.IO.Path.GetDirectoryName(solutionPath);

    // 3. .vs フォルダのパスを生成
    string vsFolderPath = System.IO.Path.Combine(solutionDir, ".vs");

    // 必要に応じて .vs フォルダ内のファイルを取得
    if (System.IO.Directory.Exists(vsFolderPath))
    {
        string[] filesInVs = System.IO.Directory.GetFiles(vsFolderPath, "*.*", System.IO.SearchOption.AllDirectories);
        // filesInVs に .vs フォルダ内の全ファイルパスが格納されます
    }
    #endif
		}
	}
    #endif
}
