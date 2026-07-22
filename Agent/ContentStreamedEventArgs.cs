using System;

namespace CSAgent
{
    public sealed class ContentStreamedEventArgs : EventArgs
    {
        public string Text { get; }
        public ContentStreamedEventArgs(string text) { Text = text; }
    }
}
