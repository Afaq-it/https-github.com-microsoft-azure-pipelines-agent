﻿using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Common;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    public interface IConfigurationProvider : IExtension, IAgentService
    {
        string ConfigurationProviderType { get; }

        string GetServerUrl(CommandSettings command);

        Task TestConnectionAsync(string tfsUrl, VssCredentials creds);

        Task<int> GetPoolId(CommandSettings command);

        string GetFailedToFindPoolErrorString();

        Task<TaskAgent> UpdateAgentAsync(int poolId, TaskAgent agent, CommandSettings command);

        Task<TaskAgent> AddAgentAsync(int poolId, TaskAgent agent, CommandSettings command);

        Task DeleteAgentAsync(int agentPoolId, int agentId);

        void UpdateAgentSetting(AgentSettings settings);

        Task<TaskAgent> GetAgentAsync(int poolId, string agentName);

        void ReadAgentSetting(AgentSettings settings);
    }

    public sealed class BuildReleasesAgentConfigProvider : AgentService, IConfigurationProvider
    {
        public Type ExtensionType => typeof(IConfigurationProvider);
        private ITerminal _term;
        private IAgentServer _agentServer;

        public string ConfigurationProviderType
            => Constants.Agent.AgentConfigurationProvider.BuildReleasesAgentConfiguration;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _term = hostContext.GetService<ITerminal>();
            _agentServer = HostContext.GetService<IAgentServer>();
        }

        public void ReadAgentSetting(AgentSettings settings)
        {
            // No implementation required
        }

        public void UpdateAgentSetting(AgentSettings settings)
        {
            // No implementation required
        }

        public string GetServerUrl(CommandSettings command)
        {
            return command.GetUrl();
        }

        public async Task<int> GetPoolId(CommandSettings command)
        {
            int poolId = 0;
            string poolName;

            poolName = command.GetPool();
            poolId = await GetPoolIdAsync(poolName);
            Trace.Info($"PoolId for agent pool '{poolName}' is '{poolId}'.");
 
            return poolId;
        }

        public string GetFailedToFindPoolErrorString() => StringUtil.Loc("FailedToFindPool");

        public Task<TaskAgent> UpdateAgentAsync(int poolId, TaskAgent agent, CommandSettings command)
        {
            return _agentServer.UpdateAgentAsync(poolId, agent);
        }

        public Task<TaskAgent> AddAgentAsync(int poolId, TaskAgent agent, CommandSettings command)
        { 
            return _agentServer.AddAgentAsync(poolId, agent);
        }

        public Task DeleteAgentAsync(int agentPoolId, int agentId)
        {
            return _agentServer.DeleteAgentAsync(agentPoolId, agentId);
        }

        public async Task TestConnectionAsync(string url, VssCredentials creds)
        {
            _term.WriteLine(StringUtil.Loc("ConnectingToServer"));
            VssConnection connection = ApiUtil.CreateConnection(new Uri(url), creds);

            await _agentServer.ConnectAsync(connection);
        }

        public async Task<TaskAgent> GetAgentAsync(int poolId, string agentName)
        {
            var agents = await _agentServer.GetAgentsAsync(poolId, agentName);
            Trace.Verbose("Returns {0} agents", agents.Count);
            return agents.FirstOrDefault();
        }

        private async Task<int> GetPoolIdAsync(string poolName)
        {
            TaskAgentPool agentPool = (await _agentServer.GetAgentPoolsAsync(poolName)).FirstOrDefault();
            if (agentPool == null)
            {
                throw new TaskAgentPoolNotFoundException(StringUtil.Loc("PoolNotFound", poolName));
            }
            else
            {
                Trace.Info("Found pool {0} with id {1}", poolName, agentPool.Id);
                return agentPool.Id;
            }
        }
    }

    public sealed class DeploymentGroupAgentConfigProvider : AgentService, IConfigurationProvider
    {
        public Type ExtensionType => typeof(IConfigurationProvider);
        private ITerminal _term;

        private string _projectName = string.Empty;
        private string _collectionName;
        private int _deploymentGroupId;
        private string _serverUrl;
        private bool _isHosted = false;
        private IDeploymentGroupServer _deploymentGroupServer = null;

        public string ConfigurationProviderType
            => Constants.Agent.AgentConfigurationProvider.DeploymentAgentConfiguration;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _term = hostContext.GetService<ITerminal>();
            _deploymentGroupServer = HostContext.GetService<IDeploymentGroupServer>();
        }

        public void ReadAgentSetting(AgentSettings settings)
        {
            _projectName = settings.ProjectName;
            _deploymentGroupId = settings.DeploymentGroupId;
            _collectionName = settings.CollectionName;
        }

        public string GetServerUrl(CommandSettings command)
        {
            _serverUrl =  command.GetUrl();
            Trace.Info("url - {0}", _serverUrl);

            _isHosted = UrlUtil.IsHosted(_serverUrl);

            // for onprem tfs, collection is required for deploymentGroup
            if (! _isHosted)
            {
                Trace.Info("Provided url is for onprem tfs, need collection name");
                _collectionName = command.GetCollectionName();
            }

            return _serverUrl;
        }

        public async Task<int> GetPoolId(CommandSettings command)
        {
            int poolId;

            _projectName = command.GetProjectName(_projectName);
            var deploymentGroupName = command.GetDeploymentGroupName();

            poolId =  await GetPoolIdAsync(_projectName, deploymentGroupName);
            Trace.Info($"PoolId for deployment group '{deploymentGroupName}' is '{poolId}'.");
            
            return poolId;
        }

        public string GetFailedToFindPoolErrorString() => StringUtil.Loc("FailedToFindDeploymentGroup");

        public async Task<TaskAgent> UpdateAgentAsync(int poolId, TaskAgent agent, CommandSettings command)
        {
            var deploymentMachine = new DeploymentMachine() { Agent = agent };
            deploymentMachine = await _deploymentGroupServer.ReplaceDeploymentMachineAsync(_projectName, _deploymentGroupId, agent.Id, deploymentMachine);
            await GetAndAddTags(agent, command);

            return deploymentMachine.Agent;
        }

        public async Task<TaskAgent> AddAgentAsync(int poolId, TaskAgent agent, CommandSettings command)
        {
            var deploymentMachine = new DeploymentMachine(){ Agent = agent };
            deploymentMachine = await _deploymentGroupServer.AddDeploymentMachineAsync(_projectName, _deploymentGroupId, deploymentMachine);
            await GetAndAddTags(deploymentMachine.Agent, command);

            return deploymentMachine.Agent;
        }

        public Task DeleteAgentAsync(int agentPoolId, int agentId)
        {
            return _deploymentGroupServer.DeleteDeploymentMachineAsync(_projectName, agentPoolId, agentId);
        }

        public async Task TestConnectionAsync(string url, VssCredentials creds)
        {
            _term.WriteLine(StringUtil.Loc("ConnectingToServer"));
            VssConnection connection = ApiUtil.CreateConnection(new Uri(url), creds);

            // Create the connection for deployment group 
            Trace.Info("Test connection with deployment group");
            if (!_isHosted && !_collectionName.IsNullOrEmpty()) // For on-prm validate the collection by making the connection
            {
                UriBuilder uriBuilder = new UriBuilder(new Uri(url));
                uriBuilder.Path = uriBuilder.Path + "/" + _collectionName;
                Trace.Info("Tfs Collection level url to connect - {0}", uriBuilder.Uri.AbsoluteUri);
                url = uriBuilder.Uri.AbsoluteUri;
            }
            VssConnection deploymentGroupconnection = ApiUtil.CreateConnection(new Uri(url), creds);

            await _deploymentGroupServer.ConnectAsync(deploymentGroupconnection);
            Trace.Info("Connect complete for deployment group");
        }

        public void UpdateAgentSetting(AgentSettings settings)
        {
            settings.DeploymentGroupId = _deploymentGroupId;
            settings.ProjectName = _projectName;
            settings.CollectionName = _collectionName;
        }

        public async Task<TaskAgent> GetAgentAsync(int poolId, string agentName)
        {
            var machines = await _deploymentGroupServer.GetDeploymentMachinesAsync(_projectName, poolId, agentName);
            Trace.Verbose("Returns {0} machines", machines.Count);
            var machine = machines.FirstOrDefault();
            if (machine != null)
            {
                return machine.Agent;
            }

            return null;
        }

        private async Task GetAndAddTags(TaskAgent agent, CommandSettings command)
        {
            // Get and apply Tags in case agent is configured against Deployment Group
            bool needToAddTags = command.GetDeploymentGroupTagsRequired();
            while (needToAddTags)
            {
                try
                {
                    string tagString = command.GetDeploymentGroupTags();
                    Trace.Info("Given tags - {0} will be processed and added", tagString);

                    if (!string.IsNullOrWhiteSpace(tagString))
                    {
                        var tagsList =
                            tagString.Split(',').Where(s => !string.IsNullOrWhiteSpace(s))
                                .Select(s => s.Trim())
                                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

                        if (tagsList.Any())
                        {
                            Trace.Info("Adding tags - {0}", string.Join(",", tagsList.ToArray()));

                            var deploymentMachine = new DeploymentMachine()
                            {
                                Agent = agent,
                                Tags = tagsList
                            };

                            await _deploymentGroupServer.UpdateDeploymentMachinesAsync(_projectName, _deploymentGroupId,
                                           new List<DeploymentMachine>() { deploymentMachine });

                            _term.WriteLine(StringUtil.Loc("DeploymentGroupTagsAddedMsg"));
                        }
                    }
                    break;
                }
                catch (Exception e) when (!command.Unattended)
                {
                    _term.WriteError(e);
                    _term.WriteError(StringUtil.Loc("FailedToAddTags"));
                }
            }
        }

        private async Task<int> GetPoolIdAsync(string projectName, string deploymentGroupName)
        {
            ArgUtil.NotNull(_deploymentGroupServer, nameof(_deploymentGroupServer));

            var deploymentGroup = (await _deploymentGroupServer.GetDeploymentGroupsAsync(projectName, deploymentGroupName)).FirstOrDefault();

            if (deploymentGroup == null)
            {
                throw new DeploymentGroupNotFoundException(StringUtil.Loc("DeploymentGroupNotFound", deploymentGroupName));
            }

            _deploymentGroupId = deploymentGroup.Id;
            Trace.Info("Found deployment group {0} with id {1}", deploymentGroupName, deploymentGroup.Id);
            Trace.Info("Found poolId {0} for deployment group {1}", deploymentGroup.Pool.Id, deploymentGroupName);

            return deploymentGroup.Pool.Id;
        }
    }
}
