using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSAgent
{
    public class History
    {
        private List<string> history_ = new List<string>();
        private int currentIndex_ = -1;
        private string currentInput_ = "";

        // 1セッションあたりの最大保存件数（古いものから自動削除）
        private const int MaxHistoryCount = 100;

        public void Add(string item)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                return;
            }

            history_.Remove(item);
            history_.Add(item);

            if (MaxHistoryCount<history_.Count)
            {
                history_.RemoveAt(0);
            }

            ResetIndex();
        }

        public string MoveUp(string currentBuffer)
        {
            if (history_.Count == 0) return currentBuffer;
            if (currentIndex_ == -1)
            {
                currentInput_ = currentBuffer;
                currentIndex_ = history_.Count - 1;
            }
            else if (currentIndex_ > 0)
            {
                currentIndex_--;
            }
            return history_[currentIndex_];
        }

        public string MoveDown(string currentBuffer)
        {
            if (history_.Count == 0 || currentIndex_ == -1) return currentBuffer;
            if (currentIndex_ < history_.Count - 1)
            {
                currentIndex_++;
                return history_[currentIndex_];
            }
            ResetIndex();
            return currentInput_;
        }

        public string Complete(string currentBuffer, out bool isCompleted)
        {
            isCompleted = false;
            if (string.IsNullOrEmpty(currentBuffer)) return currentBuffer;

            // 最新の履歴から逆引きして補完候補を探す
            for (int i = history_.Count - 1; i >= 0; i--)
            {
                if (history_[i].StartsWith(currentBuffer, StringComparison.OrdinalIgnoreCase))
                {
                    isCompleted = true;
                    return history_[i];
                }
            }
            return currentBuffer;
        }

        public void ResetIndex()
        {
            currentIndex_ = -1;
            currentInput_ = string.Empty;
        }

        public void SaveSession(string filePath)
        {
            try
            {
                string jsonString = JsonSerializer.Serialize(history_);
                File.WriteAllText(filePath, jsonString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"履歴の保存に失敗しました: {ex.Message}");
            }
        }

        public void LoadSession(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string jsonString = File.ReadAllText(filePath);
                    history_ = JsonSerializer.Deserialize<List<string>>(jsonString) ?? new List<string>();
                }
                else
                {
                    history_ = new List<string>();
                }
                ResetIndex();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"履歴の読み込みに失敗しました: {ex.Message}");
                history_ = new List<string>();
            }
        }
    }
}
