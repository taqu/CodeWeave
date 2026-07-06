using Microsoft.VisualStudio.PlatformUI;
using System.IO;
using System.Runtime.InteropServices;

namespace CodeWeave
{
    public sealed class AST : IDisposable
    {
#if DEBUG
        public const string Dll = "astd.dll";
#else
        public const string Dll = "ast.dll";
#endif
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr AstEngine_Create(string db_path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AstEngine_Destroy(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr AstEngine_Query(IntPtr handle, string compile_commands, string temp_dir, string filepath, string relative_path, uint line, uint column);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AstEngine_FreeString(IntPtr p);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        public static void LoadNativeDlls(string extDir)
        {
            string fullPath = System.IO.Path.Combine(extDir, "ast", Dll);
            if (LoadLibraryW(fullPath) == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"LoadLibrary failed for: {fullPath}");
            }
        }

        private IntPtr ast_;

        public AST(string db_path, string extDir)
        {
            LoadNativeDlls(extDir);
            ast_ = AstEngine_Create(db_path);
        }

        public void Dispose()
        {
            if(IntPtr.Zero != ast_)
            {
                AstEngine_Destroy(ast_);
                ast_ = IntPtr.Zero;
            }
        }

        public static Project GetParentProject(SolutionItem item)
        {
            if(item == null)
            {
                return null;
            }
            if(item.Type == SolutionItemType.Project
                || item.Type == SolutionItemType.VirtualProject
                || item.Type == SolutionItemType.MiscProject)
            {
                return item as Project;
            }
            else
            {
                return GetParentProject(item.Parent);
            }
        }

        public string Query(string compile_commands, string temp_dir, string filepath, string relative_path, uint line, uint column)
        {
            if(IntPtr.Zero == ast_)
            {
                return string.Empty;
            }
            IntPtr jsonPtr = AstEngine_Query(ast_, compile_commands, temp_dir, filepath, relative_path, line, column);
            string json = Marshal.PtrToStringAnsi(jsonPtr)!;
            AstEngine_FreeString(jsonPtr);
            return json;
        }

        public async System.Threading.Tasks.Task<string> QueryAsync(SolutionItem item, uint line, uint column)
        {
            if(IntPtr.Zero == ast_)
            {
                return string.Empty;
            }
            Project project = GetParentProject(item);
            if(project == null)
            {
                return string.Empty;
            }
            Community.VisualStudio.Toolkit.Solution solution = await VS.Solutions.GetCurrentSolutionAsync();
            string vsPath = Path.Combine(Path.GetDirectoryName(solution.FullPath), ".vs");
            string compile_commands = Path.Combine(vsPath, Utility.CompileCommandsName(project.Name));
            string projectPath = project.FullPath;
            string relative_path = PathUtil.MakeRelative(solution.FullPath, item.FullPath);
            return Query(compile_commands, vsPath, item.FullPath, relative_path, line, column);
        }
    }
}
