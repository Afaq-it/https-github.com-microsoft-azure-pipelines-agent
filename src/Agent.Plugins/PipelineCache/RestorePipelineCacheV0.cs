using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;

namespace Agent.Plugins.PipelineCache
{    
    public class RestorePipelineCacheV0 : PipelineCacheTaskPluginBase
    {
        public override string Stage => "main";

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint fingerprint,
            Func<Fingerprint[]> restoreKeysGenerator,
            string path,
            bool isTar,
            CancellationToken token)
        {
            context.SetTaskVariable(RestoreStepRanVariableName, RestoreStepRanVariableValue);

            var server = new PipelineCacheServer();
            Fingerprint[] restoreFingerprints = restoreKeysGenerator();
            await server.DownloadAsync(
                context, 
                (new [] { fingerprint}).Concat(restoreFingerprints).ToArray(),
                path,
                context.GetInput(PipelineCacheTaskPluginConstants.CacheHitVariable, required: false),
                isTar,
                token);
        }
    }
}