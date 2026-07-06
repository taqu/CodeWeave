global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Shell.Interop;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodeWeave
{
    [Guid(PackageGuids.CodeWeaveString)]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids.CodeWindow, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(OptionPage), "CodeWeave", "CodeWeave", 0, 0, true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(CodeWeave.ToolWindowChat.Pane), Style = VsDockStyle.Linked, Window = WindowGuids.SolutionExplorer)]
    public sealed class CodeWeavePackage : ToolkitPackage
    {
        public static bool TryGetPackage(out CodeWeavePackage package)
        {
            if (null != package_ && package_.TryGetTarget(out package))
            {
                return true;
            }
            package = null;
            return false;
        }

        public static void ScanExistingProjects()
        {
            CodeWeavePackage package;
            if (TryGetPackage(out package))
            {
                package.vcxprojWatcher_.StartWatching();
            }
        }

        public static async Task RunExtractCompileCommandsAsync(Community.VisualStudio.Toolkit.Project project)
        {
            CodeWeavePackage package;
            if (TryGetPackage(out package))
            {
                Community.VisualStudio.Toolkit.Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
                string vsPath = Path.Combine(Path.GetDirectoryName(solution.FullPath), ".vs");
                string safeName = Utility.SanitizeFileName(project.Name);
                string outputPath = Path.Combine(vsPath, $"compile_commands_{safeName}.json");
                string projectPath = project.FullPath;
                string cofiguration = await project.GetAttributeAsync("Configuration");
                string platform = await project.GetAttributeAsync("Platform");
                string result = await package.CompileCommandsExtractor.ExtractAsync(projectPath, outputPath, cofiguration, platform, System.Threading.CancellationToken.None);
                await Log.OutputAsync(result);
            }
        }

        public EnvDTE80.DTE2 DTE
        {
            get { return dte2_; }
        }

        public OptionPage OptionPage
        {
            get
            {
                if (optionPage_ == null)
                {
                    optionPage_ = GetDialogPage(typeof(OptionPage)) as OptionPage;
                }
                return optionPage_;
            }
        }

        public CompileCommandsExtractor CompileCommandsExtractor
        {
            get
            {
                if(null == compileCommandsExtractor_)
                {
                    string dllPath = Assembly.GetExecutingAssembly().Location;
                    string extensionDirectory = Path.GetDirectoryName(dllPath);
                    compileCommandsExtractor_ = new CompileCommandsExtractor(Path.Combine(extensionDirectory, "MSBuildExtractor", "bin", "msbuild-extractor.exe"));
                }
                return compileCommandsExtractor_;
            }
        }

        public class LlamaCompletionEngineWrapper : IDisposable
        {
            public LlamaCompletionEngine Engine => engine_;
            private bool disposed_ = false;
            private LlamaCompletionEngine engine_ = null;

            public LlamaCompletionEngineWrapper(LlamaCompletionEngine engine)
            {
                engine_ = engine;
            }

            ~LlamaCompletionEngineWrapper()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!disposed_)
                {
                    if(null != engine_)
                    {
                        if (CodeWeavePackage.TryGetPackage(out CodeWeavePackage package))
                        {
                            package.Release(engine_);
                            engine_ = null;
                        }
                    }
                    disposed_ = true;
                }
            }
        }

        public void Release(LlamaCompletionEngine engine)
        {
            lock (lock_)
            {
                if(null != engine)
                {
                    engine_ = engine;
                }
            }
        }

        public LlamaCompletionEngineWrapper GetLlamaEngine()
        {
            OptionPage optionPage = OptionPage;
            if (null == optionPage)
            {
                return null;
            }
            lock (lock_)
            {
                if (optionPage.ModelPath != engine_.LoadedModelPath)
                {
                    engine_.Load(optionPage.ModelPath, optionPage.NumberOfGpuLayers, optionPage.ContextSize, optionPage.Temperature, optionPage.TopP, optionPage.TopK);
                }
                return new LlamaCompletionEngineWrapper(engine_);
            }
        }

        public AST AST
        {
            get
            {
                return ast_;
            }
        }

        private static WeakReference<CodeWeavePackage> package_;
        private object lock_ = new object();
        private LlamaCompletionEngine engine_ = new LlamaCompletionEngine();
        private EnvDTE80.DTE2 dte2_ = null;
        private OptionPage optionPage_ = null;
        private CompileCommandsExtractor compileCommandsExtractor_ = null;
        private VcxprojWatcher vcxprojWatcher_ = null;
        private RunningDocTableEvents runningDocTableEvents_ = null;
        private AST ast_ = null;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await base.InitializeAsync(cancellationToken, progress);
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
            package_ = new WeakReference<CodeWeavePackage>(this);
            dte2_ = await GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;

            // Explicitly load native DLLs from the extension directory by full path
            string extDir = Path.GetDirectoryName(GetType().Assembly.Location);
            try
            {
                LlamaInterop.LoadNativeDlls(extDir);
                await Log.OutputAsync($"CodeWeave extension loaded. Extension directory: {extDir}");

                // Initialise llama backend once (cheap, idempotent)
                LlamaInterop.llama_backend_init();
                LlamaInterop.Initialized = true;

                {
                    OptionPage optionPage = OptionPage;
                    if (null != optionPage)
                    {
                        string modelPath = System.IO.Path.Combine(extDir, optionPage.ModelPath);
                        engine_.Load(modelPath, optionPage.NumberOfGpuLayers, optionPage.ContextSize, optionPage.Temperature, optionPage.TopP, optionPage.TopK);
                    }
                }
                string dbPath = Path.Combine(Path.GetDirectoryName(dte2_.Solution.FullName), ".vs", "ast_cache.db");
                ast_ = new AST(dbPath, extDir);
            }
            catch (Exception ex)
            {
                await Log.OutputAsync($"Error loading CodeWeave extension: {ex}");
            }
            vcxprojWatcher_ = new VcxprojWatcher();
            vcxprojWatcher_.StartWatching();
            runningDocTableEvents_ = new RunningDocTableEvents(this);
        }

        protected override void Dispose(bool disposing)
        {
            if(null != vcxprojWatcher_)
            {
                vcxprojWatcher_.StopWatching();
                vcxprojWatcher_ = null;
            }
            if(null != engine_)
            {
                engine_.Dispose();
                engine_ = null;
            }
            if(null != ast_)
            {
                ast_.Dispose();
                ast_ = null;
            }
            if (LlamaInterop.Initialized)
            {
                LlamaInterop.llama_backend_free();
                LlamaInterop.Initialized = false;
            }
            base.Dispose(disposing);
        }
    }
}