using System.Collections.Generic;
using System.Linq;

namespace CSAgent.Tools
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, ITool> tools_ = new Dictionary<string, ITool>();

        public void Register(ITool tool)
        {
            tools_[tool.Name] = tool;
        }

        public bool TryResolve(string name, out ITool tool)
        {
            return tools_.TryGetValue(name, out tool);
        }

        public IReadOnlyList<ITool> GetAll()
        {
            return tools_.Values.ToList();
        }
    }
}
