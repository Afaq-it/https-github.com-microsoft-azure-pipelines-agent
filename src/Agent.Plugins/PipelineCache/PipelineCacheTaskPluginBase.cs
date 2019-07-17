using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;

namespace Agent.Plugins.PipelineCache
{
    public abstract class PipelineCacheTaskPluginBase : IAgentTaskPlugin
    {
        private const string SaltVariableName = "AZDEVOPS_PIPELINECACHE_SALT";
        private const string OldKeyFormatMessage = "'key' format is changing to a single line: https://aka.ms/pipeline-caching-docs";

        public Guid Id => PipelineCachePluginConstants.CacheTaskId;

        public abstract String Stage { get; }

        internal static (bool isOldFormat, string[] keySegments,IEnumerable<string[]> restoreKeys) ParseIntoSegments(string salt, string key, string restoreKeysBlock)
        {
            Func<string,string[]> splitIntoSegments = (s) => {
                var segments = s.Split(new [] {'|'},StringSplitOptions.RemoveEmptyEntries).Select(segment => segment.Trim());
                if(!string.IsNullOrWhiteSpace(salt))
                {
                    segments = (new [] { $"{SaltVariableName}={salt}"}).Concat(segments);
                }
                return segments.ToArray();
            };

            Func<string,string[]> splitAcrossNewlines = (s) => 
                s.Replace("\r\n", "\n") //normalize newlines
                 .Split(new [] {'\n'}, StringSplitOptions.RemoveEmptyEntries)
                 .Select(line => line.Trim())
                 .ToArray();
            
            string[] keySegments;
            bool isOldFormat = key.Contains('\n');
            
            IEnumerable<string[]> restoreKeys;
            bool hasRestoreKeys = !string.IsNullOrWhiteSpace(restoreKeysBlock);

            if (isOldFormat && hasRestoreKeys)
            {
                throw new ArgumentException(OldKeyFormatMessage);
            }
            
            if (isOldFormat)
            {
                keySegments = splitAcrossNewlines(key);
            }
            else
            {
                keySegments = splitIntoSegments(key);
            }
            

            if (hasRestoreKeys)
            {
                restoreKeys = splitAcrossNewlines(restoreKeysBlock).Select(restoreKey => splitIntoSegments(restoreKey));
            }
            else
            {
                restoreKeys = Enumerable.Empty<string[]>();
            }

            return (isOldFormat, keySegments, restoreKeys);
        }

        public async Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));

            VariableValue saltValue = context.Variables.GetValueOrDefault(SaltVariableName);
            string salt = saltValue?.Value ?? string.Empty;

            VariableValue workspaceRootValue = context.Variables.GetValueOrDefault("pipeline.workspace");
            string worksapceRoot = workspaceRootValue?.Value;

            string key = context.GetInput(PipelineCacheTaskPluginConstants.Key, required: true);
            string restoreKeysBlock = context.GetInput(PipelineCacheTaskPluginConstants.RestoreKeys, required: false);

            (bool isOldFormat, string[] keySegments, IEnumerable<string[]> restoreKeys) = ParseIntoSegments(salt, key, restoreKeysBlock);

            if (isOldFormat)
            {
                context.Warning(OldKeyFormatMessage);
            }

            context.Output($"Resolving key `{string.Join(" | ", keySegments)}`...");
            Fingerprint keyFp = FingerprintCreator.EvaluateKeyToFingerprint(context, worksapceRoot, keySegments, addWildcard: false);
            context.Output($"Resolved to `{keyFp}`.");

            IEnumerable<Fingerprint> restoreFps = restoreKeys.Select(restoreKey => {
                context.Output($"Resolving restore key `{string.Join(" | ", restoreKey)}`...");
                Fingerprint f = FingerprintCreator.EvaluateKeyToFingerprint(context, worksapceRoot, restoreKey, addWildcard: true);
                context.Output($"Resolved to `{f}`.");
                return f;
            });

            // TODO: Translate path from container to host (Ting)
            string path = context.GetInput(PipelineCacheTaskPluginConstants.Path, required: true);

            await ProcessCommandInternalAsync(
                context,
                (new [] { keyFp }).Concat(restoreFps).ToArray(),
                path,
                token);
        }

        // Process the command with preprocessed arguments.
        protected abstract Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint[] fingerprints,
            string path,
            CancellationToken token);

        // Properties set by tasks
        protected static class PipelineCacheTaskPluginConstants
        {
            public static readonly string Key = "key"; // this needs to match the input in the task.
            public static readonly string RestoreKeys = "restoreKeys";
            public static readonly string Path = "path";
            public static readonly string PipelineId = "pipelineId";
            public static readonly string CacheHitVariable = "cacheHitVar";
            public static readonly string Salt = "salt";

        }
    }
}