using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Capabilities
{
    [ServiceLocator(Default = typeof(CapabilitiesManager))]
    public interface ICapabilitiesManager : IAgentService
    {
        Task<Dictionary<string, string>> GetCapabilitiesAsync(AgentSettings settings, CancellationToken token);
    }

    public sealed class CapabilitiesManager : AgentService, ICapabilitiesManager
    {
        public async Task<Dictionary<string, string>> GetCapabilitiesAsync(AgentSettings settings, CancellationToken cancellationToken)
        {
            Trace.Entering();
            ArgUtil.NotNull(settings, nameof(settings));

            // Get the providers.
            var extensionManager = HostContext.GetService<IExtensionManager>();
            IEnumerable<ICapabilitiesProvider> providers =
                extensionManager
                .GetExtensions<ICapabilitiesProvider>()
                ?.OrderBy(x => x.Order);

            // Initialize a dictionary of capabilities.
            var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Add each capability returned from each provider.
            foreach (ICapabilitiesProvider provider in providers ?? new ICapabilitiesProvider[0])
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<Capability> caps = await provider.GetCapabilitiesAsync(settings) ?? new List<Capability>();
                Trace.Info($"Find {caps.Count} capabilities thought {provider.GetType().Name}.");
                foreach (Capability capability in caps)
                {
                    capabilities[capability.Name] = capability.Value;
                }
            }

            return capabilities;
        }
    }

    public interface ICapabilitiesProvider : IExtension
    {
        int Order { get; }

        Task<List<Capability>> GetCapabilitiesAsync(AgentSettings settings);
    }

    public sealed class Capability
    {
        public string Name { get; }
        public string Value { get; }

        public Capability(string name, string value)
        {
            ArgUtil.NotNullOrEmpty(name, nameof(name));
            Name = name;
            Value = value ?? string.Empty;
        }
    }
}
