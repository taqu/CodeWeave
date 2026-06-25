using System.Windows;
using System.Windows.Controls;

namespace CodeWeave
{
    public partial class ToolWindowChatControl : UserControl
    {
        public ToolWindowChatControl()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, RoutedEventArgs e)
        {
            VS.MessageBox.Show("ToolWindowChatControl", "Button clicked");
        }
    }
}
