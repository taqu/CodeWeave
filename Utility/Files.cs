using Microsoft.VisualStudio.PlatformUI;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CodeWeave
{
    public static class Files
    {
        private static IEnumerable<SolutionItem> EnumerateFileItems(SolutionItem project)
        {
            if (project == null) {
                yield break;
            }
            foreach (SolutionItem solutionItem in project.Children)
            {
                if (solutionItem.Type == SolutionItemType.Solution
                    || solutionItem.Type == SolutionItemType.Project
                    || solutionItem.Type == SolutionItemType.MiscProject
                    || solutionItem.Type == SolutionItemType.VirtualProject
                    || solutionItem.Type == SolutionItemType.SolutionFolder
                    || solutionItem.Type == SolutionItemType.VirtualFolder
                    || solutionItem.Type == SolutionItemType.PhysicalFolder)
                {
                    foreach (SolutionItem childProject in EnumerateFileItems(solutionItem))
                    {
                        yield return childProject;
                    }
                }
                else if(solutionItem.Type == SolutionItemType.PhysicalFile)
                {
                    yield return solutionItem;
                }
            }
        }

        /// <summary>
        /// SolutionItemからファイルパス取得
        /// </summary>
        private static string? GetFilePath(SolutionItem item)
        {
            try
            {
                if(item.Type == SolutionItemType.PhysicalFile)
                {
                    return item.FullPath;
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// ファイル名だけで検索
        /// </summary>
        public static SolutionItem? FindByFileName(
            Solution solution,
            string fileName)
        {
            return EnumerateFileItems(solution)
                .FirstOrDefault(item =>
                {
                    string? path = GetFilePath(item);
                    return path != null &&
                           string.Equals(
                               Path.GetFileName(path),
                               fileName,
                               StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>
        /// プロジェクト名＋ファイル名
        /// </summary>
        public static async Task<SolutionItem> FindInProjectAsync(string projectName, string fileName)
        {
            IEnumerable<Project> projects = await VS.Solutions.GetAllProjectsAsync();
            Project project = projects.FirstOrDefault(p =>
                    string.Equals(
                        p.Name,
                        projectName,
                        StringComparison.OrdinalIgnoreCase));

            if(project == null) {
                return null;
            }
            return EnumerateFileItems(project)
                .FirstOrDefault(item =>
                {
                    string path = GetFilePath(item);
                    return path != null &&
                           string.Equals(
                               Path.GetFileName(path),
                               fileName,
                               StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>
        /// ソリューションフォルダからの相対パスで検索
        /// </summary>
        public static SolutionItem? FindByRelativePath(string relativePath)
        {
            relativePath = relativePath.Replace('/', '\\').TrimStart('\\');
            Solution? solution = VS.Solutions.GetCurrentSolution();
            if(null == solution) {
                return null;
            }
            string solutionDir = Path.GetDirectoryName(VS.Solutions.GetCurrentSolution().FullPath);
            if(string.IsNullOrEmpty(solutionDir)) {
                return null;
            }
            return EnumerateFileItems(solution)
                .FirstOrDefault(item =>
                {
                    string? path = GetFilePath(item);
                    if (path == null) {
                        return false;
                    }
                    string rel = PathUtil.MakeRelative(solutionDir, path).Replace('/', '\\');

                    return string.Equals(
                        rel,
                        relativePath,
                        StringComparison.OrdinalIgnoreCase);
                });
        }
    }
}
