using CSAgent.LLM;
using System.Threading;
using System.Threading.Tasks;

namespace CSAgent.Tools
{
    public interface ITool
    {
        string Name { get; }
        string Description { get; }
        ToolSchema GetParametersSchema();
        Task<string> ExecuteAsync(string parametersJson, CancellationToken cancellationToken);
    }
}
