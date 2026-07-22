using CSAgent;
using CSAgent.Permission;
using MdXaml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfControl;
using static WpfControl.ChatConsole;

namespace CodeWeave
{
    public partial class ToolWindowChatControl : UserControl
    {
        private readonly AgentController controller_;
        private ScrollViewer chatScrollViewer_;
        private readonly Markdown markdown_ = new Markdown();

        public ToolWindowChatControl()
        {
            InitializeComponent();

            controller_ = AppHost.Instance.AgentController;
            if (null == ChatLog.Document)
            {
                ChatLog.Document = new FlowDocument();
            }
            if (controller_ == null)
            {
                ChatLog.AppendText("OpenAI API key is not configured.\nGo to Tools > Options > CodeWeave > OpenAI to set your API key.");
                ChatConsole.IsEnabled = false;
                return;
            }
            controller_.StateChanged += OnStateChanged;
            controller_.StatusChanged += OnStatusChanged;
            controller_.ContentStreamed += OnContentStreamed;
            controller_.ApprovalRequested += OnApprovalRequested;

            ChatConsole.AvailableCommands = controller_.GetAvailableCommands();
        }

        private void OnStateChanged(object sender, AgentStateChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                bool idle = e.State == AgentState.Idle;
                if (idle)
                {
                    AppendMarkdown();
                }
                ChatConsole.IsEnabled = idle;
                CancelButton.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        private void OnStatusChanged(object sender, StatusChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => StatusText.Text = e.Status);
        }

        private void OnContentStreamed(object sender, ContentStreamedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => AppendAndSmartScroll(e.Text));
        }

        private void OnApprovalRequested(object sender, ApprovalRequestedEventArgs e)
        {
            ApprovalResult result = Dispatcher.Invoke(() =>
            {
                Window parentWindow = Window.GetWindow(this);
                var dialog = new ApprovalDialog(e.Invocation) { Owner = parentWindow };
                dialog.ShowDialog();
                return dialog.Result;
            });
            e.SetResult(result);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            controller_?.Cancel();
        }

        private T GetVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); ++i)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                T descendant = GetVisualChild<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            chatScrollViewer_ = GetVisualChild<ScrollViewer>(ChatLog);
        }

        private void AppendMarkdown()
        {
            Paragraph paragraph = ChatLog.Document.Blocks.LastBlock as Paragraph;
            if (null == paragraph)
            {
                return;
            }
            TextRange textRange = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
            string text = textRange.Text;
            text = text.Replace("\r\n", "\n");
            FlowDocument flowDocument = markdown_.Transform(text);
            if (null == flowDocument)
            {
                return;
            }
            Block[] blocksToMove = new Block[flowDocument.Blocks.Count];
            flowDocument.Blocks.CopyTo(blocksToMove, 0);
            flowDocument.Blocks.Clear();
            ChatLog.Document.Blocks.Remove(paragraph);
            ChatLog.Document.Blocks.AddRange(blocksToMove);
        }

        private void AppendAndSmartScroll(string text)
        {
            bool shouldScroll = false;
            if (chatScrollViewer_ != null)
            {
                bool isFocused = ChatLog.IsFocused;
                bool isAtBottom = chatScrollViewer_.VerticalOffset + chatScrollViewer_.ViewportHeight
                    >= chatScrollViewer_.ExtentHeight - 5;
                shouldScroll = isFocused && isAtBottom;
            }
            ChatLog.AppendText(text);
            if (shouldScroll)
            {
                ChatLog.CaretPosition = ChatLog.Document.ContentEnd;
                ChatLog.ScrollToEnd();
            }
        }

        private void ChatConsole_CommandSubmitted(object sender, RoutedEventArgs e)
        {
            if (!(e is RoutedRoutedEventArgs<string> routedEventArgs))
            {
                return;
            }
            string message = routedEventArgs.Value.Trim();
            if (!string.IsNullOrEmpty(message) && controller_ != null)
            {
                _ = controller_.RunAsync(message);
            }
        }
    }
}
