using CSAgent;
using System;

namespace CodeWeave
{
    public sealed class ConfigurationService : IConfigurationService
    {
        private readonly CodeWeavePackage package_;

        public ConfigurationService(CodeWeavePackage package)
        {
            package_ = package ?? throw new ArgumentNullException(nameof(package));
        }

        public string OpenAIApiKey => package_.OptionPage?.OpenAIApiKey;

        public string ModelName => "gpt-4o-mini";

        public TimeSpan ToolTimeout => TimeSpan.FromSeconds(package_.OptionPage?.Timeout ?? 30);
    }
}
