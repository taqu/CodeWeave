using System.Collections.Generic;
using System.Threading;

namespace CSAgent.LLM
{
    public interface ILLMClient
    {
        IAsyncEnumerable<LLMStreamDelta> StreamAsync(
            IReadOnlyList<LLMMessage> messages,
            IReadOnlyList<LLMTool> tools,
            LLMOptions options,
            CancellationToken cancellationToken);
    }
}
