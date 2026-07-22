using System.Collections.Generic;

namespace CSAgent.Session
{
    public class Session
    {
        private readonly List<SessionMessage> messages_;
        private readonly List<ToolRecord> toolHistory_;

        public SessionMetadata Metadata { get; }
        public IReadOnlyList<SessionMessage> Messages => messages_;
        public IReadOnlyList<ToolRecord> ToolHistory => toolHistory_;

        public Session(SessionMetadata metadata, List<SessionMessage> messages, List<ToolRecord> toolHistory)
        {
            Metadata = metadata;
            messages_ = messages;
            toolHistory_ = toolHistory;
        }

        internal void AddMessage(SessionMessage message) => messages_.Add(message);
        internal void AddToolRecord(ToolRecord record) => toolHistory_.Add(record);
    }
}
