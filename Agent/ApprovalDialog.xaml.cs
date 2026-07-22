using CSAgent.Permission;
using System.Windows;

namespace CSAgent
{
    public partial class ApprovalDialog : Window
    {
        public ApprovalResult Result { get; private set; } = ApprovalResult.DenyOnce;

        public ApprovalDialog(ToolInvocation invocation)
        {
            InitializeComponent();
            ToolNameText.Text = invocation.ToolName;
            ArgumentsText.Text = invocation.ArgumentsJson;
        }

        private void AllowOnce_Click(object sender, RoutedEventArgs e)    { Result = ApprovalResult.AllowOnce;   Close(); }
        private void AlwaysAllow_Click(object sender, RoutedEventArgs e)  { Result = ApprovalResult.AlwaysAllow; Close(); }
        private void DenyOnce_Click(object sender, RoutedEventArgs e)     { Result = ApprovalResult.DenyOnce;    Close(); }
        private void AlwaysDeny_Click(object sender, RoutedEventArgs e)   { Result = ApprovalResult.AlwaysDeny;  Close(); }
    }
}
