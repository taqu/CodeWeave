using Microsoft.VisualStudio.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CodeWeave
{
    public class ToolWindowChat : BaseToolWindow<ToolWindowChat>
    {
        public override string GetTitle(int toolWindowId) => "ToolWindowChat";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            return Task.FromResult<FrameworkElement>(new ToolWindowChatControl());
        }

        [Guid("bc4e1321-6763-451d-a66b-38674268e4a1")]
        internal class Pane : ToolkitToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.ToolWindow;
            }
        }
    }
}
