using System;

namespace CSAgent
{
    public sealed class StatusChangedEventArgs : EventArgs
    {
        public string Status { get; }
        public StatusChangedEventArgs(string status) { Status = status; }
    }
}
