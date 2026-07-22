using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfControl
{
    public class ChatConsole : Control
    {
        private TextBox _inputTextBox;
        private List<string> _history = new List<string>();
        private int _historyIndex = -1;
        private int _completionIndex = -1;
        private string _currentCompletionPrefix = "";
        private List<string> _currentCompletions = new List<string>();

        static ChatConsole()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ChatConsole), new FrameworkPropertyMetadata(typeof(ChatConsole)));
        }

        // 汎用カスタムルーティングイベント引数
        public class RoutedRoutedEventArgs<T> : RoutedEventArgs
        {
            public T Value { get; }
            public RoutedRoutedEventArgs(RoutedEvent routedEvent, object source, T value)
                : base(routedEvent, source) => Value = value;
        }

        // 送信コマンドイベント
        public static readonly RoutedEvent CommandSubmittedEvent = EventManager.RegisterRoutedEvent(
            "CommandSubmitted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ChatConsole));

        public event RoutedEventHandler CommandSubmitted
        {
            add => AddHandler(CommandSubmittedEvent, value);
            remove => RemoveHandler(CommandSubmittedEvent, value);
        }

        // ボット用コマンドの補完候補リスト（外部から設定可能）
        public static readonly DependencyProperty AvailableCommandsProperty = DependencyProperty.Register(
            nameof(AvailableCommands), typeof(IEnumerable<string>), typeof(ChatConsole), new PropertyMetadata(null));

        public IEnumerable<string> AvailableCommands
        {
            get => (IEnumerable<string>)GetValue(AvailableCommandsProperty);
            set => SetValue(AvailableCommandsProperty, value);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _inputTextBox = GetTemplateChild("PART_InputTextBox") as TextBox;

            if (_inputTextBox != null)
            {
                _inputTextBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
            }
        }

        private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 1. 送信処理 (Enter のみ = 送信, Shift+Enter = 改行)
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SubmitCommand();
                return;
            }

            // 2. 履歴機能 (Ctrl + Up / Down)
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Up || e.Key == Key.Down))
            {
                e.Handled = true;
                HandleHistory(e.Key == Key.Up);
                return;
            }

            // 3. タブ補完機能 (Tab)
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                HandleTabCompletion();
                return;
            }

            // Tab以外のキーが押されたら補完状態をリセット
            _completionIndex = -1;
        }

        private void SubmitCommand()
        {
            string text = _inputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // 履歴に追加
            if (_history.Count == 0 || _history.Last() != text)
            {
                _history.Add(text);
            }
            _historyIndex = _history.Count;

            // イベント発行
            RaiseEvent(new RoutedRoutedEventArgs<string>(CommandSubmittedEvent, this, text));

            // 入力欄をクリア
            _inputTextBox.Clear();
        }

        private void HandleHistory(bool isUp)
        {
            if (_history.Count == 0) return;

            if (isUp)
            {
                if (_historyIndex > 0) _historyIndex--;
            }
            else
            {
                if (_historyIndex < _history.Count - 1) _historyIndex++;
                else
                {
                    _historyIndex = _history.Count;
                    _inputTextBox.Clear();
                    return;
                }
            }

            _inputTextBox.Text = _history[_historyIndex];
            _inputTextBox.CaretIndex = _inputTextBox.Text.Length; // カーソルを末尾へ
        }

        private void HandleTabCompletion()
        {
            if (AvailableCommands == null || !AvailableCommands.Any()) return;

            // 初回のTab押下時、現在の入力テキストを元に候補を抽出
            if (_completionIndex == -1)
            {
                _currentCompletionPrefix = _inputTextBox.Text.Trim();
                _currentCompletions = AvailableCommands
                    .Where(c => c.StartsWith(_currentCompletionPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!_currentCompletions.Any()) return;
                _completionIndex = 0;
            }
            else
            {
                // 2回目以降のTab押下は次の候補へ
                _completionIndex = (_completionIndex + 1) % _currentCompletions.Count;
            }

            _inputTextBox.Text = _currentCompletions[_completionIndex];
            _inputTextBox.CaretIndex = _inputTextBox.Text.Length;
        }
    }
}
