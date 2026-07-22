using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Session
{
    public interface ISessionStorage
    {
        Task<Session> CreateAsync(SessionMetadata metadata, CancellationToken cancellationToken = default);
        Task<Session> LoadAsync(string sessionId, CancellationToken cancellationToken = default);
        Task AppendMessageAsync(string sessionId, SessionMessage message, CancellationToken cancellationToken = default);
        Task AppendToolRecordAsync(string sessionId, ToolRecord record, CancellationToken cancellationToken = default);
        Task UpdateMetadataAsync(SessionMetadata metadata, CancellationToken cancellationToken = default);
        Task<SessionMetadata> GetLatestAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<SessionMetadata>> EnumerateAsync(CancellationToken cancellationToken = default);
        Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
    }
}
