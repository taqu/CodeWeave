using EnvDTE;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeWeave.Commands
{
    [Command(PackageGuids.CodeWeaveString, PackageIds.CommandCompileCommands)]
	internal sealed class CommandCompileCommandsProject : BaseCommand<CommandCompileCommandsProject>
	{
		protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
		{
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CodeWeavePackage package = null;
			if(!CodeWeavePackage.TryGetPackage(out package)){
				return;
            }
            EnvDTE.SelectedItems selectedItems = package.DTE.SelectedItems;
            if(null == selectedItems)
            {
                return;
            }
            foreach(SelectedItem item in selectedItems)
            {
                ProjectItem projectItem = item.ProjectItem;
                Log.Output("Compiling project: " + projectItem.Object.GetType().FullName);
                if(item is EnvDTE.Solution)
                {
                    break;
                }
                if(item is VCProject)
                {
                    EnvDTE.Project project = item as EnvDTE.Project;
                    Log.Output("Compiling project: " + project.Name);
                }
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
}
