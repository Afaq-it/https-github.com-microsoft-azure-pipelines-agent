using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public sealed class BuildJobExtension : AgentService, IJobExtension
    {
        public Type ExtensionType => typeof(IJobExtension);
        public IList<string> HostTypes => new List<string> { "build" };
        public IStep PrepareStep { get; private set; }
        public IStep FinallyStep { get; private set; }
        private ServiceEndpoint SourceEndpoint { set; get; }
        private ISourceProvider SourceProvider { set; get; }

        public BuildJobExtension()
        {
            PrepareStep = new JobExtensionRunner(
                runAsync: PrepareAsync,
                alwaysRun: false,
                continueOnError: false,
                critical: true,
                displayName: StringUtil.Loc("GetSources"),
                enabled: true,
                @finally: false);

            FinallyStep = new JobExtensionRunner(
                runAsync: FinallyAsync,
                alwaysRun: false,
                continueOnError: false,
                critical: false,
                displayName: StringUtil.Loc("Cleanup"),
                enabled: true,
                @finally: true);
        }

        // 1. use source provide to solve path, if solved result is rooted, return full path.
        // 2. prefix default path root (build.sourcesDirectory), if result is rooted, return full path.
        public string GetRootedPath(IExecutionContext context, string path)
        {
            string rootedPath = null;

            if (SourceProvider != null && SourceEndpoint != null)
            {
                path = SourceProvider.GetLocalPath(context, SourceEndpoint, path) ?? string.Empty;
                Trace.Info($"Build JobExtension resolving path use source provide: {path}");

                if (!string.IsNullOrEmpty(path) &&
                    path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                    Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Path resolved by source provider is a rooted path, return absolute path: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Info($"Path resolved by source provider is a rooted path, but it is not a full qualified path: {path}");
                        Trace.Error(ex);
                    }
                }
            }

            string defaultPathRoot = context.Variables.Get(Constants.Variables.Build.SourcesDirectory) ?? string.Empty;
            Trace.Info($"The Default Path Root of Build JobExtension is build.sourcesDirectory: {defaultPathRoot}");

            if (defaultPathRoot != null && defaultPathRoot.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                path != null && path.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            {
                path = Path.Combine(defaultPathRoot, path);
                Trace.Info($"After prefix Default Path Root provide by JobExtension: {path}");
                if (Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Return absolute path after prefix DefaultPathRoot: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Error(ex);
                        Trace.Info($"After prefix Default Path Root provide by JobExtension, the Path is a rooted path, but it is not full qualified, return the path: {path}.");
                        return path;
                    }
                }
            }

            return rootedPath;
        }

        public void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath)
        {
            repoName = "";

            // If no repo was found, send back an empty repo with original path.
            sourcePath = localPath;

            if (!string.IsNullOrEmpty(localPath) &&
                File.Exists(localPath) &&
                SourceEndpoint != null &&
                SourceProvider != null)
            {
                // If we found a repo, calculate the relative path to the file
                repoName = SourceEndpoint.Name;
                sourcePath = IOUtil.MakeRelative(localPath, context.Variables.Get(Constants.Variables.Build.SourcesDirectory));
            }
        }

        private async Task PrepareAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(PrepareStep, nameof(PrepareStep));
            ArgUtil.NotNull(PrepareStep.ExecutionContext, nameof(PrepareStep.ExecutionContext));
            IExecutionContext executionContext = PrepareStep.ExecutionContext;
            var directoryManager = HostContext.GetService<IBuildDirectoryManager>();

            // This flag can be false for jobs like cleanup artifacts.
            // If syncSources = false, we will not set source related build variable, not create build folder, not sync source.
            bool syncSources = executionContext.Variables.Build_SyncSources ?? true;
            if (!syncSources)
            {
                Trace.Info($"{Constants.Variables.Build.SyncSources} = false, we will not set source related build variable, not create build folder and not sync source");
                return;
            }

            // Get the repo endpoint and source provider.
            if (!TrySetPrimaryEndpointAndProviderInfo(executionContext))
            {
                throw new Exception(StringUtil.Loc("SupportedRepositoryEndpointNotFound"));
            }

            executionContext.Debug($"Primary repository: {SourceEndpoint.Name}. repository type: {SourceProvider.RepositoryType}");

            // Prepare the build directory.
            executionContext.Debug("Preparing build directory.");
            TrackingConfig trackingConfig = directoryManager.PrepareDirectory(
                executionContext,
                SourceEndpoint,
                SourceProvider);

            // Set the directory variables.
            executionContext.Debug("Set build variables.");
            string _workDirectory = IOUtil.GetWorkPath(HostContext);
            executionContext.Variables.Set(Constants.Variables.Agent.BuildDirectory, Path.Combine(_workDirectory, trackingConfig.BuildDirectory));
            executionContext.Variables.Set(Constants.Variables.System.ArtifactsDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory));
            executionContext.Variables.Set(Constants.Variables.System.DefaultWorkingDirectory, Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));
            executionContext.Variables.Set(Constants.Variables.Common.TestResultsDirectory, Path.Combine(_workDirectory, trackingConfig.TestResultsDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.BinariesDirectory, Path.Combine(_workDirectory, trackingConfig.BuildDirectory, Constants.Build.Path.BinariesDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.SourcesDirectory, Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.StagingDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory));
            executionContext.Variables.Set(Constants.Variables.Build.ArtifactStagingDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory));

            // Set the repo variables.
            string repositoryId;
            if (SourceEndpoint.Data.TryGetValue("repositoryId", out repositoryId)) // TODO: Move to const after source artifacts PR is merged.
            {
                executionContext.Variables.Set(Constants.Variables.Build.RepoId, repositoryId);
            }

            executionContext.Variables.Set(Constants.Variables.Build.RepoName, SourceEndpoint.Name);
            executionContext.Variables.Set(Constants.Variables.Build.RepoProvider, SourceEndpoint.Type);
            executionContext.Variables.Set(Constants.Variables.Build.RepoUri, SourceEndpoint.Url?.AbsoluteUri);
            executionContext.Variables.Set(Constants.Variables.Build.RepoLocalPath, Path.Combine(_workDirectory, trackingConfig.SourcesDirectory));

            string checkoutSubmoduleText;
            if (SourceEndpoint.Data.TryGetValue(WellKnownEndpointData.CheckoutSubmodules, out checkoutSubmoduleText))
            {
                executionContext.Variables.Set(Constants.Variables.Build.RepoGitSubmoduleCheckout, checkoutSubmoduleText);
            }

            // overwrite primary repository's clean value if build.repository.clean is sent from server. this is used by tfvc gated check-in
            bool? repoClean = executionContext.Variables.GetBoolean(Constants.Variables.Build.RepoClean);
            if (repoClean != null)
            {
                SourceEndpoint.Data[WellKnownEndpointData.Clean] = repoClean.Value.ToString();
            }
            else
            {
                string cleanRepoText;
                if (SourceEndpoint.Data.TryGetValue(WellKnownEndpointData.Clean, out cleanRepoText))
                {
                    executionContext.Variables.Set(Constants.Variables.Build.RepoClean, cleanRepoText);
                }
            }

            SourceProvider.SetVariablesInEndpoint(executionContext, SourceEndpoint);

            // Read skipSyncSource property fron endpoint data
            string skipSyncSourceText;
            bool skipSyncSource = false;
            if (SourceEndpoint.Data.TryGetValue("skipSyncSource", out skipSyncSourceText))
            {
                skipSyncSource = StringUtil.ConvertToBoolean(skipSyncSourceText, false);
            }

            // Prefer feature variable over endpoint data
            skipSyncSource = executionContext.Variables.GetBoolean(Constants.Variables.Features.SkipSyncSource) ?? skipSyncSource;

            if (skipSyncSource)
            {
                executionContext.Output($"Skip sync source for endpoint: {SourceEndpoint.Name}");
            }
            else
            {
                executionContext.Debug($"Sync source for endpoint: {SourceEndpoint.Name}");
                await SourceProvider.GetSourceAsync(executionContext, SourceEndpoint, executionContext.CancellationToken);
            }
        }

        private async Task FinallyAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(FinallyStep, nameof(FinallyStep));
            ArgUtil.NotNull(FinallyStep.ExecutionContext, nameof(FinallyStep.ExecutionContext));
            IExecutionContext executionContext = FinallyStep.ExecutionContext;

            // If syncSources = false, we will not reset repository.
            bool syncSources = executionContext.Variables.Build_SyncSources ?? true;

            // Read skipSyncSource property fron endpoint data
            string skipSyncSourceText;
            bool skipSyncSource = false;
            if (SourceEndpoint.Data.TryGetValue("skipSyncSource", out skipSyncSourceText))
            {
                skipSyncSource = StringUtil.ConvertToBoolean(skipSyncSourceText, false);
            }

            // Prefer feature variable over endpoint data
            skipSyncSource = executionContext.Variables.GetBoolean(Constants.Variables.Features.SkipSyncSource) ?? skipSyncSource;

            if (!syncSources || skipSyncSource)
            {
                Trace.Verbose($"{Constants.Variables.Build.SyncSources} = false, we will not run post job cleanup for this repository");
                return;
            }

            await SourceProvider.PostJobCleanupAsync(executionContext, SourceEndpoint);
        }

        private bool TrySetPrimaryEndpointAndProviderInfo(IExecutionContext executionContext)
        {
            // Return the first service endpoint that contains a supported source provider.
            Trace.Entering();
            var extensionManager = HostContext.GetService<IExtensionManager>();
            List<ISourceProvider> sourceProviders = extensionManager.GetExtensions<ISourceProvider>();
            foreach (ServiceEndpoint ep in executionContext.Endpoints)
            {
                SourceProvider = sourceProviders
                    .FirstOrDefault(x => string.Equals(x.RepositoryType, ep.Type, StringComparison.OrdinalIgnoreCase));
                if (SourceProvider != null)
                {
                    SourceEndpoint = ep.Clone();
                    return true;
                }
            }

            return false;
        }
    }
}