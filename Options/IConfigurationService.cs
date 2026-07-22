using System;

namespace CSAgent
{
    public interface IConfigurationService
    {
        string OpenAIApiKey { get; }

        string ModelName { get; }

        TimeSpan ToolTimeout { get; }
    }
}
