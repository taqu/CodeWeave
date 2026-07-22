using Medo;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Session
{
    public class SessionManager
    {
        private readonly ISessionStorage storage_;
        private Session currentSession_;

        public Session CurrentSession => currentSession_;

        public SessionManager(ISessionStorage storage)
        {
            storage_ = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public async Task<Session> CreateSessionAsync(string model = null, string title = null, CancellationToken cancellationToken = default)
        {
            SessionMetadata metadata = new SessionMetadata
            {
                Id = GenerateSessionId(),
                Title = title,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Model = model,
            };
            currentSession_ = await storage_.CreateAsync(metadata, cancellationToken).ConfigureAwait(false);
            return currentSession_;
        }

        public async Task LoadLatestOrCreateAsync(string model = null, CancellationToken cancellationToken = default)
        {
            SessionMetadata latest = null;
            try
            {
                latest = await storage_.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { }

            if (latest != null)
            {
                try
                {
                    currentSession_ = await storage_.LoadAsync(latest.Id, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch { }
            }

            await CreateSessionAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<Session> OpenSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            currentSession_ = await storage_.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return currentSession_;
        }

        public void CloseSession()
        {
            currentSession_ = null;
        }

        public async Task AppendMessageAsync(SessionMessage message, CancellationToken cancellationToken = default)
        {
            if (currentSession_ == null) return;

            string sessionId = currentSession_.Metadata.Id;
            await storage_.AppendMessageAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
            currentSession_.AddMessage(message);

            currentSession_.Metadata.UpdatedAt = DateTime.UtcNow;
            await storage_.UpdateMetadataAsync(currentSession_.Metadata, cancellationToken).ConfigureAwait(false);
        }

        public async Task AppendToolRecordAsync(ToolRecord record, CancellationToken cancellationToken = default)
        {
            if (currentSession_ == null) return;

            await storage_.AppendToolRecordAsync(currentSession_.Metadata.Id, record, cancellationToken).ConfigureAwait(false);
            currentSession_.AddToolRecord(record);
        }

        public Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            return storage_.EnumerateAsync(cancellationToken);
        }

        public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            await storage_.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (currentSession_?.Metadata.Id == sessionId)
                currentSession_ = null;
        }

        private static string GenerateSessionId()
        {
            Uuid7 uuid = Uuid7.NewUuid7();
            return uuid.ToString();
        }
    }
}
