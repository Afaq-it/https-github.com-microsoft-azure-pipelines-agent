using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Build;
using Microsoft.VisualStudio.Services.Agent.Worker.Release.Artifacts.Definition;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts;

using Newtonsoft.Json;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Release.Artifacts
{
    public class GitHubArtifact : AgentService, IArtifactExtension
    {
        public Type ExtensionType => typeof(IArtifactExtension);
        public AgentArtifactType ArtifactType => AgentArtifactType.GitHub;

        public async Task DownloadAsync(
            IExecutionContext executionContext,
            ArtifactDefinition artifactDefinition,
            string localFolderPath)
        {
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(artifactDefinition, nameof(artifactDefinition));
            ArgUtil.NotNullOrEmpty(localFolderPath, nameof(localFolderPath));

            var gitHubDetails = artifactDefinition.Details as GitHubArtifactDetails;
            ArgUtil.NotNull(gitHubDetails, nameof(gitHubDetails));

            executionContext.Output(StringUtil.Loc("RMReceivedGithubArtifactDetails"));
            ServiceEndpoint endpoint = executionContext.Endpoints.FirstOrDefault((e => string.Equals(e.Name, gitHubDetails.ConnectionName, StringComparison.OrdinalIgnoreCase)));
            if (endpoint == null)
            {
                throw new InvalidOperationException(StringUtil.Loc("RMReceivedGithubArtifactDetails", gitHubDetails.ConnectionName));
            }

            ServiceEndpoint gitHubEndpoint = PrepareGitHubTaskEndpoint(endpoint, gitHubDetails.CloneUrl);
            var extensionManager = HostContext.GetService<IExtensionManager>();
            ISourceProvider sourceProvider = (extensionManager.GetExtensions<ISourceProvider>()).FirstOrDefault(x => x.RepositoryType == WellKnownRepositoryTypes.GitHub);

            if (sourceProvider == null)
            {
                throw new InvalidOperationException(StringUtil.Loc("RMArtifactTypeNotSupported"));
            }

            executionContext.Variables.Set(Constants.Variables.Build.SourcesDirectory, localFolderPath);
            executionContext.Variables.Set(Constants.Variables.Build.SourceBranch, gitHubDetails.Branch);
            executionContext.Variables.Set(Constants.Variables.Build.SourceVersion, artifactDefinition.Version);

            await sourceProvider.GetSourceAsync(executionContext, gitHubEndpoint, executionContext.CancellationToken);
        }

        public IArtifactDetails GetArtifactDetails(
            IExecutionContext context,
            AgentArtifactDefinition agentArtifactDefinition)
        {
            var artifactDetails =
                JsonConvert.DeserializeObject<Dictionary<string, string>>(agentArtifactDefinition.Details);

            string connectionName;
            string repositoryName = string.Empty;
            string branch = string.Empty;

            if (artifactDetails.TryGetValue(ArtifactDefinitionConstants.ConnectionName, out connectionName)
                && artifactDetails.TryGetValue(ArtifactDefinitionConstants.RepositoryId, out repositoryName)
                && artifactDetails.TryGetValue(ArtifactDefinitionConstants.BranchId, out branch))
            {
                ServiceEndpoint gitHubEndpoint = context.Endpoints.FirstOrDefault((e => string.Equals(e.Name, connectionName, StringComparison.OrdinalIgnoreCase)));
                if (gitHubEndpoint == null)
                {
                    throw new InvalidOperationException(StringUtil.Loc("RMGitHubEndpointNotFound", agentArtifactDefinition.Name));
                }

                string accessToken = gitHubEndpoint.Authorization.Parameters[EndpointAuthorizationParameters.AccessToken];
                GitHubRepository repository = HostContext.GetService<IGitHubHttpClient>().GetUserRepo(accessToken, repositoryName);

                Trace.Info($"Found github repository url {repository.Clone_url}");
                return new GitHubArtifactDetails
                {
                    RelativePath = Path.DirectorySeparatorChar.ToString(),
                    ConnectionName = connectionName,
                    CloneUrl = new Uri(repository.Clone_url),
                    Branch = branch
                };
            }
            else
            {
                throw new InvalidOperationException(StringUtil.Loc("RMArtifactDetailsIncomplete", agentArtifactDefinition.Name));
            }
        }

        private static ServiceEndpoint PrepareGitHubTaskEndpoint(ServiceEndpoint taskEndpoint, Uri url)
        {
            var serviceEndpoint = new ServiceEndpoint
            {
                Url = url,
                Authorization = taskEndpoint.Authorization,
                Name = taskEndpoint.Name
            };

            serviceEndpoint.Authorization.Scheme = EndpointAuthorizationSchemes.OAuth;
            return serviceEndpoint;
        }
    }
}