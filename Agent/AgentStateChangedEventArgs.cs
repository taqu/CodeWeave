using System;

namespace CSAgent
{
    public sealed class AgentStateChangedEventArgs : EventArgs
    {
        public AgentState State { get; }
        public AgentStateChangedEventArgs(AgentState state) { State = state; }
    }
}
