using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Session
{
    public class FileSessionStorage : ISessionStorage
    {
        private readonly string rootPath_;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks_ =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        private static readonly JsonSerializerOptions CompactOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        private static readonly JsonSerializerOptions IndentedOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        public FileSessionStorage(string rootPath = "sessions")
        {
            rootPath_ = Path.GetFullPath(rootPath);
        }

        public async Task<Session> CreateAsync(SessionMetadata metadata, CancellationToken cancellationToken = default)
        {
            string dir = GetSessionDir(metadata.Id);
            Directory.CreateDirectory(dir);

            string metaPath = Path.Combine(dir, "session.json");
            await WriteTextAsync(metaPath, JsonSerializer.Serialize(metadata, IndentedOptions)).ConfigureAwait(false);

            string messagesPath = Path.Combine(dir, "messages.jsonl");
            string toolsPath = Path.Combine(dir, "tools.jsonl");
            if (!File.Exists(messagesPath)) File.WriteAllText(messagesPath, string.Empty, Encoding.UTF8);
            if (!File.Exists(toolsPath)) File.WriteAllText(toolsPath, string.Empty, Encoding.UTF8);

            return new Session(metadata, new List<SessionMessage>(), new List<ToolRecord>());
        }

        public async Task<Session> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            string dir = GetSessionDir(sessionId);

            string metaJson = await ReadTextAsync(Path.Combine(dir, "session.json")).ConfigureAwait(false);
            SessionMetadata metadata = JsonSerializer.Deserialize<SessionMetadata>(metaJson, CompactOptions);

            List<SessionMessage> messages = new List<SessionMessage>();
            string messagesPath = Path.Combine(dir, "messages.jsonl");
            if (File.Exists(messagesPath))
            {
                foreach (string line in await ReadLinesAsync(messagesPath, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    SessionMessage msg = JsonSerializer.Deserialize<SessionMessage>(line, CompactOptions);
                    if (msg != null) messages.Add(msg);
                }
            }

            List<ToolRecord> toolHistory = new List<ToolRecord>();
            string toolsPath = Path.Combine(dir, "tools.jsonl");
            if (File.Exists(toolsPath))
            {
                foreach (string line in await ReadLinesAsync(toolsPath, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ToolRecord record = JsonSerializer.Deserialize<ToolRecord>(line, CompactOptions);
                    if (record != null) toolHistory.Add(record);
                }
            }

            return new Session(metadata, messages, toolHistory);
        }

        public async Task AppendMessageAsync(string sessionId, SessionMessage message, CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(GetSessionDir(sessionId), "messages.jsonl");
            await AppendLineAsync(sessionId, path, JsonSerializer.Serialize(message, CompactOptions), cancellationToken).ConfigureAwait(false);
        }

        public async Task AppendToolRecordAsync(string sessionId, ToolRecord record, CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(GetSessionDir(sessionId), "tools.jsonl");
            await AppendLineAsync(sessionId, path, JsonSerializer.Serialize(record, CompactOptions), cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateMetadataAsync(SessionMetadata metadata, CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(GetSessionDir(metadata.Id), "session.json");
            await WriteTextAsync(path, JsonSerializer.Serialize(metadata, IndentedOptions)).ConfigureAwait(false);
        }

        public Task<SessionMetadata> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(rootPath_))
                return Task.FromResult<SessionMetadata>(null);

            SessionMetadata latest = null;
            DateTime latestTime = DateTime.MinValue;

            foreach (string dir in Directory.GetDirectories(rootPath_))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string metaPath = Path.Combine(dir, "session.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    string json = File.ReadAllText(metaPath, Encoding.UTF8);
                    SessionMetadata meta = JsonSerializer.Deserialize<SessionMetadata>(json, CompactOptions);
                    if (meta == null) continue;

                    DateTime time = meta.UpdatedAt != default ? meta.UpdatedAt
                                                              : File.GetLastWriteTimeUtc(metaPath);
                    if (time > latestTime)
                    {
                        latestTime = time;
                        latest = meta;
                    }
                }
                catch { }
            }

            return Task.FromResult(latest);
        }

        public Task<IReadOnlyList<SessionMetadata>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            List<SessionMetadata> result = new List<SessionMetadata>();

            if (!Directory.Exists(rootPath_)) {
                return Task.FromResult<IReadOnlyList<SessionMetadata>>(result);
            }

            foreach (string dir in Directory.GetDirectories(rootPath_))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string metaPath = Path.Combine(dir, "session.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    string json = File.ReadAllText(metaPath, Encoding.UTF8);
                    SessionMetadata meta = JsonSerializer.Deserialize<SessionMetadata>(json, CompactOptions);
                    if (meta != null) result.Add(meta);
                }
                catch
                {
                    // skip corrupt session directories
                }
            }

            result.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            return Task.FromResult<IReadOnlyList<SessionMetadata>>(result);
        }

        public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            string dir = GetSessionDir(sessionId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            locks_.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        private string GetSessionDir(string sessionId) =>
            Path.Combine(rootPath_, sessionId);

        private SemaphoreSlim GetLock(string sessionId) =>
            locks_.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

        private async Task AppendLineAsync(string sessionId, string path, string line, CancellationToken cancellationToken)
        {
            SemaphoreSlim sem = GetLock(sessionId);
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (StreamWriter writer = new StreamWriter(path, append: true, Encoding.UTF8))
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        private static async Task<string> ReadTextAsync(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8)) {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static async Task WriteTextAsync(string path, string content)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(fs, Encoding.UTF8)) {
                await writer.WriteAsync(content).ConfigureAwait(false);
            }
        }

        private static async Task<List<string>> ReadLinesAsync(string path, CancellationToken cancellationToken)
        {
            List<string> lines = new List<string>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lines.Add(line);
                }
            }
            return lines;
        }
    }
}
