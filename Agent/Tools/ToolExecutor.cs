using System;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools
{
    public class ToolExecutor
    {
        private readonly TimeSpan defaultTimeout_;

        public ToolExecutor(TimeSpan? defaultTimeout = null)
        {
            defaultTimeout_ = defaultTimeout ?? TimeSpan.FromSeconds(30);
        }

        public async Task<string> ExecuteAsync(
            ITool tool,
            string parametersJson,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            TimeSpan effectiveTimeout = timeout ?? defaultTimeout_;
            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(effectiveTimeout))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                try
                {
                    return await tool.ExecuteAsync(parametersJson, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return $"[Error] Tool '{tool.Name}' timed out after {effectiveTimeout.TotalSeconds} seconds.";
                }
                catch (Exception ex)
                {
                    return $"[Error] Tool '{tool.Name}' failed: {ex.Message}";
                }
            }
        }
    }
}
