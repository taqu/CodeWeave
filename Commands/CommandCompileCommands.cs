using EnvDTE;
using System.IO;
using System.Linq;

namespace CodeWeave
{
    [Command(PackageGuids.CodeWeaveString, PackageIds.CommandCompileCommands)]
    internal sealed class CommandCompileCommandsProject : BaseCommand<CommandCompileCommandsProject>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            CodeWeavePackage package;
            if (!CodeWeavePackage.TryGetPackage(out package))
            {
                return;
            }
            Community.VisualStudio.Toolkit.Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
            string vsPath = Path.Combine(Path.GetDirectoryName(solution.FullPath), ".vs");
            try
            {
                foreach (SolutionItem solutionItem in await VS.Solutions.GetActiveItemsAsync())
                {
                    switch (solutionItem.Type)
                    {
                        case SolutionItemType.Solution:
                            {
                                //Community.VisualStudio.Toolkit.Solution solution = solutionItem as Community.VisualStudio.Toolkit.Solution;
                                string config = (string)package.DTE.Solution.Properties.Item("ActiveConfig").Value;
                                string[] configNames = config.Split(new[] { '|' });
                                string cofiguration = "Release";
                                string platform = "x64";
                                if (0 < configNames.Length)
                                {
                                    cofiguration = configNames[0];
                                    if (1 < configNames.Length)
                                    {
                                        platform = configNames[1];
                                    }
                                }
                                string solutionPath = (solutionItem as Community.VisualStudio.Toolkit.Solution).FullPath;
                                string result = await package.CompileCommandsExtractor.ExtractAsync(solutionPath, vsPath, cofiguration, platform, System.Threading.CancellationToken.None);
                                await Log.OutputAsync(result);
                            }
                            break;
                        case SolutionItemType.Project:
                            {
                                Community.VisualStudio.Toolkit.Project project = solutionItem as Community.VisualStudio.Toolkit.Project;
                                string outputPath = Path.Combine(vsPath, Utility.CompileCommandsName(project.Name));
                                string projectPath = project.FullPath;
                                string cofiguration = await project.GetAttributeAsync("Configuration");
                                string platform = await project.GetAttributeAsync("Platform");
                                string result = await package.CompileCommandsExtractor.ExtractAsync(projectPath, outputPath, cofiguration, platform, System.Threading.CancellationToken.None);
                                await Log.OutputAsync(result);
                            }
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
