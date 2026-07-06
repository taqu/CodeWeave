using Microsoft.VisualStudio.PlatformUI;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace CodeWeave
{
    public class VcxprojWatcher
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> fileLocks_ = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private FileHashCache fileHashCache_;

        public VcxprojWatcher()
        {
            string solutionDir = Path.GetDirectoryName(VS.Solutions.GetCurrentSolution().FullPath);
            string dbPath = Path.Combine(solutionDir, ".vs", "file_cache.db");
            fileHashCache_ = new FileHashCache(dbPath);
        }

        public void StartWatching()
        {
            _ = ScanExistingProjectsAsync();
        }

        public void StopWatching()
        {
            // セマフォの解放
            foreach (var kvp in fileLocks_)
            {
                kvp.Value.Dispose();
            }
            fileLocks_.Clear();
        }

        /// <summary>
        /// ソリューション内のすべての既存 .vcxproj をスキャンする
        /// </summary>
        public async Task ScanExistingProjectsAsync()
        {
            await Task.Run(async () =>
            {
                // UIスレッドでソリューション内のプロジェクトを取得
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
                string solutionDir = Path.GetDirectoryName(solution.FullPath);
                // ソリューション内のプロジェクトを列挙
                foreach(Project project in await VS.Solutions.GetAllProjectsAsync())
                {
                    if (!await project.IsKindAsync(ProjectTypes.C_PLUS_PLUS))
                    {
                        continue;
                    }
                    _ = RunHeavyProcessWithLockAsync(project);
                }
            });
        }

        /// <summary>
        /// 排他制御（セマフォ）付きの重たい処理の実行ルーチン
        /// </summary>
        private async Task RunHeavyProcessWithLockAsync(Project project)
        {
            Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
            string solutionDir = Path.GetDirectoryName(solution.FullPath);
            string vcxprojPath = project.FullPath;
            string relativePath = PathUtil.MakeRelative(solutionDir, vcxprojPath);

            // ファイルごとのセマフォを取得、なければ生成（同時実行数を1に制限）
            var semaphore = fileLocks_.GetOrAdd(relativePath, _ => new SemaphoreSlim(1, 1));

            await Task.Run(async () =>
            {
                // ロックを獲得する（他のスレッドが処理中の場合はここで待機）
                await semaphore.WaitAsync();
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[排他制御] 処理開始: {relativePath}");
                    bool changed = fileHashCache_.IsChanged(relativePath, vcxprojPath);
                    if (changed)
                    {
                        await CodeWeavePackage.RunExtractCompileCommandsAsync(project);
                        fileHashCache_.UpdateHash(relativePath, vcxprojPath);
                    }
                    System.Diagnostics.Debug.WriteLine($"[排他制御] 処理完了: {relativePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"エラー発生: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }
    }
}

