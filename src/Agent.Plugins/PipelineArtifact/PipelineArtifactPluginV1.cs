using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Agent.Sdk;

namespace Agent.Plugins.PipelineArtifact
{
    public abstract class PipelineArtifactTaskPluginBaseV1 : IAgentTaskPlugin
    {
        public abstract Guid Id { get; }
        public string Version => "1.0.0"; // Publish and Download tasks will be always on the same version.
        public string Stage => "main";

        public Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            return this.ProcessCommandInternalAsync(context, token);
        }

        protected abstract Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context, 
            CancellationToken token);

        // Properties set by tasks
        protected static class ArtifactEventProperties
        {
            public static readonly string BuildType = "buildType";
            public static readonly string Project = "project";
            public static readonly string BuildPipelineDefinition = "definition";
            public static readonly string BuildTriggering = "specificBuildWithTriggering";
            public static readonly string BuildVersionToDownload = "buildVersionToDownload";
            public static readonly string BranchName = "branchName";
            public static readonly string BuildId = "buildId";
            public static readonly string Tags = "tags";
            public static readonly string ArtifactName = "artifactName";
            public static readonly string ItemPattern = "itemPattern";
            public static readonly string DownloadPath = "downloadPath";
        }
    }

    // Can be invoked from a build run or a release run should a build be set as the artifact. 
    public class DownloadPipelineArtifactTaskV1 : PipelineArtifactTaskPluginBaseV1
    {
        // Same as https://github.com/Microsoft/vsts-tasks/blob/master/Tasks/DownloadPipelineArtifactV1/task.json
        public override Guid Id => PipelineArtifactPluginConstants.DownloadPipelineArtifactTaskId;

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context, 
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            string buildType = context.GetInput(ArtifactEventProperties.BuildType, required: true);
            string project = context.GetInput(ArtifactEventProperties.Project, required: false);
            string buildPipelineDefinition = context.GetInput(ArtifactEventProperties.BuildPipelineDefinition, required: false);
            string buildTriggering = context.GetInput(ArtifactEventProperties.BuildTriggering, required: false);
            string buildVersionToDownload = context.GetInput(ArtifactEventProperties.BuildVersionToDownload, required: false);
            string branchName = context.GetInput(ArtifactEventProperties.BranchName, required: false);
            string userSpecifiedBuildId = context.GetInput(ArtifactEventProperties.BuildId, required: false);
            string tags = context.GetInput(ArtifactEventProperties.Tags, required: false);
            string artifactName = context.GetInput(ArtifactEventProperties.ArtifactName, required: true);
            string itemPattern = context.GetInput(ArtifactEventProperties.ItemPattern, required: false);
            string downloadPath = context.GetInput(ArtifactEventProperties.DownloadPath, required: true);
            string environmentBuildId = context.Variables.GetValueOrDefault(BuildVariables.BuildId)?.Value ?? string.Empty; // BuildID provided by environment.

            string[] minimatchPatterns = itemPattern.Split(
                new[] { "\n" },
                StringSplitOptions.None
            );

            string[] tagsInput = tags.Split(
                new[] { "," },
                StringSplitOptions.None
            );

            if (buildType == "current")
            {
                // TODO: use a constant for project id, which is currently defined in Microsoft.VisualStudio.Services.Agent.Constants.Variables.System.TeamProjectId (Ting)
                string guidStr = context.Variables.GetValueOrDefault("system.teamProjectId")?.Value;
                Guid projectId = Guid.Parse(guidStr);
                ArgUtil.NotEmpty(projectId, nameof(projectId));

                int buildId = 0;
                if (int.TryParse(environmentBuildId, out buildId) && buildId != 0)
                {
                    context.Output(StringUtil.Loc("DownloadingFromBuild", buildId));
                }
                else
                {
                    string hostType = context.Variables.GetValueOrDefault("system.hosttype")?.Value;
                    if (string.Equals(hostType, "Release", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hostType, "DeploymentGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(StringUtil.Loc("BuildIdIsNotAvailable", hostType ?? string.Empty));
                    }
                    else if (!string.Equals(hostType, "Build", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(StringUtil.Loc("CannotDownloadFromCurrentEnvironment", hostType ?? string.Empty));
                    }
                    else
                    {
                        // This should not happen since the build id comes from build environment. But a user may override that so we must be careful.
                        throw new ArgumentException(StringUtil.Loc("BuildIdIsNotValid", environmentBuildId));
                    }
                }
                // Create the directory if absent
                string fullPath = this.CreateDirectoryIfDoesntExist(downloadPath);

                context.Output(StringUtil.Loc("DownloadArtifactTo", downloadPath));
                PipelineArtifactServer server = new PipelineArtifactServer();
                await server.DownloadAsyncMinimatch(context, projectId, buildId, artifactName, downloadPath, minimatchPatterns, token);
                context.Output(StringUtil.Loc("DownloadArtifactFinished"));
            }
            else if (buildType == "specific")
            {
                int buildId;
                if (buildVersionToDownload == "latest")
                {
                    buildId = await this.GetBuildIdAsync(context, buildPipelineDefinition, buildVersionToDownload, project, tagsInput);
                }
                else if (buildVersionToDownload == "specific")
                {
                    buildId = Int32.Parse(userSpecifiedBuildId);
                }
                else if (buildVersionToDownload == "latestFromBranch")
                {
                    buildId = await this.GetBuildIdAsync(context, buildPipelineDefinition, buildVersionToDownload, project, tagsInput, branchName);
                }
                else
                {
                    throw new InvalidOperationException("Unreachable code!");
                }
                string fullPath = this.CreateDirectoryIfDoesntExist(downloadPath);

                context.Output(StringUtil.Loc("DownloadArtifactTo", downloadPath));
                var server = new PipelineArtifactServer();
                await server.DownloadAsyncWithProjectNameMiniMatch(context, project, buildId, artifactName, downloadPath, minimatchPatterns, token);
                context.Output(StringUtil.Loc("DownloadArtifactFinished"));
            }
            else
            {
                throw new InvalidOperationException("Unreachable code!");
            }
        }

        private string CreateDirectoryIfDoesntExist(string downloadPath)
        {
            string fullPath = Path.GetFullPath(downloadPath);
            bool dirExists = Directory.Exists(fullPath);
            if (!dirExists)
            {
                Directory.CreateDirectory(fullPath);
            }
            return fullPath;
        }

        private async Task<int> GetBuildIdAsync(AgentTaskPluginExecutionContext context, string buildPipelineDefinition, string buildVersionToDownload, string project, string[] tagFilters, string branchName=null)
        {
            var definitions = new List<int>() { Int32.Parse(buildPipelineDefinition) };
            VssConnection connection = context.VssConnection;
            BuildHttpClient buildHttpClient = connection.GetClient<BuildHttpClient>();
            List<Build> list;
            if (buildVersionToDownload == "latest")
            {
                list = await buildHttpClient.GetBuildsAsync(project, definitions, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending);
            }
            else if (buildVersionToDownload == "latestFromBranch")
            {
                list = await buildHttpClient.GetBuildsAsync(project, definitions, branchName: branchName, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending);
            }
            else
            {
                throw new InvalidOperationException("Unreachable code!");
            }

            if (list.Any())
            {
                return list.First().Id;
            }
            else
            {
                throw new ArgumentException("No builds currently exist in the build definition supplied.");
            }
        }
    }
}