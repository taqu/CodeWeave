using System;
using System.IO;
using System.Text;

namespace CSAgent
{
    public class AgentInstructionProvider
    {
        private const string FileName = "AGENT.md";

        private sealed class CacheEntry
        {
            public readonly string Content;
            public readonly DateTime LastWriteTime;

            public CacheEntry(string content, DateTime lastWriteTime)
            {
                Content = content;
                LastWriteTime = lastWriteTime;
            }
        }

        private readonly object lock_ = new object();
        private bool searched_;
        private string filePath_;
        private volatile CacheEntry cache_;

        public string GetInstructions()
        {
            EnsureSearched();

            if (filePath_ == null)
                return string.Empty;

            CacheEntry entry = cache_;
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(filePath_);
                if (entry != null && writeTime == entry.LastWriteTime)
                    return entry.Content;

                lock (lock_)
                {
                    entry = cache_;
                    DateTime writeTime2 = File.GetLastWriteTimeUtc(filePath_);
                    if (entry != null && writeTime2 == entry.LastWriteTime)
                        return entry.Content;

                    try
                    {
                        string content = File.ReadAllText(filePath_, Encoding.UTF8);
                        DateTime wt = File.GetLastWriteTimeUtc(filePath_);
                        cache_ = new CacheEntry(content, wt);
                        return content;
                    }
                    catch
                    {
                        cache_ = null;
                        return string.Empty;
                    }
                }
            }
            catch
            {
                return entry?.Content ?? string.Empty;
            }
        }

        private void EnsureSearched()
        {
            if (searched_) return;

            lock (lock_)
            {
                if (searched_) return;
                filePath_ = FindAgentMd();
                searched_ = true;
            }
        }

        private static string FindAgentMd()
        {
            try
            {
                string dir = Directory.GetCurrentDirectory();
                while (dir != null)
                {
                    string path = Path.Combine(dir, FileName);
                    if (File.Exists(path))
                        return path;

                    DirectoryInfo parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                }
            }
            catch { }

            return null;
        }
    }
}
