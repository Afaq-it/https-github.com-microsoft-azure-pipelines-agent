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
    public interface IConfigurationProvider : IExtension
    {
        void InitConnection(IAgentServer agentServer);

        string ConfigurationProviderType { get; }

        string GetServerUrl(CommandSettings command);

        Task<IAgentServer> TestConnectAsync(string tfsUrl, VssCredentials creds);

        Task<int> GetPoolId(CommandSettings command);

        Task<TaskAgent> UpdateAgentAsync(int poolId, TaskAgent agent);

        Task<TaskAgent> AddAgentAsync(int poolId, TaskAgent agent);

        Task DeleteAgentAsync(int agentPoolId, int agentId);

        void UpdateAgentSetting(AgentSettings settings);
    }

    public abstract class ConfigurationProvider : AgentService
    {
        public Type ExtensionType => typeof(IConfigurationProvider);
        protected ITerminal _term;
        protected IAgentServer _agentServer;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _term = hostContext.GetService<ITerminal>();
        }

        protected void InitializeServerConnection(IAgentServer agentServer)
        {
            _agentServer = agentServer;
        }
        
        protected Task<TaskAgent> UpdateAgent(int poolId, TaskAgent agent)
        {
           return _agentServer.UpdateAgentAsync(poolId, agent);
        }

        protected Task<TaskAgent> AddAgent(int poolId, TaskAgent agent)
        {
            return _agentServer.AddAgentAsync(poolId, agent);
        }

        protected Task DeleteAgent(int poolId, int agentId)
        {
            return _agentServer.DeleteAgentAsync(poolId, agentId);
        }

        protected async Task TestConnectionAsync(string url, VssCredentials creds)
        {
            _term.WriteLine(StringUtil.Loc("ConnectingToServer"));
            VssConnection connection = ApiUtil.CreateConnection(new Uri(url), creds);

            _agentServer = HostContext.CreateService<IAgentServer>();
            await _agentServer.ConnectAsync(connection);
        }
    }

    public sealed class BuildReleasesAgentConfigProvider : ConfigurationProvider, IConfigurationProvider
    {
        public string ConfigurationProviderType
            => Constants.Agent.AgentConfigurationProvider.BuildReleasesAgentConfiguration;

        public void InitConnection(IAgentServer agentServer)
        {
            InitializeServerConnection(agentServer);
        }

        public void UpdateAgentSetting(AgentSettings settings)
        {
            // No implementation required
        }

        public string GetServerUrl(CommandSettings command)
        {
            return command.GetUrl(false);
        }

        public async Task<int> GetPoolId(CommandSettings command)
        {
            int poolId = 0;
            string poolName;
            while (true)
            {
                poolName = command.GetPool();
                try
                {
                    poolId = await GetPoolIdAsync(poolName);
                }
                catch (Exception e) when (!command.Unattended)
                {
                    _term.WriteError(e);
                }

                if (poolId > 0)
                {
                    break;
                }

                _term.WriteError(StringUtil.Loc("FailedToFindPool"));
            }
            return poolId;            
        }

        public Task<TaskAgent> UpdateAgentAsync(int poolId, TaskAgent agent)
        {
            return UpdateAgent(poolId, agent);
        }

        public Task<TaskAgent> AddAgentAsync(int poolId, TaskAgent agent)
        {
            return AddAgent(poolId, agent);
        }

        public Task DeleteAgentAsync(int agentPoolId, int agentId)
        {
            return DeleteAgent(agentPoolId,agentId);
        }

        public async Task<IAgentServer> TestConnectAsync(string url, VssCredentials creds)
        {
            await TestConnectionAsync(url, creds);
            return _agentServer;
        }

        private async Task<int> GetPoolIdAsync(string poolName)
        {
            int poolId = 0;
            List<TaskAgentPool> pools = await _agentServer.GetAgentPoolsAsync(poolName);
            Trace.Verbose("Returned {0} pools", pools.Count);

            if (pools.Count == 1)
            {
                poolId = pools[0].Id;
                Trace.Info("Found pool {0} with id {1}", poolName, poolId);
            }

            return poolId;
        }

    }

    public sealed class MachineGroupAgentConfigProvider : ConfigurationProvider, IConfigurationProvider
    {
        private IAgentServer _collectionAgentServer =null;
        private string _projectName;
        private string _collectionName;
        private string _machineGroupName;
        private string _serverUrl;
        private bool _isHosted = false;

        public string ConfigurationProviderType
            => Constants.Agent.AgentConfigurationProvider.DeploymentAgentConfiguration;

        public void InitConnection(IAgentServer agentServer)
        {
            InitializeServerConnection(agentServer);
        }

        public string GetServerUrl(CommandSettings command)
        {
            _serverUrl =  command.GetUrl(true);
            Trace.Info("url - {0}", _serverUrl);

            string baseUrl = _serverUrl;
            _isHosted = UrlUtil.IsHosted(_serverUrl);

            // VSTS account url - Do validation of server Url includes project name 
            // On-prem tfs Url - Do validation of tfs Url includes collection and project name 

            Uri uri = new Uri(_serverUrl);                                   //e.g On-prem => http://myonpremtfs:8080/tfs/defaultcollection/myproject
                                                                             //e.g VSTS => https://myvstsaccount.visualstudio.com/myproject

            string urlAbsolutePath = uri.AbsolutePath;                       //e.g tfs/defaultcollection/myproject
                                                                             //e.g myproject
            string[] urlTokenParts = urlAbsolutePath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);      //e.g tfs,defaultcollection,myproject
            int tokenCount = urlTokenParts.Length;

            if (tokenCount == 0)
            {
                if (! _isHosted)
                {
                    ThrowExceptionForOnPremUrl();
                }
                else
                {
                    ThrowExceptionForVSTSUrl();
                }
            }
            
            // for onprem ensure collection/project is format
            if (! _isHosted)
            {
                Trace.Info("Provided url is for onprem tfs");
                
                if (tokenCount <= 1)
                {
                    ThrowExceptionForOnPremUrl();
                }
                _collectionName = urlTokenParts[tokenCount-2];
                _projectName = urlTokenParts[tokenCount-1];
                Trace.Info("collectionName - {0}", _collectionName);

                baseUrl = _serverUrl.Replace(_projectName, "").Replace(_collectionName, "").TrimEnd(new char[] { '/'});
            }
            else
            {
                Trace.Info("Provided url is for vsts account");
                _projectName = urlTokenParts.Last();

                baseUrl = new Uri(_serverUrl).GetLeftPart(UriPartial.Authority);
            }

            Trace.Info("projectName - {0}", _projectName);

            return baseUrl;
        }

        public async Task<IAgentServer> TestConnectAsync(string url, VssCredentials creds)
        {
            if (!_isHosted && !_collectionName.IsNullOrEmpty()) 
            {
                TestConnectionWithCollection(url, creds);   // For on-prm validate the collection by making the connection
            }

            await TestConnectionAsync(url, creds);

            return _agentServer;
        }

        public async Task<int> GetPoolId(CommandSettings command)
        {
            int poolId = 0;
            while (true)
            {
                _machineGroupName = command.GetMachineGroupName();
                try
                {
                    poolId =  await GetPoolIdAsync(_projectName, _machineGroupName);
                }
                catch (Exception e) when (!command.Unattended)
                {
                    _term.WriteError(e);
                }

                if (poolId > 0)
                {
                    break;
                }

                _term.WriteError(StringUtil.Loc("FailedToFindPool"));

                // In case of failure ensure to get the project name again
                _projectName = command.GetProjectName(_projectName);
            }
            
            return poolId;
        }

        public Task<TaskAgent> UpdateAgentAsync(int poolId, TaskAgent agent)
        {
            return UpdateAgent(poolId, agent);
            // this may have additional calls related to Machine Group
        }

        public Task<TaskAgent> AddAgentAsync(int poolId, TaskAgent agent)
        {
            return AddAgent(poolId, agent);
            // this may have additional calls related to Machine Group
        }

        public Task DeleteAgentAsync(int agentPoolId, int agentId)
        {
            return DeleteAgent(agentPoolId, agentId);
        }

        public void UpdateAgentSetting(AgentSettings settings)
        {
            settings.MachineGroupName = _machineGroupName;
            settings.ProjectName = _projectName;
        }

        private async Task<int> GetPoolIdAsync(string projectName, string machineGroupName)
        {
            int poolId = 0;

            if (_collectionAgentServer == null)
            {
                _collectionAgentServer = _agentServer;
            }

            List<TaskAgentQueue> machineGroup = await _collectionAgentServer.GetAgentQueuesAsync(projectName, machineGroupName);
            Trace.Verbose("Returned {0} machineGroup", machineGroup.Count);

            if (machineGroup.Count == 1)
            {
                int queueId = machineGroup[0].Id;
                Trace.Info("Found queue {0} with id {1}", machineGroupName, queueId);
                poolId = machineGroup[0].Pool.Id;
                Trace.Info("Found poolId {0} with queueName {1}", poolId, machineGroupName);
            }

            return poolId;
        }

        private async Task TestCollectionConnectionAsync(string url, VssCredentials creds)
        {
            _term.WriteLine(StringUtil.Loc("ConnectingToServer"));
            VssConnection connection = ApiUtil.CreateConnection(new Uri(url), creds);

            _collectionAgentServer = HostContext.CreateService<IAgentServer>();
            await _collectionAgentServer.ConnectAsync(connection);
        }

        private async void TestConnectionWithCollection(string tfsUrl, VssCredentials creds)
        {
            Trace.Info("Test connection with collection level");

            UriBuilder uriBuilder = new UriBuilder(new Uri(tfsUrl));
            uriBuilder.Path = uriBuilder.Path + "/" + _collectionName;
            Trace.Info("Tfs Collection level url to connect - {0}", uriBuilder.Uri.AbsoluteUri);

            // Validate can connect.
            await TestCollectionConnectionAsync(uriBuilder.Uri.AbsoluteUri, creds);
            Trace.Info("Connect complete.");
        }

        private void ThrowExceptionForOnPremUrl()
        {
            throw new Exception(StringUtil.Loc("UrlValidationFailedForOnPremTfs"));
        }

        private void ThrowExceptionForVSTSUrl()
        {
            throw new Exception(StringUtil.Loc("UrlValidationFailedForVSTSAccount"));
        }

    }

}
